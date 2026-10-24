using Doudou.Core.Services;
using Doudou.Editor;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using static Doudou.WinUI.UiKit;

namespace Doudou.WinUI;

/// <summary>账号与云端同步：登录、设置上传/应用、历史记录合并。</summary>
public sealed class AccountWindow : Window
{
    private readonly Session _s;
    private readonly AccountManager _accounts;
    private readonly IntPtr _hwnd;
    private readonly Grid _authView = new();
    private readonly Grid _dashView = new();
    private readonly Grid _busy = new() { Visibility = Visibility.Collapsed };
    private readonly ProgressRing _ring = new() { IsActive = false, Width = 42, Height = 42 };
    private readonly TextBlock _busyText = Caption("");
    private readonly TextBox _email = new() { PlaceholderText = "you@example.com", Header = "邮箱" };
    private readonly PasswordBox _password = new() { PlaceholderText = "至少 10 位", Header = "密码" };
    private readonly TextBox _displayName = new() { PlaceholderText = "怎么称呼你（可选）", Header = "称呼" };
    private readonly TextBlock _authMsg = Caption("");
    private readonly Button _submit;
    private readonly RadioButton _loginRadio = new() { Content = "登录", GroupName = "account-mode", IsChecked = true };
    private readonly RadioButton _registerRadio = new() { Content = "注册", GroupName = "account-mode" };
    private readonly TextBlock _whoName = new() { FontFamily = Theme.Display, FontWeight = Microsoft.UI.Text.FontWeights.Bold, FontSize = 24 };
    private readonly TextBlock _whoEmail = Caption("");
    private readonly TextBlock _cloudState = Caption("");
    private readonly TextBlock _cloudMsg = Caption("");
    private readonly TextBlock _historyMsg = Caption("");
    private readonly StackPanel _historyList = new() { Spacing = 8 };
    private readonly TextBlock _syncState = Caption("");
    private readonly Button _logout;
    private readonly Button _web;
    private bool _registerMode, _running, _closed;
    private CancellationTokenSource? _cts;

    public AccountWindow(Session s, AccountManager accounts)
    {
        _s = s;
        _accounts = accounts;
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        Title = "账号与同步 · " + Theme.AppName;
        AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"));
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        int w = Math.Min(1120, area.Width - 40), h = Math.Min(860, area.Height - 40);
        AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h));
        ExtendsContentIntoTitleBar = true;
        if (AppWindowTitleBar.IsCustomizationSupported())
        {
            var cap = AppWindow.TitleBar;
            cap.ButtonBackgroundColor = Colors.Transparent;
            cap.ButtonInactiveBackgroundColor = Colors.Transparent;
            cap.ButtonForegroundColor = Theme.Text1;
            cap.ButtonInactiveForegroundColor = Theme.Text3;
            cap.ButtonHoverBackgroundColor = Theme.Orange;
            cap.ButtonHoverForegroundColor = Theme.Navy;
            cap.ButtonPressedBackgroundColor = Theme.OrangeDeep;
            cap.ButtonPressedForegroundColor = Theme.Navy;
        }

        _submit = WideBtn("登录", async () => await SubmitAsync(), G.Account);
        _logout = Btn("退出登录", async () => await LogoutAsync(), G.Back, tip: "同时清除本机保存的登录令牌");
        _web = Btn("在网页上管理", () => OpenWeb(), G.Open);
        Content = BuildRoot(out var drag);
        SetTitleBar(drag);
        _accounts.Changed += OnAccountsChanged;
        Closed += (_, _) =>
        {
            _closed = true;
            _cts?.Cancel();
            _accounts.Changed -= OnAccountsChanged;
        };
        RenderAll();
        if (_accounts.IsLoggedIn) _ = RefreshAsync();
    }

    private UIElement BuildRoot(out Grid drag)
    {
        var root = new Grid { Background = Theme.Background() };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) });
        root.RowDefinitions.Add(new RowDefinition());

        drag = new Grid { Background = new SolidColorBrush(Colors.Transparent), Padding = new Thickness(16, 0, 150, 0) };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        head.Children.Add(new Image
        {
            Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "app.png"))),
            Width = 20, Height = 20,
        });
        var names = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        names.Children.Add(new TextBlock
        {
            Text = "账号与同步", FontFamily = Theme.Display, FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            FontSize = 17, RenderTransform = new SkewTransform { AngleX = -10 },
        });
        names.Children.Add(new TextBlock
        {
            Text = "CLOUD SYNC", FontFamily = Theme.Display, FontSize = 11, CharacterSpacing = 160,
            Foreground = Theme.B(Theme.Orange), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 3, 0, 0),
        });
        head.Children.Add(names);
        drag.Children.Add(head);
        root.Children.Add(drag);

        var content = new Grid { Padding = new Thickness(20, 8, 20, 20) };
        Grid.SetRow(content, 1);
        root.Children.Add(content);
        _authView.Visibility = Visibility.Collapsed;
        _dashView.Visibility = Visibility.Collapsed;
        content.Children.Add(_authView);
        content.Children.Add(_dashView);
        BuildAuth();
        BuildDashboard();

        _busy.Background = Theme.B(Theme.Navy, 0xC0);
        var bs = new StackPanel { Spacing = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        bs.Children.Add(_ring);
        bs.Children.Add(_busyText);
        _busy.Children.Add(bs);
        Grid.SetRow(_busy, 1);
        root.Children.Add(_busy);
        return root;
    }

    private void BuildAuth()
    {
        var card = new StackPanel { Spacing = 8, MaxWidth = 520, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        card.Children.Add(Theme.Heading("把设置与历史带到每一台电脑", "BEADWATCH ACCOUNT", 26));
        card.Children.Add(Caption("桌面端与网页共用同一个账号。只同步品牌、作者、水印、豆径、生图模型和历史摘要；API Key 永不上传。"));
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, Margin = new Thickness(0, 12, 0, 0) };
        tabs.Children.Add(_loginRadio);
        tabs.Children.Add(_registerRadio);
        card.Children.Add(tabs);
        _loginRadio.Checked += (_, _) => SetMode(false);
        _registerRadio.Checked += (_, _) => SetMode(true);
        card.Children.Add(_email);
        card.Children.Add(_password);
        card.Children.Add(_displayName);
        _displayName.Visibility = Visibility.Collapsed;
        card.Children.Add(_submit);
        card.Children.Add(_authMsg);
        card.Children.Add(Caption("这是拼豆先锋自己的同步账号，不是 Qoder 账号。密码只保存 PBKDF2 派生值；当前不发送验证邮件，也没有自助找回密码。"));
        var border = Card(null, null, card);
        border.MaxWidth = 620;
        border.HorizontalAlignment = HorizontalAlignment.Center;
        border.VerticalAlignment = VerticalAlignment.Center;
        _authView.Children.Add(border);
    }

    private void BuildDashboard()
    {
        var grid = new Grid { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(370) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        _dashView.Children.Add(grid);

        var left = new StackPanel { Spacing = 0 };
        var account = new StackPanel { Spacing = 6 };
        var who = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
        var avatar = new Border
        {
            Width = 48, Height = 48, Background = Theme.B(Theme.Orange), CornerRadius = new CornerRadius(3, 12, 3, 12),
            Child = new TextBlock { Text = "B", FontSize = 21, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = Theme.B(Theme.Navy), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        names.Children.Add(_whoName);
        names.Children.Add(_whoEmail);
        who.Children.Add(avatar);
        who.Children.Add(names);
        account.Children.Add(who);
        account.Children.Add(Flow(_logout));
        account.Children.Add(Caption("登录令牌用 Windows 当前用户凭据保护，保存在 userdata；不会写进工程或安装包。"));
        left.Children.Add(Card("账号", null, account));

        var cloud = new StackPanel { Spacing = 8 };
        cloud.Children.Add(_cloudState);
        cloud.Children.Add(_syncState);
        cloud.Children.Add(Flow(
            Btn("上传本机设置", async () => await UploadSettingsAsync(), G.Download, accent: true),
            Btn("应用云端设置", async () => await ApplyCloudSettingsAsync(), G.Refresh),
            Btn("刷新", async () => await RefreshAsync(), G.Refresh)));
        cloud.Children.Add(_cloudMsg);
        cloud.Children.Add(Caption("桌面端保存设置后会自动上传；网页上修改的设置，在这里点「应用云端设置」拉取。"));
        left.Children.Add(Card("云端设置", "品牌 / 作者 / 水印 / 豆径 / 生图模型。API Key 不在同步范围内。", cloud));
        left.Children.Add(Flow(_web));

        var right = new StackPanel { Spacing = 8 };
        var histHead = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        histHead.Children.Add(Btn("同步历史", async () => await SyncHistoryAsync(), G.Refresh, accent: true));
        histHead.Children.Add(Btn("刷新", async () => await RefreshAsync(), G.Refresh));
        right.Children.Add(histHead);
        right.Children.Add(_historyMsg);
        right.Children.Add(new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _historyList,
        });
        var historyCard = Card("历史记录", "最近的重建、导入、导出与平面图纸摘要；合并本机与云端，不上传工程文件。", right);
        Grid.SetColumn(historyCard, 1);
        grid.Children.Add(left);
        grid.Children.Add(historyCard);
    }

    private void SetMode(bool register)
    {
        _registerMode = register;
        _displayName.Visibility = register ? Visibility.Visible : Visibility.Collapsed;
        _submit.Content = register ? "注册并登录" : "登录";
        _authMsg.Text = "";
        _password.PasswordRevealMode = PasswordRevealMode.Peek;
    }

    private async Task SubmitAsync()
    {
        if (_running) return;
        var email = _email.Text.Trim();
        var password = _password.Password;
        if (email.Length == 0 || password.Length == 0) { _authMsg.Text = "请填写邮箱和密码。"; return; }
        if (_registerMode && password.Length < 10) { _authMsg.Text = "密码至少 10 位。"; return; }
        SetBusy(true, _registerMode ? "正在创建账号…" : "正在登录…");
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        try
        {
            if (_registerMode) await _accounts.RegisterAsync(email, password, _displayName.Text.Trim(), _cts.Token);
            else await _accounts.LoginAsync(email, password, _cts.Token);
            _authMsg.Text = "";
            RenderAll();
        }
        catch (AccountClient.AccountApiException e)
        {
            LogError(e);
            _authMsg.Text = AccountClient.Friendly(e.Code);
        }
        catch (Exception e)
        {
            LogError(e);
            _authMsg.Text = "登录失败：" + e.Message;
        }
        finally { SetBusy(false, ""); }
    }

    private async Task LogoutAsync()
    {
        await _accounts.LogoutAsync();
        _email.Text = "";
        _password.Password = "";
        RenderAll();
    }

    private async Task UploadSettingsAsync()
    {
        if (!_accounts.IsLoggedIn) return;
        SetBusy(true, "正在上传本机设置…");
        try
        {
            var result = await _accounts.PushSettingsAsync();
            _cloudMsg.Text = $"已上传 · 版本 {result.Version}";
            RenderCloud();
        }
        catch (AccountClient.AccountApiException e) { _cloudMsg.Text = AccountClient.Friendly(e.Code); }
        catch (Exception) { _cloudMsg.Text = "上传失败，请稍后重试。"; }
        finally { SetBusy(false, ""); }
    }

    private async Task ApplyCloudSettingsAsync()
    {
        if (!_accounts.IsLoggedIn) return;
        SetBusy(true, "正在应用云端设置…");
        try
        {
            var result = await _accounts.ApplyCloudSettingsAsync();
            _cloudMsg.Text = result.Settings == null ? "云端还没有设置，可先上传本机设置。" : $"已应用云端设置 · 版本 {result.Version}";
            RenderCloud();
        }
        catch (AccountClient.AccountApiException e) { _cloudMsg.Text = AccountClient.Friendly(e.Code); }
        catch (Exception) { _cloudMsg.Text = "应用失败，请稍后重试。"; }
        finally { SetBusy(false, ""); }
    }

    private async Task SyncHistoryAsync()
    {
        if (!_accounts.IsLoggedIn) return;
        SetBusy(true, "正在同步历史记录…");
        try
        {
            await _accounts.SyncHistoryAsync();
            _historyMsg.Text = "历史记录已合并。";
            RenderHistory();
        }
        catch (AccountClient.AccountApiException e) { _historyMsg.Text = AccountClient.Friendly(e.Code); }
        catch (Exception) { _historyMsg.Text = "同步失败，请稍后重试。"; }
        finally { SetBusy(false, ""); }
    }

    private async Task RefreshAsync()
    {
        if (!_accounts.IsLoggedIn || _running) return;
        try
        {
            await _accounts.RefreshSettingsAsync();
            await _accounts.SyncHistoryAsync();
            RenderAll();
        }
        catch (AccountClient.AccountApiException e)
        {
            if (e.Status == 401) { RenderAll(); return; }
            _cloudMsg.Text = AccountClient.Friendly(e.Code);
        }
        catch (Exception) { _cloudMsg.Text = "刷新失败，请稍后重试。"; }
    }

    private void OnAccountsChanged() => Ui(RenderAll);

    private void RenderAll()
    {
        if (_closed) return;
        var logged = _accounts.IsLoggedIn;
        _authView.Visibility = logged ? Visibility.Collapsed : Visibility.Visible;
        _dashView.Visibility = logged ? Visibility.Visible : Visibility.Collapsed;
        if (!logged)
        {
            _authMsg.Text = "";
            return;
        }
        var session = _accounts.Session;
        _whoName.Text = session?.DisplayName ?? "BEADWATCH";
        _whoEmail.Text = session?.Email ?? "";
        _cloudState.Text = "登录后会自动读取云端设置与历史。";
        _syncState.Text = _accounts.LastSyncError ?? "云端连接正常。";
        RenderCloud();
        RenderHistory();
    }

    private void RenderCloud()
    {
        var c = _accounts.CloudSettings;
        if (c == null)
        {
            _cloudState.Text = "还没有读取云端设置。";
            return;
        }
        var when = c.UpdatedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "—";
        var s = c.Settings;
        _cloudState.Text = s == null
            ? "云端还没有设置。"
            : $"云端设置 v{c.Version} · {when}\n色卡 {s.DefaultBrand ?? "—"} · 豆径 {(s.BeadPitchMm?.ToString("0.0") ?? "—")} mm · 模型 {s.ImageModel ?? "—"}";
    }

    private void RenderHistory()
    {
        _historyList.Children.Clear();
        var entries = _accounts.History.Take(60).ToList();
        if (entries.Count == 0)
        {
            _historyList.Children.Add(Caption("还没有历史记录。生成、导入或导出作品后会自动记录。"));
            return;
        }
        foreach (var e in entries)
        {
            var card = new StackPanel { Spacing = 3 };
            var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            line.Children.Add(Theme.Tag(KindLabel(e.Kind), Theme.Blue, 10, Microsoft.UI.Colors.White));
            line.Children.Add(new TextBlock { Text = e.Title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            line.Children.Add(new TextBlock
            {
                Text = e.CreatedAt.ToLocalTime().ToString("MM-dd HH:mm"), FontSize = 11,
                Foreground = Theme.B(Theme.Text3), VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 0, 0),
            });
            card.Children.Add(line);
            if (!string.IsNullOrWhiteSpace(e.Summary)) card.Children.Add(Caption(e.Summary));
            var meta = new List<string>();
            if (e.Meta.Beads is > 0) meta.Add($"{e.Meta.Beads:N0} 颗");
            if (!string.IsNullOrWhiteSpace(e.Meta.Size)) meta.Add(e.Meta.Size!);
            if (e.Meta.Colors is > 0) meta.Add($"{e.Meta.Colors} 色");
            if (!string.IsNullOrWhiteSpace(e.Meta.Mode)) meta.Add(e.Meta.Mode!);
            if (meta.Count > 0) card.Children.Add(Caption(string.Join(" · ", meta)));
            _historyList.Children.Add(new Border
            {
                Background = Theme.B(Theme.Navy, 0x99), CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1), BorderBrush = Theme.B(Theme.Blue, 0x28),
                Padding = new Thickness(12, 9, 12, 9), Child = card,
            });
        }
    }

    private static string KindLabel(string kind) => kind switch
    {
        "model" => "重建",
        "import" => "导入",
        "project" => "工程",
        "export" => "导出",
        "flat" => "平面",
        "open" => "打开",
        _ => kind,
    };

    private void SetBusy(bool busy, string text)
    {
        if (_closed) return;
        _running = busy;
        _ring.IsActive = busy;
        _busy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        _busyText.Text = text;
        _authView.IsHitTestVisible = !busy;
        _dashView.IsHitTestVisible = !busy;
        _authView.Opacity = busy ? 0.5 : 1;
        _dashView.Opacity = busy ? 0.5 : 1;
    }

    private static void LogError(Exception e)
    {
        try
        {
            File.AppendAllText(System.IO.Path.Combine(AppPaths.UserData, "account_error.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {e}\n\n");
        }
        catch (Exception) { }
    }

    private void OpenWeb()
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = AccountClient.AccountPage, UseShellExecute = true }); }
        catch (Exception e) { _cloudMsg.Text = "打开网页失败：" + e.Message; }
    }

    private void Ui(Action action)
    {
        if (!_closed) DispatcherQueue.TryEnqueue(() => { if (!_closed) action(); });
    }
}
