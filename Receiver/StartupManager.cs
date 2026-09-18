using Microsoft.Win32;

namespace CloudRemoteShouter.Receiver;

internal static class StartupManager
{
    private const string RunKey = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
    private const string ValueName = "ICeCreamShouter";
    private const string LegacyValueName = "CloudRemoteShouter";

    public static void SetEnabled(bool enabled)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (key is null) return;
            key.DeleteValue(LegacyValueName, false);
            if (enabled)
            {
                var path = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "ICeCreamShouter.exe");
                key.SetValue(ValueName, $"\"{path}\" --background");
            }
            else key.DeleteValue(ValueName, false);
        }
        catch { }
    }
}
