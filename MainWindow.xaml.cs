using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CrtSharp.Connections;
using CrtSharp.Controls;
using CrtSharp.Models;
using CrtSharp.Services;

namespace CrtSharp;

public partial class MainWindow : Window
{
    private static readonly SessionStore SharedSessionStore = new();
    private static readonly ObservableCollection<SessionProfile> SharedProfiles =
        new(SharedSessionStore.Load());
    private static readonly KnownHostStore SharedKnownHosts = new();

    private readonly ObservableCollection<SessionProfile> _profiles = SharedProfiles;
    private readonly SessionStore _sessionStore = SharedSessionStore;
    private readonly KnownHostStore _knownHosts = SharedKnownHosts;
    private readonly Dictionary<TabItem, ActiveSession> _activeSessions = [];

    public MainWindow()
    {
        InitializeComponent();
        SessionList.ItemsSource = _profiles;
        TerminalTabs.PreviewKeyDown += TerminalTabs_PreviewKeyDown;
    }

    private async void TerminalTabs_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Home/End 不应切换到第一个/最后一个标签：当焦点落在标签页头而不是
        // WebView2 终端里时，TabControl 的内置键盘导航会劫持这两个键。
        // 这里拦截并转发为行首/行尾的光标控制序列，再把焦点还给终端。
        if (e.Key is not (Key.Home or Key.End))
        {
            return;
        }

        if (Keyboard.FocusedElement is TextBox or System.Windows.Controls.Primitives.TextBoxBase)
        {
            return;
        }

        e.Handled = true;

        if (TerminalTabs.SelectedItem is not TabItem tab ||
            !_activeSessions.TryGetValue(tab, out var active) ||
            !active.Connection.IsConnected)
        {
            return;
        }

        var sequence = e.Key == Key.Home ? "\u001b[H" : "\u001b[F";
        try
        {
            await active.Connection.SendAsync(Encoding.UTF8.GetBytes(sequence));
        }
        catch (Exception)
        {
            // 发送失败交给连接错误流程处理，这里不再重复报告
        }

        await active.Terminal.FocusTerminalAsync();
    }

    private async void NewSession_Click(object sender, RoutedEventArgs e) =>
        await ShowConnectionDialogAsync();

    private async void NewCommand_Executed(object sender, ExecutedRoutedEventArgs e) =>
        await ShowConnectionDialogAsync();

    private async Task ShowConnectionDialogAsync(SessionProfile? source = null)
    {
        var dialog = new NewSessionDialog(source) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.ResultProfile is null)
        {
            return;
        }

        if (dialog.SaveProfile)
        {
            SaveProfile(dialog.ResultProfile);
        }

        await OpenSessionAsync(dialog.ResultProfile, dialog.Password);
    }

    private void SaveProfile(SessionProfile profile)
    {
        var existing = _profiles.FirstOrDefault(item => item.Id == profile.Id);
        if (existing is null)
        {
            _profiles.Add(profile.Clone());
        }
        else
        {
            var index = _profiles.IndexOf(existing);
            _profiles[index] = profile.Clone();
        }
        _sessionStore.Save(_profiles);
    }

    private async Task OpenSessionAsync(SessionProfile profile, string password)
    {
        var terminal = new TerminalView(profile);
        var tab = new TabItem { Content = terminal };
        tab.Header = CreateTabHeader(tab, profile.Name);

        var active = new ActiveSession(this, profile.Clone(), password, terminal);
        var connection = CreateConnection(profile, password, active.VerifyHostKey);
        active.Connection = connection;
        _activeSessions[tab] = active;
        WireTerminalEvents(tab, active);
        WireConnectionEvents(tab, active, connection);

        TerminalTabs.Items.Add(tab);
        TerminalTabs.SelectedItem = tab;
        EmptyState.Visibility = Visibility.Collapsed;

        await terminal.InitializeAsync();
        await terminal.WriteTextAsync($"\u001b[38;5;45m正在连接 {profile.DisplayTarget}…\u001b[0m\r\n");
        await ConnectActiveSessionAsync(tab, active, isReconnect: false);
    }

    private void WireTerminalEvents(TabItem tab, ActiveSession active)
    {
        active.Terminal.InputReceived += async (_, args) =>
        {
            if (active.Connection.IsConnected)
            {
                try
                {
                    await active.Connection.SendAsync(Encoding.UTF8.GetBytes(args.Data));
                }
                catch (Exception exception)
                {
                    if (active.Connection.IsConnected)
                    {
                        await active.Owner.ShowConnectionErrorAsync(active.Terminal, exception);
                    }
                }
                return;
            }

            if (active.ReconnectReady && IsEnter(args.Data))
            {
                var owner = active.Owner;
                var currentTab = owner.FindTab(active);
                if (currentTab is not null)
                {
                    await owner.ConnectActiveSessionAsync(currentTab, active, isReconnect: true);
                }
            }
        };

        active.Terminal.TerminalResized += async (_, args) =>
        {
            active.Columns = args.Columns;
            active.Rows = args.Rows;
            var owner = active.Owner;
            var currentTab = owner.FindTab(active);
            if (currentTab is not null && owner.TerminalTabs.SelectedItem == currentTab)
            {
                owner.TerminalSizeText.Text = $"{args.Columns} × {args.Rows}";
            }
            if (active.Connection.IsConnected)
            {
                await active.Connection.ResizeAsync(args.Columns, args.Rows);
            }
        };
    }

    private void WireConnectionEvents(
        TabItem tab,
        ActiveSession active,
        ITerminalConnection connection)
    {
        connection.DataReceived += (_, bytes) =>
            _ = active.Terminal.Dispatcher.InvokeAsync(async () =>
            {
                if (ReferenceEquals(active.Connection, connection) && !active.IsClosing)
                {
                    await active.Terminal.WriteAsync(bytes);
                }
            });

        connection.StatusChanged += (_, status) =>
            _ = active.Terminal.Dispatcher.InvokeAsync(async () =>
            {
                if (!ReferenceEquals(active.Connection, connection) || active.IsClosing)
                {
                    return;
                }

                var owner = active.Owner;
                active.Status = status;
                var currentTab = owner.FindTab(active);
                if (currentTab is not null)
                {
                    owner.UpdateTabConnectionState(currentTab, status);
                    if (owner.TerminalTabs.SelectedItem == currentTab)
                    {
                        owner.StatusText.Text = $"{active.Profile.Name} — {status}";
                    }
                }

                if (!active.IsConnecting && IsDisconnectedStatus(status))
                {
                    await active.Owner.ShowReconnectPromptAsync(active);
                }
            });

        connection.Faulted += (_, exception) =>
            _ = active.Terminal.Dispatcher.InvokeAsync(async () =>
            {
                if (!ReferenceEquals(active.Connection, connection) || active.IsClosing)
                {
                    return;
                }

                var owner = active.Owner;
                await owner.ShowConnectionErrorAsync(active.Terminal, exception);
                await active.Owner.ShowReconnectPromptAsync(active);
            });
    }

    private async Task ConnectActiveSessionAsync(
        TabItem tab,
        ActiveSession active,
        bool isReconnect)
    {
        if (active.IsConnecting || active.IsClosing)
        {
            return;
        }

        active.IsConnecting = true;
        active.ReconnectReady = false;
        active.ReconnectPromptShown = false;

        try
        {
            if (isReconnect)
            {
                active.ConnectionCancellation.Cancel();
                await active.Connection.DisposeAsync();
                active.ConnectionCancellation.Dispose();

                var owner = active.Owner;
                var newConnection = owner.CreateConnection(
                    active.Profile,
                    active.Password,
                    active.VerifyHostKey);
                active.Connection = newConnection;
                active.ConnectionCancellation = new CancellationTokenSource();
                owner.WireConnectionEvents(tab, active, newConnection);

                await active.Terminal.WriteTextAsync(
                    $"\r\n\u001b[38;5;45m正在重新连接 {active.Profile.DisplayTarget}…\u001b[0m\r\n");
            }

            await active.Connection.ResizeAsync(active.Columns, active.Rows);
            await active.Connection.ConnectAsync(active.ConnectionCancellation.Token);
            active.Status = "已连接";
            await active.Terminal.FocusTerminalAsync();
        }
        catch (OperationCanceledException)
        {
            if (!active.IsClosing)
            {
                await active.Terminal.WriteTextAsync("\r\n\u001b[33m连接已取消。\u001b[0m\r\n");
                await active.Owner.ShowReconnectPromptAsync(active);
            }
        }
        catch (Exception exception)
        {
            var owner = active.Owner;
            await owner.ShowConnectionErrorAsync(active.Terminal, exception);
            await owner.ShowReconnectPromptAsync(active);
        }
        finally
        {
            active.IsConnecting = false;
            if (active.Connection.IsConnected)
            {
                await active.Terminal.FocusTerminalAsync();
            }
        }
    }

    private static bool IsEnter(string input) => input.Contains('\r') || input.Contains('\n');

    private static bool IsDisconnectedStatus(string status) =>
        status is "远程主机已断开" or "已断开";

    private async Task ShowReconnectPromptAsync(ActiveSession active)
    {
        var owner = active.Owner;
        active.ReconnectReady = true;
        active.Status = "已断开，按 Enter 重连";
        var tab = owner.FindTab(active);
        if (tab is not null)
        {
            owner.UpdateTabConnectionState(tab, active.Status);
        }
        if (active.ReconnectPromptShown || active.IsClosing)
        {
            return;
        }

        active.ReconnectPromptShown = true;
        await active.Terminal.WriteTextAsync(
            "\r\n\u001b[33m会话已断开。按 Enter 重新连接，当前终端内容将保留。\u001b[0m\r\n");
    }

    private ITerminalConnection CreateConnection(
        SessionProfile profile,
        string password,
        Func<HostKeyDetails, bool> hostKeyVerifier) =>
        profile.Protocol switch
        {
            ConnectionProtocol.Ssh => new SshTerminalConnection(profile, password, hostKeyVerifier),
            ConnectionProtocol.Telnet => new TelnetTerminalConnection(profile),
            ConnectionProtocol.Serial => new SerialTerminalConnection(profile),
            _ => throw new NotSupportedException($"不支持协议 {profile.Protocol}。")
        };

    private bool VerifyHostKey(HostKeyDetails details) => Dispatcher.Invoke(() =>
    {
        var state = _knownHosts.Check(details.Host, details.Port, details.Fingerprint);
        if (state == HostKeyState.Match)
        {
            return true;
        }

        if (state == HostKeyState.Mismatch)
        {
            MessageBox.Show(
                this,
                $"服务器 {details.Host}:{details.Port} 的 SSH 主机密钥已改变！\n\n" +
                $"收到：{details.Algorithm}\n{details.Fingerprint}\n\n" +
                "为防止中间人攻击，连接已拒绝。若服务器确实更换了密钥，请删除 %APPDATA%\\CrtSharp\\known-hosts.json 中对应记录后重试。",
                "SSH 主机密钥不匹配",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return false;
        }

        var answer = MessageBox.Show(
            this,
            $"首次连接到 {details.Host}:{details.Port}。\n\n" +
            $"算法：{details.Algorithm}\n指纹：{details.Fingerprint}\n\n" +
            "请与服务器管理员提供的指纹核对。是否信任并保存？",
            "确认 SSH 主机密钥",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
        {
            return false;
        }

        _knownHosts.Trust(details.Host, details.Port, details.Fingerprint);
        return true;
    });

    private FrameworkElement CreateTabHeader(TabItem tab, string title)
    {
        var panel = new StackPanel
        {
            Tag = "TabHeader",
            Orientation = Orientation.Horizontal,
            ToolTip = "右键管理此会话",
            ContextMenu = CreateTabContextMenu(tab)
        };
        panel.Children.Add(new Border
        {
            Tag = "ConnectionState",
            Width = 8,
            Height = 8,
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(Color.FromRgb(136, 136, 136)),
            Margin = new Thickness(0, 0, 7, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "未连接"
        });
        panel.Children.Add(new TextBlock
        {
            Tag = "TabTitle",
            Text = title,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 180,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        panel.MouseLeftButtonDown += (_, args) =>
        {
            if (args.ClickCount == 2 &&
                FindVisualParent<Button>(args.OriginalSource as DependencyObject) is null)
            {
                RenameTab(tab);
                args.Handled = true;
            }
        };
        var close = new Button
        {
            Content = "×",
            Padding = new Thickness(5, 0, 5, 1),
            Margin = new Thickness(8, 0, -5, 0),
            Background = Brushes.Transparent,
            Foreground = Brushes.LightGray,
            BorderThickness = new Thickness(0),
            ToolTip = "关闭标签"
        };
        close.Click += async (_, _) => await CloseTabAsync(tab);
        panel.Children.Add(close);
        return panel;
    }

    private async Task CloseTabAsync(TabItem tab)
    {
        if (!ConfirmCloseTab(tab))
        {
            return;
        }

        Exception? cleanupError = null;
        if (_activeSessions.Remove(tab, out var active))
        {
            active.IsClosing = true;
            active.ConnectionCancellation.Cancel();
            try
            {
                await active.Connection.DisposeAsync();
            }
            catch (Exception exception)
            {
                cleanupError = exception;
            }
            finally
            {
                active.ConnectionCancellation.Dispose();
                active.Terminal.Dispose();
            }
        }

        TerminalTabs.Items.Remove(tab);
        EmptyState.Visibility = TerminalTabs.Items.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (TerminalTabs.Items.Count == 0)
        {
            StatusText.Text = cleanupError is null ? "就绪" : $"会话已关闭，清理警告：{cleanupError.Message}";
            TerminalSizeText.Text = "-- × --";
        }
        else if (cleanupError is not null)
        {
            StatusText.Text = $"会话已关闭，清理警告：{cleanupError.Message}";
        }
    }

    private async void SessionList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SessionList.SelectedItem is SessionProfile profile)
        {
            await ShowConnectionDialogAsync(profile);
        }
    }

    private async void ConnectSelected_Click(object sender, RoutedEventArgs e)
    {
        if (SessionList.SelectedItem is SessionProfile profile)
        {
            await ShowConnectionDialogAsync(profile);
        }
        else
        {
            await ShowConnectionDialogAsync();
        }
    }

    private void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        if (SessionList.SelectedItem is not SessionProfile profile)
        {
            return;
        }

        if (MessageBox.Show(
                this,
                $"删除会话“{profile.Name}”？",
                "删除会话",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        _profiles.Remove(profile);
        _sessionStore.Save(_profiles);
    }

    private async void DisconnectCurrent_Click(object sender, RoutedEventArgs e)
    {
        if (GetSelectedSession() is { } active)
        {
            active.ConnectionCancellation.Cancel();
            await active.Connection.DisconnectAsync();
        }
    }

    private async void CloseCurrent_Click(object sender, RoutedEventArgs e)
    {
        if (TerminalTabs.SelectedItem is TabItem tab)
        {
            await CloseTabAsync(tab);
        }
    }

    private async void CopyTerminal_Click(object sender, RoutedEventArgs e)
    {
        if (GetSelectedSession() is { } active)
        {
            await active.Terminal.CopySelectionAsync();
        }
    }

    private async void PasteTerminal_Click(object sender, RoutedEventArgs e)
    {
        if (GetSelectedSession() is { } active)
        {
            await active.Terminal.PasteClipboardAsync();
        }
    }

    private async void SelectAllTerminal_Click(object sender, RoutedEventArgs e)
    {
        if (GetSelectedSession() is { } active)
        {
            await active.Terminal.SelectAllAsync();
        }
    }

    private ActiveSession? GetSelectedSession() =>
        TerminalTabs.SelectedItem is TabItem tab && _activeSessions.TryGetValue(tab, out var active)
            ? active
            : null;

    private async void ClearTerminal_Click(object sender, RoutedEventArgs e)
    {
        if (GetSelectedSession() is { } active)
        {
            await active.Terminal.ClearAsync();
        }
    }

    private void TerminalTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var canDuplicate = TerminalTabs.SelectedItem is TabItem selectedTab &&
                           _activeSessions.TryGetValue(selectedTab, out var selectedSession) &&
                           selectedSession.Profile.Protocol != ConnectionProtocol.Serial;
        DuplicateSessionMenuItem.IsEnabled = canDuplicate;
        DuplicateSessionButton.IsEnabled = canDuplicate;

        if (TerminalTabs.SelectedItem is TabItem tab && _activeSessions.TryGetValue(tab, out var active))
        {
            StatusText.Text = $"{active.Profile.Name} — {active.Status}";
            TerminalSizeText.Text = $"{active.Columns} × {active.Rows}";
            _ = active.Terminal.FocusTerminalAsync();
        }
    }

    private async Task ShowConnectionErrorAsync(TerminalView terminal, Exception exception)
    {
        StatusText.Text = "连接错误";
        await terminal.WriteTextAsync(
            $"\r\n\u001b[31m连接错误：{exception.Message}\u001b[0m\r\n");
    }

    private void About_Click(object sender, RoutedEventArgs e) => MessageBox.Show(
        this,
        "CrtSharp\n\n多标签 SSH / Telnet / 串口终端\n终端内核：xterm.js",
        "关于",
        MessageBoxButton.OK,
        MessageBoxImage.Information);

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_Closed(object? sender, EventArgs e)
    {
        foreach (var active in _activeSessions.Values
                     .Where(active => ReferenceEquals(active.Owner, this)))
        {
            active.IsClosing = true;
            active.ConnectionCancellation.Cancel();
            _ = active.Connection.DisposeAsync();
            active.Terminal.Dispose();
            active.ConnectionCancellation.Dispose();
        }
        _activeSessions.Clear();
    }

    private sealed class ActiveSession(
        MainWindow owner,
        SessionProfile profile,
        string password,
        TerminalView terminal)
    {
        public MainWindow Owner { get; set; } = owner;
        public SessionProfile Profile { get; } = profile;
        public string Password { get; } = password;
        public TerminalView Terminal { get; } = terminal;
        public ITerminalConnection Connection { get; set; } = null!;
        public CancellationTokenSource ConnectionCancellation { get; set; } = new();
        public string Status { get; set; } = "未连接";
        public int Columns { get; set; } = 120;
        public int Rows { get; set; } = 30;
        public bool IsConnecting { get; set; }
        public bool IsClosing { get; set; }
        public bool IsTransferring { get; set; }
        public bool ReconnectReady { get; set; }
        public bool ReconnectPromptShown { get; set; }

        public bool VerifyHostKey(HostKeyDetails details) => Owner.VerifyHostKey(details);
    }
}
