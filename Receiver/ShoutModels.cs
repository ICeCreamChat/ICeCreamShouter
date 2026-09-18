using System.Text.Json.Serialization;

namespace CloudRemoteShouter.Receiver;

public sealed class ShoutPayload
{
    public string Type { get; set; } = "";
    public string Version { get; set; } = "";

    [JsonPropertyName("message_id")]
    public string MessageId { get; set; } = "";

    public long Timestamp { get; set; }

    [JsonPropertyName("expires_at")]
    public long ExpiresAt { get; set; }

    public string Action { get; set; } = "";
    public ShoutData Data { get; set; } = new();
}

public sealed class ShoutData
{
    [JsonPropertyName("sender_name")]
    public string SenderName { get; set; } = "";

    public string Text { get; set; } = "";

    [JsonPropertyName("tts_volume")]
    public double TtsVolume { get; set; } = 1;

    [JsonPropertyName("tts_speed")]
    public int TtsSpeed { get; set; }

    [JsonPropertyName("display_duration_sec")]
    public int DisplayDurationSec { get; set; } = 10;

    [JsonPropertyName("alert_level")]
    public string AlertLevel { get; set; } = "warning";

    [JsonPropertyName("content_type")]
    public string ContentType { get; set; } = "text";

    [JsonPropertyName("audio_url")]
    public string? AudioUrl { get; set; }

    [JsonPropertyName("audio_duration_ms")]
    public int? AudioDurationMs { get; set; }
}
