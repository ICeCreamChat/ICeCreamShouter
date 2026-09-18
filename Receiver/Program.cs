using Avalonia;

namespace CloudRemoteShouter.Receiver;

internal static class Program
{
    private static Mutex? _singleInstance;

    [STAThread]
    public static void Main(string[] args)
    {
        _singleInstance = new Mutex(true, "CloudRemoteShouter.Receiver.SingleInstance", out var isFirstInstance);
        if (!isFirstInstance) return;
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        _singleInstance.ReleaseMutex();
        _singleInstance.Dispose();
    }

    private static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
    }
}
