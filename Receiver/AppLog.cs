using System.Text;

namespace CloudRemoteShouter.Receiver;

internal static class AppLog
{
    private const long MaxBytes = 1_000_000;
    private static readonly object Sync = new();
    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CloudRemoteShouter",
        "logs");
    private static readonly string LogPath = Path.Combine(LogDirectory, "receiver.log");

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string message, Exception? exception = null)
    {
        var detail = exception is null ? message : $"{message}: {exception.GetType().Name}: {exception.Message}";
        Write("ERROR", detail);
    }

    private static void Write(string level, string message)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(LogDirectory);
                RotateIfNeeded();
                var safeMessage = message.Replace('\r', ' ').Replace('\n', ' ');
                File.AppendAllText(LogPath, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} [{level}] {safeMessage}{Environment.NewLine}", Encoding.UTF8);
            }
        }
        catch
        {
            // Logging must never prevent the receiver from showing a message.
        }
    }

    private static void RotateIfNeeded()
    {
        if (!File.Exists(LogPath) || new FileInfo(LogPath).Length < MaxBytes) return;
        var previous = LogPath + ".1";
        File.Move(LogPath, previous, true);
    }
}
