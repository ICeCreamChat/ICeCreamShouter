using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace CloudRemoteShouter.Receiver;

public sealed class AlertWindow : Window
{
    private readonly TaskCompletionSource<bool> _opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly DispatcherTimer _closeTimer;
    private readonly DispatcherTimer _topmostTimer;
    private readonly DispatcherTimer _fadeTimer;
    private readonly TextBlock _bodyBlock;
    private readonly TextBlock _title;
    private readonly TextBlock _typeLabel;
    private readonly TextBlock _countdown;
    private readonly ProgressBar _progress;
    private readonly string _alertLevel;
    private int _remainingSeconds;
    private bool _closing;
    private double _fadeTarget;
    private Action? _fadeCompleted;

    public AlertWindow(ShoutData payload)
    {
        _alertLevel = payload.AlertLevel is "info" or "urgent" ? payload.AlertLevel : "warning";
        _remainingSeconds = Math.Clamp(payload.DisplayDurationSec, 5, 60);
        SystemDecorations = SystemDecorations.None;
        WindowState = _alertLevel == "urgent" ? WindowState.FullScreen : WindowState.Normal;
        Topmost = true;
        ShowActivated = _alertLevel == "urgent";
        ShowInTaskbar = false;
        CanResize = false;
        Background = Brushes.Transparent;
        Opacity = 0;

        var palette = PaletteFor(_alertLevel);
        _typeLabel = new TextBlock
        {
            Text = palette.Label,
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(palette.Accent),
        };
        _title = new TextBlock
        {
            Text = payload.SenderName,
            FontSize = 18,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse("#1D1D1F")),
            TextWrapping = TextWrapping.Wrap,
        };
        var timestamp = new TextBlock
        {
            Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.Parse("#6E6E73")),
            Margin = new Thickness(0, 3, 0, 0),
        };
        var headerText = new StackPanel { Spacing = 0 };
        headerText.Children.Add(_typeLabel);
        headerText.Children.Add(_title);
        headerText.Children.Add(timestamp);

        _bodyBlock = new TextBlock
        {
            Text = payload.Text,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse("#1D1D1F")),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Left,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var bodyScroll = new ScrollViewer
        {
            Content = _bodyBlock,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Padding = new Thickness(0, 13, 0, 5),
        };

        _countdown = new TextBlock
        {
            Text = $"{_remainingSeconds} 秒",
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.Parse("#6E6E73")),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        _progress = new ProgressBar
        {
            Minimum = 0,
            Maximum = _remainingSeconds,
            Value = _remainingSeconds,
            Height = 3,
            Foreground = new SolidColorBrush(palette.Accent),
            Background = new SolidColorBrush(Color.Parse("#E5E5EA")),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        var close = new Button
        {
            Content = "×",
            FontSize = 24,
            Width = 38,
            Height = 38,
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.Parse("#F2F2F7")),
            Foreground = new SolidColorBrush(Color.Parse("#6E6E73")),
            BorderBrush = Brushes.Transparent,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
        };
        close.Click += (_, _) => CloseByUser();

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(headerText, 0);
        Grid.SetColumn(close, 1);
        header.Children.Add(headerText);
        header.Children.Add(close);

        var content = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto"), MinWidth = 280 };
        Grid.SetRow(header, 0);
        Grid.SetRow(bodyScroll, 1);
        Grid.SetRow(_countdown, 2);
        Grid.SetRow(_progress, 3);
        content.Children.Add(header);
        content.Children.Add(bodyScroll);
        content.Children.Add(_countdown);
        content.Children.Add(_progress);

        Content = new Border
        {
            Background = new SolidColorBrush(palette.Background),
            BorderBrush = new SolidColorBrush(palette.Border),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(_alertLevel == "urgent" ? 0 : 18),
            Padding = new Thickness(_alertLevel == "urgent" ? 64 : 24),
            Child = content,
        };

        _closeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _closeTimer.Tick += (_, _) =>
        {
            _remainingSeconds--;
            _countdown.Text = $"{Math.Max(0, _remainingSeconds)} 秒";
            _progress.Value = Math.Max(0, _remainingSeconds);
            if (_remainingSeconds <= 0)
            {
                StopSpeechOnClose = true;
                BeginClose();
            }
        };
        _topmostTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _topmostTimer.Tick += (_, _) => PlatformTopmostService.Reassert(this);
        _fadeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        _fadeTimer.Tick += (_, _) =>
        {
            var next = Opacity + (_fadeTarget > Opacity ? .12 : -.12);
            if ((_fadeTarget >= Opacity && next >= _fadeTarget) || (_fadeTarget <= Opacity && next <= _fadeTarget))
            {
                Opacity = _fadeTarget;
                _fadeTimer.Stop();
                var completed = _fadeCompleted;
                _fadeCompleted = null;
                completed?.Invoke();
            }
            else Opacity = next;
        };
        Opened += (_, _) =>
        {
            ConfigureWindow();
            PlatformTopmostService.Reassert(this);
            UpdateTextLayout();
            _closeTimer.Start();
            _topmostTimer.Start();
            FadeTo(1, null);
            _opened.TrySetResult(true);
        };
        Closed += (_, _) =>
        {
            _closeTimer.Stop();
            _topmostTimer.Stop();
            _fadeTimer.Stop();
        };
        SizeChanged += (_, _) => UpdateTextLayout();
        KeyDown += (_, args) => { if (args.Key == Avalonia.Input.Key.Escape) CloseByUser(); };
    }

    public bool StopSpeechOnClose { get; private set; }
    public bool StopAudioOnClose { get; private set; }
    public Task OpenedTask => _opened.Task;

    private void ConfigureWindow()
    {
        if (_alertLevel == "urgent")
        {
            WindowState = WindowState.FullScreen;
            return;
        }

        var screen = Screens.Primary ?? Screens.All.FirstOrDefault();
        if (screen is null) return;
        var scale = RenderScaling > 0 ? RenderScaling : 1;
        var work = screen.WorkingArea;
        var workWidth = work.Width / scale;
        var workHeight = work.Height / scale;
        Width = Math.Min(_alertLevel == "info" ? 500 : 1100, Math.Max(360, workWidth * (_alertLevel == "info" ? .34 : .8)));
        Height = _alertLevel == "info" ? Math.Min(250, Math.Max(190, workHeight * .25)) : Math.Min(310, Math.Max(220, workHeight * .32));
        var left = _alertLevel == "info" ? work.X / scale + workWidth - Width - 24 : work.X / scale + (workWidth - Width) / 2;
        var top = _alertLevel == "info" ? work.Y / scale + workHeight - Height - 24 : work.Y / scale + 24;
        Position = new PixelPoint((int)Math.Round(left * scale), (int)Math.Round(top * scale));
    }

    private void CloseByUser()
    {
        StopSpeechOnClose = true;
        StopAudioOnClose = true;
        BeginClose();
    }

    private void BeginClose()
    {
        if (_closing) return;
        _closing = true;
        _closeTimer.Stop();
        _topmostTimer.Stop();
        FadeTo(0, Close);
    }

    private void FadeTo(double target, Action? completed)
    {
        _fadeTarget = target;
        _fadeCompleted = completed;
        _fadeTimer.Start();
    }

    private void UpdateTextLayout()
    {
        var width = Math.Max(180, Bounds.Width - (_alertLevel == "urgent" ? 128 : 48));
        var height = Math.Max(100, Bounds.Height - (_alertLevel == "urgent" ? 180 : 120));
        var high = _alertLevel == "urgent" ? 96.0 : _alertLevel == "warning" ? 48.0 : 30.0;
        var low = _alertLevel == "urgent" ? 24.0 : 16.0;
        for (var i = 0; i < 14; i++)
        {
            var size = (low + high) / 2;
            var unitsPerLine = Math.Max(1, width / (size * .96));
            var lines = (_bodyBlock.Text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')
                .Sum(line => Math.Max(1, Math.Ceiling(line.Sum(CharacterWidth) / unitsPerLine)));
            if (lines * Math.Ceiling(size * 1.3) <= height * .9) low = size; else high = size;
        }
        _bodyBlock.FontSize = Math.Floor(low);
        _bodyBlock.LineHeight = Math.Ceiling(_bodyBlock.FontSize * 1.3);
    }

    private static double CharacterWidth(char value) => value <= 0x7f ? (char.IsWhiteSpace(value) ? .45 : .56) : 1;

    private static (string Label, Color Accent, Color Background, Color Border) PaletteFor(string level) => level switch
    {
        "urgent" => ("紧急通知", Color.Parse("#C92A2A"), Color.Parse("#FFF7F7"), Color.Parse("#F2B8B8")),
        "warning" => ("重要通知", Color.Parse("#B45309"), Color.Parse("#FFFBF3"), Color.Parse("#F0D19B")),
        _ => ("普通通知", Color.Parse("#0071E3"), Color.Parse("#FFFFFF"), Color.Parse("#D7E8FA")),
    };
}
