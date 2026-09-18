using System.Net.Http.Json;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace CloudRemoteShouter.Receiver;

public sealed class EnrollmentWindow : Window
{
    private readonly ReceiverConfig? _existing;
    private readonly TextBox _endpoint = new();
    private readonly TextBox _deviceName = new();
    private readonly TextBox _code = new();
    private readonly CheckBox _startup = new();
    private readonly TextBlock _connection = new();
    private readonly TextBlock _updateStatus = new();
    private readonly TextBlock _error = new();
    private readonly Button _save = new();
    private readonly Button _checkUpdate = new();
    private readonly Button _audioTest = new();
    private readonly TextBlock _audioTestStatus = new();
    private readonly bool _endpointWasPreconfigured;

    public EnrollmentWindow(ReceiverConfig? existing)
    {
        _existing = existing;
        Title = "教室接收端设置";
        Width = 580;
        MinWidth = 420;
        Height = 660;
        MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        CanResize = true;
        Background = new SolidColorBrush(Color.Parse("#F5F5F7"));

        var title = new TextBlock { Text = existing is null ? "绑定教室电脑" : "教室电脑设置", FontSize = 28, FontWeight = FontWeight.Bold, Foreground = new SolidColorBrush(Color.Parse("#1D1D1F")) };
        var description = new TextBlock
        {
            Text = "由 ICe 在管理网页中创建班级并生成绑定码，然后在这里输入。绑定完成后，本程序会在系统托盘后台运行。",
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.Parse("#6E6E73")),
            LineHeight = 25,
        };
        var classStatus = new TextBlock
        {
            Text = existing is null ? "当前尚未绑定班级" : $"当前班级：{existing.ClassName}\n设备名称：{existing.DeviceName}",
            FontSize = 17,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        };
        var statusPanel = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#FFFFFF")),
            BorderBrush = new SolidColorBrush(Color.Parse("#D9D9DE")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(16),
            Child = classStatus,
        };

        var defaultEndpoint = existing?.Endpoint ?? ReceiverConfig.TryLoadDefaultEndpoint();
        _endpointWasPreconfigured = existing is null && !string.IsNullOrWhiteSpace(defaultEndpoint);
        _endpoint.Text = defaultEndpoint ?? "";
        _endpoint.Watermark = "例如：https://icecreamchat.top";
        _deviceName.Text = existing?.DeviceName ?? Environment.MachineName;
        _deviceName.MaxLength = 80;
        _code.Watermark = existing is null ? "输入网页生成的一次性绑定码" : "重新绑定其他班级时才需要填写";
        _code.MaxLength = 20;
        _startup.Content = "随 Windows 开机自动启动";
        _startup.IsChecked = existing?.StartWithWindows ?? true;
        _connection.Text = existing is null ? "等待绑定" : "正在读取连接状态";
        _connection.Foreground = new SolidColorBrush(Color.Parse("#6E6E73"));
        _updateStatus.Text = $"当前版本：{ReceiverUpdateService.CurrentVersionText}";
        _updateStatus.Foreground = new SolidColorBrush(Color.Parse("#6E6E73"));
        _updateStatus.TextWrapping = TextWrapping.Wrap;
        _checkUpdate.Content = "检查并安装更新";
        _checkUpdate.Height = 42;
        _checkUpdate.Click += (_, _) => UpdateRequested?.Invoke(this, EventArgs.Empty);
        _error.Foreground = new SolidColorBrush(Color.Parse("#C92A2A"));
        _error.TextWrapping = TextWrapping.Wrap;
        _error.MinHeight = 24;

        _save.Content = existing is null ? "绑定并开始接收" : "保存设置";
        _save.Height = 48;
        _save.FontSize = 17;
        _save.FontWeight = FontWeight.SemiBold;
        _save.Background = new SolidColorBrush(Color.Parse("#0071E3"));
        _save.Foreground = Brushes.White;
        _save.Click += SaveOnClick;

        var form = new StackPanel { Spacing = 14 };
        form.Children.Add(title);
        form.Children.Add(description);
        form.Children.Add(statusPanel);
        if (_endpointWasPreconfigured)
        {
            form.Children.Add(new TextBlock
            {
                Text = $"系统网址已由管理员预置：{_endpoint.Text}",
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.Parse("#52605A")),
            });
        }
        else
        {
            form.Children.Add(Field("系统网址", _endpoint));
        }
        if (existing is not null) form.Children.Add(Field("本机名称", _deviceName));
        else form.Children.Add(new TextBlock
        {
            Text = $"本机名称将自动使用：{_deviceName.Text}",
            Foreground = new SolidColorBrush(Color.Parse("#52605A")),
        });
        form.Children.Add(Field(existing is null ? "一次性绑定码" : "新绑定码（可不填）", _code));
        form.Children.Add(_startup);
        form.Children.Add(_connection);
        form.Children.Add(_updateStatus);
        form.Children.Add(_checkUpdate);
        form.Children.Add(BuildPreviewPanel());
        form.Children.Add(_error);
        form.Children.Add(_save);

        Content = new ScrollViewer
        {
            Content = new Border { Padding = new Thickness(32), Child = form },
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };
    }

    public event EventHandler<ReceiverConfig>? Saved;
    public event EventHandler? UpdateRequested;
    public event Action<string>? PreviewRequested;
    public event EventHandler? AudioTestRequested;

    public void SetConnectionState(ReceiverConnectionState state)
    {
        _connection.Text = state.Text;
        _connection.Foreground = new SolidColorBrush(Color.Parse(state.Connected ? "#14804A" : "#B45309"));
    }

    public void SetUpdateState(string text, bool checking)
    {
        _updateStatus.Text = text;
        _checkUpdate.IsEnabled = !checking;
    }

    public void SetAudioTestState(string text, bool playing)
    {
        _audioTestStatus.Text = text;
        _audioTest.IsEnabled = !playing;
    }

    private Control BuildPreviewPanel()
    {
        var title = new TextBlock { Text = "通知样式预览", FontSize = 16, FontWeight = FontWeight.SemiBold };
        var note = new TextBlock
        {
            Text = "无需连接云端，可查看通知位置并测试教室电脑扬声器。",
            Foreground = new SolidColorBrush(Color.Parse("#6E6E73")),
            TextWrapping = TextWrapping.Wrap,
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(PreviewButton("普通", "info", "#0071E3"));
        buttons.Children.Add(PreviewButton("重要", "warning", "#B45309"));
        buttons.Children.Add(PreviewButton("紧急", "urgent", "#C92A2A"));
        _audioTest.Content = "测试本机声音";
        _audioTest.Height = 38;
        _audioTest.HorizontalAlignment = HorizontalAlignment.Stretch;
        _audioTest.HorizontalContentAlignment = HorizontalAlignment.Center;
        _audioTest.Background = new SolidColorBrush(Color.Parse("#E8F2FF"));
        _audioTest.Foreground = new SolidColorBrush(Color.Parse("#005BB8"));
        _audioTest.BorderBrush = new SolidColorBrush(Color.Parse("#B7D7F7"));
        _audioTest.Click += (_, _) => AudioTestRequested?.Invoke(this, EventArgs.Empty);
        _audioTestStatus.Foreground = new SolidColorBrush(Color.Parse("#6E6E73"));
        _audioTestStatus.FontSize = 12;
        _audioTestStatus.TextWrapping = TextWrapping.Wrap;
        return new Border
        {
            Background = new SolidColorBrush(Color.Parse("#FFFFFF")),
            BorderBrush = new SolidColorBrush(Color.Parse("#D9D9DE")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16),
            Child = new StackPanel { Spacing = 8, Children = { title, note, buttons, _audioTest, _audioTestStatus } },
        };
    }

    private Button PreviewButton(string text, string level, string color)
    {
        var button = new Button
        {
            Content = text,
            Height = 38,
            MinWidth = 76,
            Background = new SolidColorBrush(Color.Parse(color)),
            Foreground = Brushes.White,
            BorderBrush = Brushes.Transparent,
        };
        button.Click += (_, _) => PreviewRequested?.Invoke(level);
        return button;
    }

    private async void SaveOnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    {
        _error.Text = "";
        var endpoint = NormalizeEndpoint(_endpoint.Text ?? "");
        var deviceName = (_deviceName.Text ?? "").Trim();
        var code = (_code.Text ?? "").Trim().ToUpperInvariant();
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var baseUri) ||
            (baseUri.Scheme != Uri.UriSchemeHttps && !(baseUri.Scheme == Uri.UriSchemeHttp && baseUri.IsLoopback)))
        {
            _error.Text = "系统网址不正确。正式使用应以 https:// 开头，请完整粘贴部署后得到的网址。";
            return;
        }
        if (deviceName.Length is < 1 or > 80)
        {
            _error.Text = "本机名称应为 1 至 80 个字符。";
            return;
        }

        _save.IsEnabled = false;
        try
        {
            ReceiverConfig config;
            if (string.IsNullOrWhiteSpace(code) && _existing is not null)
            {
                config = new ReceiverConfig
                {
                    Endpoint = endpoint,
                    DeviceId = _existing.DeviceId,
                    DeviceToken = _existing.DeviceToken,
                    ClassId = _existing.ClassId,
                    ClassName = _existing.ClassName,
                    DeviceName = deviceName,
                    StartWithWindows = _startup.IsChecked == true,
                };
            }
            else
            {
                if (string.IsNullOrWhiteSpace(code))
                {
                    _error.Text = "请输入管理员网页中生成的一次性绑定码。";
                    return;
                }
                _save.Content = "正在连接并绑定…";
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
                var response = await client.PostAsJsonAsync(new Uri(baseUri, "/api/device/enroll"), new { code, deviceName });
                var text = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode)
                {
                    var apiError = TryReadError(text);
                    throw new InvalidOperationException(apiError ?? $"云端返回错误（{(int)response.StatusCode}）。");
                }
                var enrollment = JsonSerializer.Deserialize<EnrollmentResponse>(text, JsonOptions)
                    ?? throw new InvalidOperationException("云端返回内容无法识别。");
                if (string.IsNullOrWhiteSpace(enrollment.DeviceToken) || string.IsNullOrWhiteSpace(enrollment.DeviceId))
                    throw new InvalidOperationException("云端未返回完整的设备信息。");
                config = new ReceiverConfig
                {
                    Endpoint = endpoint,
                    DeviceId = enrollment.DeviceId,
                    DeviceToken = enrollment.DeviceToken,
                    ClassId = enrollment.ClassId,
                    ClassName = enrollment.ClassName,
                    DeviceName = deviceName,
                    StartWithWindows = _startup.IsChecked == true,
                };
            }

            ReceiverConfig.Save(config);
            Saved?.Invoke(this, config);
        }
        catch (TaskCanceledException)
        {
            _error.Text = "连接云端超时，请检查电脑网络和系统网址后重试。";
        }
        catch (HttpRequestException)
        {
            _error.Text = "无法连接云端，请检查电脑网络和系统网址后重试。";
        }
        catch (UnauthorizedAccessException)
        {
            _error.Text = "无法保存配置。请把 EXE 移到可写目录（例如 D:\\ICeCreamShouter）后重新运行。";
        }
        catch (IOException)
        {
            _error.Text = "配置文件写入失败。请确认 EXE 所在目录可写且磁盘空间充足。";
        }
        catch (Exception exception)
        {
            _error.Text = exception.Message;
            AppLog.Error("Device enrollment failed", exception);
        }
        finally
        {
            _save.Content = _existing is null ? "绑定并开始接收" : "保存设置";
            _save.IsEnabled = true;
        }
    }

    private static Control Field(string label, Control input)
    {
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(new TextBlock { Text = label, FontWeight = FontWeight.SemiBold });
        panel.Children.Add(input);
        return panel;
    }

    private static string NormalizeEndpoint(string value)
    {
        var result = value.Trim().TrimEnd('/');
        if (!result.Contains("://", StringComparison.Ordinal)) result = "https://" + result;
        return result;
    }

    private static string? TryReadError(string text)
    {
        try { return JsonSerializer.Deserialize<ApiError>(text, JsonOptions)?.Error; }
        catch { return null; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private sealed class EnrollmentResponse
    {
        public string DeviceId { get; set; } = "";
        public string DeviceToken { get; set; } = "";
        public string ClassId { get; set; } = "";
        public string ClassName { get; set; } = "";
    }
    private sealed class ApiError { public string Error { get; set; } = ""; }
}
