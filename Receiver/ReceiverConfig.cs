using System.Text.Json;
using System.Security.Cryptography;

namespace CloudRemoteShouter.Receiver;

public sealed class ReceiverConfig
{
    public string Endpoint { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public string DeviceToken { get; set; } = "";
    public string ClassId { get; set; } = "";
    public string ClassName { get; set; } = "";
    public string DeviceName { get; set; } = "教室电脑";
    public bool StartWithWindows { get; set; } = true;

    private static string PathOnDisk => Path.Combine(AppContext.BaseDirectory, "config.json");

    public static string? TryLoadDefaultEndpoint()
    {
        foreach (var path in new[]
        {
            Path.Combine(AppContext.BaseDirectory, "cloud-url.txt"),
            Path.Combine(AppContext.BaseDirectory, "receiver-defaults.json"),
        })
        {
            try
            {
                if (!File.Exists(path)) continue;
                var value = path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                    ? JsonSerializer.Deserialize<ReceiverDefaults>(File.ReadAllText(path))?.Endpoint
                    : File.ReadAllText(path).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                var endpoint = NormalizeEndpoint(value ?? "");
                if (Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) &&
                    (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)))
                {
                    return endpoint;
                }
            }
            catch
            {
                // A malformed optional defaults file should fall back to the editable setup form.
            }
        }
        return null;
    }

    public static bool TryLoad(out ReceiverConfig config)
    {
        config = new ReceiverConfig();
        try
        {
            if (!File.Exists(PathOnDisk)) return false;
            var text = File.ReadAllText(PathOnDisk);
            var loaded = JsonSerializer.Deserialize<ReceiverConfig>(text);
            if (loaded is null || string.IsNullOrWhiteSpace(loaded.Endpoint) || string.IsNullOrWhiteSpace(loaded.DeviceToken)) return false;
            loaded.DeviceToken = UnprotectToken(loaded.DeviceToken);
            if (string.IsNullOrWhiteSpace(loaded.DeviceToken)) return false;
            config = loaded;
            return true;
        }
        catch { return false; }
    }

    public static void Save(ReceiverConfig config)
    {
        var temporary = PathOnDisk + ".tmp";
        var persisted = new ReceiverConfig
        {
            Endpoint = NormalizeEndpoint(config.Endpoint),
            DeviceId = config.DeviceId,
            DeviceToken = ProtectToken(config.DeviceToken),
            ClassId = config.ClassId,
            ClassName = config.ClassName,
            DeviceName = config.DeviceName,
            StartWithWindows = config.StartWithWindows,
        };
        var json = JsonSerializer.Serialize(persisted, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(temporary, json);
        File.Move(temporary, PathOnDisk, true);
        StartupManager.SetEnabled(config.StartWithWindows);
    }

    public static string NormalizeEndpoint(string endpoint) => endpoint.Trim().TrimEnd('/');

    private static string ProtectToken(string token)
    {
        if (!OperatingSystem.IsWindows() || token.StartsWith("dpapi:", StringComparison.Ordinal)) return token;
        var protectedBytes = ProtectedData.Protect(System.Text.Encoding.UTF8.GetBytes(token), null, DataProtectionScope.CurrentUser);
        return "dpapi:" + Convert.ToBase64String(protectedBytes);
    }

    private static string UnprotectToken(string token)
    {
        if (!token.StartsWith("dpapi:", StringComparison.Ordinal)) return token;
        if (!OperatingSystem.IsWindows()) return "";
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(token[6..]), null, DataProtectionScope.CurrentUser);
            return System.Text.Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return "";
        }
    }

    private sealed class ReceiverDefaults
    {
        public string Endpoint { get; set; } = "";
    }
}
