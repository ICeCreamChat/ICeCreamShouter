using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CloudRemoteShouter.Receiver;

public sealed class ReceiverUpdateService
{
    private const long MaximumDownloadBytes = 250L * 1024 * 1024;
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromMinutes(10) };
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static Version CurrentVersion => typeof(ReceiverUpdateService).Assembly.GetName().Version ?? new Version(1, 0, 0);
    public static string CurrentVersionText => $"{CurrentVersion.Major}.{CurrentVersion.Minor}.{CurrentVersion.Build}";

    public async Task<ReceiverUpdateResult> CheckAndDownloadAsync(
        ReceiverConfig config,
        IProgress<int>? progress,
        CancellationToken token)
    {
        var baseUri = new Uri(config.Endpoint.TrimEnd('/') + "/", UriKind.Absolute);
        var checkUri = new Uri(baseUri,
            $"api/receiver/update?deviceId={Uri.EscapeDataString(config.DeviceId)}&currentVersion={Uri.EscapeDataString(CurrentVersionText)}");
        using var checkRequest = CreateRequest(HttpMethod.Get, checkUri, config.DeviceToken);
        using var checkResponse = await Client.SendAsync(checkRequest, HttpCompletionOption.ResponseHeadersRead, token);
        if (checkResponse.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            throw new InvalidOperationException("教室绑定已失效，请联系管理员重新绑定。");
        checkResponse.EnsureSuccessStatusCode();
        var manifest = await JsonSerializer.DeserializeAsync<ReceiverUpdateManifest>(
            await checkResponse.Content.ReadAsStreamAsync(token), JsonOptions, token)
            ?? throw new InvalidOperationException("服务器返回的更新信息无法识别。");

        if (!manifest.Available) return ReceiverUpdateResult.Latest(CurrentVersionText);
        if (!Version.TryParse(manifest.Version, out var remoteVersion) || remoteVersion <= CurrentVersion)
            return ReceiverUpdateResult.Latest(CurrentVersionText);
        if (manifest.Size is < 1 or > MaximumDownloadBytes)
            throw new InvalidOperationException("服务器提供的更新文件大小不正确。");
        if (manifest.Sha256.Length != 64 || manifest.Sha256.Any(value => !Uri.IsHexDigit(value)))
            throw new InvalidOperationException("服务器提供的更新校验信息不正确。");

        var downloadUri = new Uri(baseUri, manifest.DownloadUrl);
        if (!SameOrigin(baseUri, downloadUri) ||
            (downloadUri.Scheme != Uri.UriSchemeHttps && !(downloadUri.Scheme == Uri.UriSchemeHttp && downloadUri.IsLoopback)))
            throw new InvalidOperationException("服务器提供的更新地址不安全。");

        var updateDirectory = Path.Combine(AppContext.BaseDirectory, ".updates");
        Directory.CreateDirectory(updateDirectory);
        var temporaryPath = Path.Combine(updateDirectory, $"ICeCreamShouter-{manifest.Version}.download");
        var readyPath = Path.Combine(updateDirectory, $"ICeCreamShouter-{manifest.Version}.exe");
        TryDelete(temporaryPath);

        try
        {
            using var downloadRequest = CreateRequest(HttpMethod.Get, downloadUri, config.DeviceToken);
            using var response = await Client.SendAsync(downloadRequest, HttpCompletionOption.ResponseHeadersRead, token);
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                throw new InvalidOperationException("下载更新时教室身份验证失败。");
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long contentLength && contentLength != manifest.Size)
                throw new InvalidOperationException("更新文件长度与服务器记录不一致。");

            await using var source = await response.Content.ReadAsStreamAsync(token);
            await using var destination = new FileStream(
                temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 128,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[1024 * 128];
            long total = 0;
            while (true)
            {
                var read = await source.ReadAsync(buffer, token);
                if (read == 0) break;
                total += read;
                if (total > manifest.Size || total > MaximumDownloadBytes)
                    throw new InvalidOperationException("更新文件超过服务器声明的大小。");
                await destination.WriteAsync(buffer.AsMemory(0, read), token);
                hash.AppendData(buffer, 0, read);
                progress?.Report((int)Math.Clamp(total * 100 / manifest.Size, 0, 100));
            }
            await destination.FlushAsync(token);
            if (total != manifest.Size)
                throw new InvalidOperationException("更新下载不完整，请稍后重试。");
            var actualHash = Convert.ToHexString(hash.GetHashAndReset());
            if (!actualHash.Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("更新文件校验失败，请稍后重试。");

            File.Move(temporaryPath, readyPath, true);
            return ReceiverUpdateResult.Downloaded(manifest.Version, readyPath);
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
    }

    public bool TryLaunchInstaller(ReceiverUpdateResult update, out string error)
    {
        error = "";
        if (!update.UpdateAvailable || string.IsNullOrWhiteSpace(update.DownloadedPath))
        {
            error = "没有可安装的更新。";
            return false;
        }

        var currentExecutable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(currentExecutable) || !File.Exists(currentExecutable))
        {
            error = "无法确定当前程序位置。";
            return false;
        }

        try
        {
            var updateDirectory = Path.Combine(AppContext.BaseDirectory, ".updates");
            Directory.CreateDirectory(updateDirectory);
            var scriptPath = Path.Combine(updateDirectory, "apply-update.ps1");
            var backupPath = currentExecutable + ".previous";
            var markerPath = Path.Combine(updateDirectory, $"started-{update.Version}.ok");
            File.WriteAllText(scriptPath, UpdateScript, new UTF8Encoding(false));
            TryDelete(markerPath);

            var start = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            foreach (var value in new[]
            {
                "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", scriptPath,
                Environment.ProcessId.ToString(), currentExecutable, update.DownloadedPath, backupPath, markerPath,
            }) start.ArgumentList.Add(value);
            Process.Start(start);
            return true;
        }
        catch (Exception exception)
        {
            AppLog.Error("Could not start receiver updater", exception);
            error = "无法启动更新程序，请稍后重试。";
            return false;
        }
    }

    public static void ConfirmUpdatedLaunch()
    {
        const string prefix = "--update-marker=";
        var argument = Environment.GetCommandLineArgs().FirstOrDefault(value => value.StartsWith(prefix, StringComparison.Ordinal));
        if (argument is null) return;
        var requested = argument[prefix.Length..].Trim('"');
        try
        {
            var marker = Path.GetFullPath(requested);
            var updateDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".updates"));
            if (!marker.StartsWith(updateDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return;
            Directory.CreateDirectory(updateDirectory);
            File.WriteAllText(marker, DateTimeOffset.UtcNow.ToString("O"));
        }
        catch (Exception exception)
        {
            AppLog.Error("Could not confirm updated launch", exception);
        }
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, Uri uri, string token)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static bool SameOrigin(Uri left, Uri right) =>
        left.Scheme.Equals(right.Scheme, StringComparison.OrdinalIgnoreCase) &&
        left.Host.Equals(right.Host, StringComparison.OrdinalIgnoreCase) && left.Port == right.Port;

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private const string UpdateScript = """
param(
  [int]$OldProcessId,
  [string]$CurrentExecutable,
  [string]$DownloadedExecutable,
  [string]$BackupExecutable,
  [string]$MarkerPath
)
$ErrorActionPreference = 'Stop'
try { Wait-Process -Id $OldProcessId -Timeout 120 -ErrorAction SilentlyContinue } catch { }
$newProcess = $null
try {
  Remove-Item -LiteralPath $MarkerPath -Force -ErrorAction SilentlyContinue
  Remove-Item -LiteralPath $BackupExecutable -Force -ErrorAction SilentlyContinue
  Move-Item -LiteralPath $CurrentExecutable -Destination $BackupExecutable -Force
  Move-Item -LiteralPath $DownloadedExecutable -Destination $CurrentExecutable -Force
  $argument = '--update-marker="' + $MarkerPath + '"'
  $newProcess = Start-Process -FilePath $CurrentExecutable -ArgumentList $argument -PassThru
  $started = $false
  for ($index = 0; $index -lt 60; $index++) {
    if (Test-Path -LiteralPath $MarkerPath) { $started = $true; break }
    if ($newProcess.HasExited) { break }
    Start-Sleep -Milliseconds 500
    $newProcess.Refresh()
  }
  if (-not $started) { throw 'Updated receiver did not confirm startup.' }
  Remove-Item -LiteralPath $BackupExecutable -Force -ErrorAction SilentlyContinue
} catch {
  if ($newProcess -and -not $newProcess.HasExited) { Stop-Process -Id $newProcess.Id -Force -ErrorAction SilentlyContinue }
  Remove-Item -LiteralPath $CurrentExecutable -Force -ErrorAction SilentlyContinue
  if (Test-Path -LiteralPath $BackupExecutable) {
    Move-Item -LiteralPath $BackupExecutable -Destination $CurrentExecutable -Force
    Start-Process -FilePath $CurrentExecutable
  }
} finally {
  Remove-Item -LiteralPath $DownloadedExecutable -Force -ErrorAction SilentlyContinue
  Remove-Item -LiteralPath $MarkerPath -Force -ErrorAction SilentlyContinue
  Start-Sleep -Seconds 1
  Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue
}
""";

    private sealed class ReceiverUpdateManifest
    {
        public bool Available { get; set; }
        public string Version { get; set; } = "";
        public string DownloadUrl { get; set; } = "";
        public string Sha256 { get; set; } = "";
        public long Size { get; set; }
    }
}

public sealed record ReceiverUpdateResult(bool UpdateAvailable, string Version, string? DownloadedPath)
{
    public static ReceiverUpdateResult Latest(string version) => new(false, version, null);
    public static ReceiverUpdateResult Downloaded(string version, string path) => new(true, version, path);
}
