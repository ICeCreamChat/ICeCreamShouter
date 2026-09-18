using System.Net.WebSockets;
using System.Text.Json;
using Avalonia.Threading;

namespace CloudRemoteShouter.Receiver;

public sealed class CloudReceiverService
{
    private readonly SpeechService _speech;
    private readonly AudioPlaybackService _audio = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly HashSet<string> _recentIds = new(StringComparer.Ordinal);
    private readonly Queue<string> _recentOrder = new();
    private Task? _connectionTask;
    private CancellationTokenSource? _connectionCancellation;
    private AlertWindow? _currentAlert;

    public CloudReceiverService(SpeechService speech) => _speech = speech;
    public event EventHandler<ReceiverConnectionState>? ConnectionStateChanged;

    public Task StartAsync(ReceiverConfig config)
    {
        _connectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _connectionTask = ConnectLoopAsync(config, _connectionCancellation.Token);
        return Task.CompletedTask;
    }

    public async Task RestartAsync(ReceiverConfig config)
    {
        await StopConnectionAsync();
        await StartAsync(config);
    }

    public async Task StopAsync()
    {
        _lifetime.Cancel();
        await StopConnectionAsync();
        await Dispatcher.UIThread.InvokeAsync(() => { _currentAlert?.Close(); _speech.Stop(); _audio.Stop(); });
        _audio.Dispose();
    }

    public Task ShowTestAsync(string alertLevel = "info")
    {
        var level = alertLevel is "info" or "warning" or "urgent" ? alertLevel : "info";
        var duration = level switch { "urgent" => 20, "warning" => 12, _ => 8 };
        return DisplayAsync(new ShoutPayload
        {
            Data = new ShoutData
            {
                SenderName = "本机测试",
                Text = $"这是{level switch { "urgent" => "紧急", "warning" => "重要", _ => "普通" }}通知的显示预览。",
                AlertLevel = level,
                DisplayDurationSec = duration,
            },
        }, null, CancellationToken.None);
    }

    public async Task PlayTestAudioAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _currentAlert?.Close();
            _currentAlert = null;
            _speech.Stop();
            _audio.Stop();
        });
        await _audio.PlayTestToneAsync(CancellationToken.None);
    }

    private async Task ConnectLoopAsync(ReceiverConfig config, CancellationToken token)
    {
        var delaySeconds = 1;
        while (!token.IsCancellationRequested)
        {
            try
            {
                SetState(false, "正在连接云端");
                using var socket = new ClientWebSocket();
                using var sendLock = new SemaphoreSlim(1, 1);
                socket.Options.SetRequestHeader("Authorization", $"Bearer {config.DeviceToken}");
                var endpoint = new UriBuilder(config.Endpoint)
                {
                    Scheme = config.Endpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws",
                    Path = "/ws/device",
                    Query = $"deviceId={Uri.EscapeDataString(config.DeviceId)}",
                }.Uri;
                await socket.ConnectAsync(endpoint, token);
                delaySeconds = 1;
                AppLog.Info($"Connected for class {config.ClassName}");
                SetState(true, $"已连接：{config.ClassName}");
                using var connection = CancellationTokenSource.CreateLinkedTokenSource(token);
                var receive = ReceiveAsync(socket, sendLock, config, connection.Token);
                var heartbeat = HeartbeatAsync(socket, sendLock, connection.Token);
                await Task.WhenAny(receive, heartbeat);
                connection.Cancel();
                socket.Abort();
                try { await Task.WhenAll(receive, heartbeat); }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                AppLog.Error("Cloud connection failed", exception);
                SetState(false, "连接中断，正在自动重连；持续失败请检查绑定");
            }
            if (token.IsCancellationRequested) break;
            SetState(false, $"断线，{delaySeconds} 秒后自动重连");
            try { await Task.Delay(TimeSpan.FromSeconds(delaySeconds), token); }
            catch (OperationCanceledException) { break; }
            delaySeconds = Math.Min(60, delaySeconds * 2);
        }
        SetState(false, "已停止连接");
    }

    private static async Task HeartbeatAsync(ClientWebSocket socket, SemaphoreSlim sendLock, CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(20));
        while (await timer.WaitForNextTickAsync(token))
            await SendAsync(socket, sendLock, new { type = "heartbeat" }, token);
    }

    private async Task ReceiveAsync(ClientWebSocket socket, SemaphoreSlim sendLock, ReceiverConfig config, CancellationToken token)
    {
        var buffer = new byte[8192];
        while (!token.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            using var message = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, token);
                if (result.MessageType == WebSocketMessageType.Close) return;
                if (result.MessageType != WebSocketMessageType.Text || message.Length + result.Count > 256 * 1024) return;
                message.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            ShoutPayload? payload;
            try { payload = JsonSerializer.Deserialize<ShoutPayload>(message.ToArray(), JsonOptions); }
            catch (JsonException) { continue; }
            if (!TryAccept(payload) || payload is null) continue;
            await AckAsync(socket, sendLock, payload.MessageId, "received", null, token);
            try
            {
                var audioTask = await DisplayAsync(payload, config, token);
                if (audioTask is null)
                {
                    await AckAsync(socket, sendLock, payload.MessageId, "displayed", null, token);
                }
                else
                {
                    _ = FinishAudioAsync(socket, sendLock, payload.MessageId, audioTask, token);
                }
            }
            catch (Exception exception)
            {
                AppLog.Error("Message display failed", exception);
                await AckAsync(socket, sendLock, payload.MessageId, "failed", "教室端无法显示消息", token);
            }
        }
    }

    private bool TryAccept(ShoutPayload? payload)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (payload is null || payload.Type != "shout" || payload.Version != "1.0" || payload.Action != "shout" || payload.Data is null) return false;
        if (string.IsNullOrWhiteSpace(payload.MessageId) || payload.MessageId.Length > 100 || payload.ExpiresAt <= now) return false;
        if (payload.Timestamp > now + 300_000 || payload.ExpiresAt > now + 300_000) return false;
        if (string.IsNullOrWhiteSpace(payload.Data.Text) || payload.Data.Text.Length > 500) return false;
        if (string.IsNullOrWhiteSpace(payload.Data.SenderName) || payload.Data.SenderName.Length > 80) return false;
        if (payload.Data.AlertLevel is not ("info" or "warning" or "urgent")) return false;
        if (!double.IsFinite(payload.Data.TtsVolume) || payload.Data.TtsVolume is < 0 or > 1 || payload.Data.TtsSpeed is < -10 or > 10) return false;
        if (payload.Data.DisplayDurationSec is < 5 or > 60 || !_recentIds.Add(payload.MessageId)) return false;
        if (payload.Data.ContentType == "audio")
        {
            if (string.IsNullOrWhiteSpace(payload.Data.AudioUrl) || !payload.Data.AudioUrl.StartsWith("/api/shouts/", StringComparison.Ordinal) ||
                payload.Data.AudioDurationMs is < 500 or > 30_000) return false;
        }
        else if (payload.Data.ContentType != "text") return false;
        _recentOrder.Enqueue(payload.MessageId);
        while (_recentOrder.Count > 50) _recentIds.Remove(_recentOrder.Dequeue());
        return true;
    }

    private async Task<Task?> DisplayAsync(ShoutPayload payload, ReceiverConfig? config, CancellationToken token)
    {
        AlertWindow? openedWindow = null;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _speech.Stop();
            _audio.Stop();
            _currentAlert?.Close();
            var window = new AlertWindow(payload.Data);
            _currentAlert = window;
            window.Closed += (_, _) =>
            {
                if (window.StopSpeechOnClose) _speech.Stop();
                if (window.StopAudioOnClose) _audio.Stop();
                if (ReferenceEquals(_currentAlert, window)) _currentAlert = null;
            };
            window.Show();
            openedWindow = window;
        });
        if (openedWindow is null) throw new InvalidOperationException("The alert window could not be created.");
        await openedWindow.OpenedTask;
        if (payload.Data.AlertLevel == "info") return null;
        if (payload.Data.ContentType == "audio")
        {
            if (config is null) throw new InvalidOperationException("语音预览缺少设备配置。");
            var audioUri = new Uri(new Uri(config.Endpoint.TrimEnd('/') + "/", UriKind.Absolute), payload.Data.AudioUrl!.TrimStart('/'));
            var authenticatedAudioUri = new UriBuilder(audioUri)
            {
                Query = $"deviceId={Uri.EscapeDataString(config.DeviceId)}",
            }.Uri;
            return _audio.PlayAsync(authenticatedAudioUri, config.DeviceToken, token);
        }
        _speech.SpeakLatest(payload.Data.Text, payload.Data.TtsSpeed, payload.Data.TtsVolume);
        return null;
    }

    private async Task FinishAudioAsync(ClientWebSocket socket, SemaphoreSlim sendLock, string messageId, Task audioTask, CancellationToken token)
    {
        try
        {
            await audioTask;
            await AckAsync(socket, sendLock, messageId, "displayed", null, token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            await AckAsync(socket, sendLock, messageId, "failed", "语音播放被新通知中断", token);
        }
        catch (Exception exception)
        {
            AppLog.Error("Voice playback failed", exception);
            try { await AckAsync(socket, sendLock, messageId, "failed", "教室端无法播放语音", token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        }
    }

    private async Task StopConnectionAsync()
    {
        _connectionCancellation?.Cancel();
        if (_connectionTask is not null)
        {
            try { await _connectionTask; } catch (OperationCanceledException) { }
        }
        _connectionCancellation?.Dispose();
        _connectionCancellation = null;
        _connectionTask = null;
    }

    private static Task AckAsync(ClientWebSocket socket, SemaphoreSlim sendLock, string id, string status, string? detail, CancellationToken token) =>
        SendAsync(socket, sendLock, new { type = "ack", message_id = id, status, detail }, token);

    private static async Task SendAsync(ClientWebSocket socket, SemaphoreSlim sendLock, object value, CancellationToken token)
    {
        await sendLock.WaitAsync(token);
        try { await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions), WebSocketMessageType.Text, true, token); }
        finally { sendLock.Release(); }
    }

    private void SetState(bool connected, string text) => ConnectionStateChanged?.Invoke(this, new ReceiverConnectionState(connected, text));
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}

public sealed record ReceiverConnectionState(bool Connected, string Text);
