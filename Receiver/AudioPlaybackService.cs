using System.Text;
using NAudio.Wave;

namespace CloudRemoteShouter.Receiver;

/// <summary>Downloads and plays one temporary classroom voice message at a time.</summary>
public sealed class AudioPlaybackService : IDisposable
{
    private const long MaximumBytes = 4L * 1024 * 1024;
    private const double MinimumDurationSeconds = .5;
    private const double MaximumDurationSeconds = 30;
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(60) };
    private readonly object _sync = new();
    private CancellationTokenSource? _playCancellation;
    private IWavePlayer? _output;
    private bool _disposed;

    public async Task PlayAsync(Uri uri, string deviceToken, CancellationToken cancellationToken)
    {
        if (!uri.IsAbsoluteUri) throw new InvalidOperationException("语音地址无效。");
        var temporaryPath = Path.Combine(Path.GetTempPath(), $"ICeCreamShouter-{Guid.NewGuid():N}.wav");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        RegisterPlayback(linked);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", deviceToken);
            using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token);
            AppLog.Info($"Audio download response {(int)response.StatusCode}, contentType={response.Content.Headers.ContentType?.MediaType ?? "unknown"}, contentLength={response.Content.Headers.ContentLength?.ToString() ?? "unknown"}");
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long length && (length < 44 || length > MaximumBytes))
                throw new InvalidOperationException("语音文件大小不正确。");
            await using (var source = await response.Content.ReadAsStreamAsync(linked.Token))
            await using (var destination = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous))
            {
                var buffer = new byte[64 * 1024];
                long total = 0;
                while (true)
                {
                    var count = await source.ReadAsync(buffer, linked.Token);
                    if (count == 0) break;
                    total += count;
                    if (total > MaximumBytes) throw new InvalidOperationException("语音文件超过大小限制。");
                    await destination.WriteAsync(buffer.AsMemory(0, count), linked.Token);
                }
                if (total < 44) throw new InvalidOperationException("语音文件内容不完整。");
            }

            await PlayFileAsync(temporaryPath, linked.Token);
        }
        finally
        {
            ClearPlayback(linked);
            TryDelete(temporaryPath);
        }
    }

    /// <summary>Plays a short locally generated tone so a classroom can verify its speakers without the cloud.</summary>
    public async Task PlayTestToneAsync(CancellationToken cancellationToken)
    {
        var temporaryPath = Path.Combine(Path.GetTempPath(), $"ICeCreamShouter-test-{Guid.NewGuid():N}.wav");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        RegisterPlayback(linked);
        try
        {
            WriteTestTone(temporaryPath);
            await PlayFileAsync(temporaryPath, linked.Token);
        }
        finally
        {
            ClearPlayback(linked);
            TryDelete(temporaryPath);
        }
    }

    private async Task PlayFileAsync(string path, CancellationToken token)
    {
        using var reader = new WaveFileReader(path);
        ValidateWave(reader);
        try
        {
            await PlayWithOutputAsync(reader, () => new WaveOutEvent(), token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception firstException)
        {
            // Some Windows audio drivers cannot open WaveOutEvent's callback worker.
            AppLog.Error("WaveOutEvent playback failed; trying WaveOut", firstException);
            reader.Position = 0;
            await PlayWithOutputAsync(reader, () => new WaveOut(), token);
        }
    }

    private async Task PlayWithOutputAsync(WaveStream reader, Func<IWavePlayer> createOutput, CancellationToken token)
    {
        using var output = createOutput();
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        output.PlaybackStopped += (_, args) =>
        {
            if (args.Exception is not null) stopped.TrySetException(args.Exception);
            else stopped.TrySetResult();
        };
        output.Init(reader);
        lock (_sync)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(AudioPlaybackService));
            _output = output;
        }
        using var registration = token.Register(() =>
        {
            try { output.Stop(); } catch { /* Playback is already stopping. */ }
        });
        try
        {
            output.Play();
            await stopped.Task.WaitAsync(token);
            token.ThrowIfCancellationRequested();
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_output, output)) _output = null;
            }
        }
    }

    private static void ValidateWave(WaveFileReader reader)
    {
        var format = reader.WaveFormat;
        var duration = reader.TotalTime.TotalSeconds;
        if (format.Encoding != WaveFormatEncoding.Pcm || format.Channels != 1 || format.BitsPerSample != 16 ||
            format.SampleRate is not (8000 or 16000 or 22050 or 44100 or 48000) ||
            duration < MinimumDurationSeconds || duration > MaximumDurationSeconds)
        {
            throw new InvalidOperationException("语音格式不受支持，必须是 0.5 至 30 秒的单声道 16 位 WAV。");
        }
        AppLog.Info($"Audio WAV validated: {format.SampleRate}Hz, {format.Channels}ch, {format.BitsPerSample}bit, {duration:F1}s, {reader.Length} bytes");
    }

    private static void WriteTestTone(string path)
    {
        const int sampleRate = 16_000;
        const int durationMs = 800;
        const int frequency = 660;
        var sampleCount = sampleRate * durationMs / 1000;
        var dataBytes = sampleCount * sizeof(short);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: false);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataBytes);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate * sizeof(short));
        writer.Write((short)sizeof(short));
        writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataBytes);
        for (var index = 0; index < sampleCount; index++)
        {
            var envelope = Math.Min(1, Math.Min(index / 400d, (sampleCount - index) / 400d));
            writer.Write((short)(Math.Sin(2 * Math.PI * frequency * index / sampleRate) * 5000 * envelope));
        }
    }

    private void RegisterPlayback(CancellationTokenSource cancellation)
    {
        lock (_sync)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(AudioPlaybackService));
            _playCancellation = cancellation;
        }
    }

    private void ClearPlayback(CancellationTokenSource cancellation)
    {
        lock (_sync)
        {
            if (ReferenceEquals(_playCancellation, cancellation)) _playCancellation = null;
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* Temporary files are also cleared by Windows. */ }
    }

    public void Stop()
    {
        CancellationTokenSource? cancellation;
        IWavePlayer? output;
        lock (_sync)
        {
            cancellation = _playCancellation;
            output = _output;
        }
        try { cancellation?.Cancel(); } catch (ObjectDisposedException) { }
        try { output?.Stop(); } catch { /* Playback is already stopped. */ }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
        }
        Stop();
    }
}
