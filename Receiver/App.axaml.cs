using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;

namespace CloudRemoteShouter.Receiver;

public sealed class App : Application
{
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private CloudReceiverService? _receiver;
    private readonly ReceiverUpdateService _updates = new();
    private SpeechService? _speech;
    private TrayIcon? _tray;
    private NativeMenuItem? _statusMenuItem;
    private NativeMenuItem? _updateMenuItem;
    private Bitmap? _trayIconBitmap;
    private EnrollmentWindow? _enrollmentWindow;
    private CancellationTokenSource? _automaticUpdateCancellation;
    private int _updateInProgress;
    private bool _exitRequested;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            base.OnFrameworkInitializationCompleted();
            return;
        }

        _desktop = desktop;
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        _speech = new SpeechService();
        _receiver = new CloudReceiverService(_speech);
        _receiver.ConnectionStateChanged += ReceiverOnConnectionStateChanged;
        CreateTray();
        ReceiverUpdateService.ConfirmUpdatedLaunch();
        desktop.Exit += async (_, _) => await ShutdownAsync();

        if (ReceiverConfig.TryLoad(out var config))
        {
            StartupManager.SetEnabled(config.StartWithWindows);
            _ = _receiver.StartAsync(config);
            StartAutomaticUpdates(config);
        }
        else
        {
            ShowEnrollment();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void CreateTray()
    {
        _trayIconBitmap = new Bitmap(AppIconFactory.CreateStream());
        var menu = new NativeMenu();
        _statusMenuItem = new NativeMenuItem { Header = "状态：等待连接", Icon = _trayIconBitmap };
        _statusMenuItem.Click += (_, _) => ShowEnrollment();
        menu.Items.Add(_statusMenuItem);
        var setup = new NativeMenuItem { Header = "打开绑定设置" };
        setup.Click += (_, _) => ShowEnrollment();
        menu.Items.Add(setup);
        var test = new NativeMenuItem { Header = "本机测试" };
        test.Click += async (_, _) => await TestAsync();
        menu.Items.Add(test);
        _updateMenuItem = new NativeMenuItem { Header = $"检查更新（当前 {ReceiverUpdateService.CurrentVersionText}）" };
        _updateMenuItem.Click += async (_, _) => await CheckForUpdateAsync(true);
        menu.Items.Add(_updateMenuItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        var exit = new NativeMenuItem { Header = "退出" };
        exit.Click += async (_, _) => await ExitAsync();
        menu.Items.Add(exit);
        _tray = new TrayIcon { Icon = new WindowIcon(AppIconFactory.CreateStream()), ToolTipText = "ICeCream Shouter", Menu = menu, IsVisible = true };
        _tray.Clicked += (_, _) => ShowEnrollment();
    }

    private void ShowEnrollment()
    {
        if (_enrollmentWindow is not null)
        {
            _enrollmentWindow.Activate();
            return;
        }

        _enrollmentWindow = new EnrollmentWindow(ReceiverConfig.TryLoad(out var config) ? config : null);
        _enrollmentWindow.UpdateRequested += async (_, _) => await CheckForUpdateAsync(true);
        _enrollmentWindow.PreviewRequested += level => _ = _receiver?.ShowTestAsync(level);
        _enrollmentWindow.AudioTestRequested += async (_, _) => await TestAudioAsync();
        _enrollmentWindow.Saved += async (_, saved) =>
        {
            await _receiver!.RestartAsync(saved);
            StartAutomaticUpdates(saved);
            _enrollmentWindow?.Close();
        };
        _enrollmentWindow.Closed += (_, _) => _enrollmentWindow = null;
        _enrollmentWindow.Show();
        _enrollmentWindow.Activate();
    }

    private async Task TestAsync()
    {
        if (_receiver is null) return;
        await _receiver.ShowTestAsync();
    }

    private async Task TestAudioAsync()
    {
        if (_receiver is null || _enrollmentWindow is null) return;
        _enrollmentWindow.SetAudioTestState("正在播放测试音…", true);
        try
        {
            await _receiver.PlayTestAudioAsync();
            _enrollmentWindow?.SetAudioTestState("测试音播放完成；如果没有听到，请检查 Windows 音量和默认扬声器。", false);
        }
        catch (Exception exception)
        {
            AppLog.Error("Local audio test failed", exception);
            _enrollmentWindow?.SetAudioTestState($"本机声音测试失败：{exception.Message}", false);
        }
    }

    private Task ExitAsync()
    {
        if (_exitRequested) return Task.CompletedTask;
        _exitRequested = true;
        _desktop?.TryShutdown();
        return Task.CompletedTask;
    }

    private async Task ShutdownAsync()
    {
        _automaticUpdateCancellation?.Cancel();
        _automaticUpdateCancellation?.Dispose();
        _tray?.Dispose();
        _trayIconBitmap?.Dispose();
        if (_receiver is not null) await _receiver.StopAsync();
        _speech?.Dispose();
    }

    private void StartAutomaticUpdates(ReceiverConfig config)
    {
        _automaticUpdateCancellation?.Cancel();
        _automaticUpdateCancellation?.Dispose();
        _automaticUpdateCancellation = new CancellationTokenSource();
        var token = _automaticUpdateCancellation.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(Random.Shared.Next(2, 16)), token);
                while (!token.IsCancellationRequested)
                {
                    await CheckForUpdateAsync(false, config, token);
                    await Task.Delay(TimeSpan.FromHours(6), token);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        }, token);
    }

    private async Task CheckForUpdateAsync(bool userInitiated, ReceiverConfig? knownConfig = null, CancellationToken token = default)
    {
        if (Interlocked.Exchange(ref _updateInProgress, 1) != 0)
        {
            if (userInitiated) SetUpdateState("正在检查更新，请稍候…", true);
            return;
        }

        try
        {
            var config = knownConfig;
            if (config is null && !ReceiverConfig.TryLoad(out config))
            {
                if (userInitiated) SetUpdateState("请先完成教室绑定，再检查更新。", false);
                return;
            }

            SetUpdateState("正在检查更新…", true);
            var progress = new Progress<int>(value => SetUpdateState($"正在下载更新：{value}%", true));
            var result = await _updates.CheckAndDownloadAsync(config, progress, token);
            if (!result.UpdateAvailable)
            {
                SetUpdateState($"当前已是最新版本 {ReceiverUpdateService.CurrentVersionText}", false);
                return;
            }

            SetUpdateState($"新版 {result.Version} 已下载，程序即将自动重启…", true);
            if (!_updates.TryLaunchInstaller(result, out var error))
            {
                SetUpdateState(error, false);
                return;
            }
            await Task.Delay(500, token);
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                _exitRequested = true;
                _desktop?.TryShutdown();
            });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            AppLog.Error("Receiver update check failed", exception);
            SetUpdateState(userInitiated ? $"更新失败：{exception.Message}" : $"检查更新失败，稍后自动重试", false);
        }
        finally
        {
            Interlocked.Exchange(ref _updateInProgress, 0);
        }
    }

    private void SetUpdateState(string text, bool checking)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (_updateMenuItem is not null)
            {
                _updateMenuItem.Header = checking ? text : $"检查更新（{text}）";
                _updateMenuItem.IsEnabled = !checking;
            }
            _enrollmentWindow?.SetUpdateState(text, checking);
        });
    }

    private void ReceiverOnConnectionStateChanged(object? sender, ReceiverConnectionState state)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (_statusMenuItem is not null) _statusMenuItem.Header = $"状态：{state.Text}";
            _tray!.ToolTipText = state.Text;
            _enrollmentWindow?.SetConnectionState(state);
        });
    }
}
