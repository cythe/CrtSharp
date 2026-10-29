using System.IO;
using System.IO.Ports;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CrtSharp.Models;
using CrtSharp.Services;
using Microsoft.Win32;

namespace CrtSharp;

public partial class NewSessionDialog : Window
{
    private readonly SessionProfile? _source;
    private ConnectionProtocol _lastProtocol = ConnectionProtocol.Ssh;
    private TerminalAppearanceSettings _terminalAppearance = new(
        "Cascadia Mono",
        14,
        TerminalColorTheme.Dark,
        TerminalCursorStyle.Block,
        true,
        "user@host:~$ echo CrtSharp 终端预览");

    public NewSessionDialog(SessionProfile? profile = null, bool editOnly = false)
    {
        InitializeComponent();
        _source = profile;
        if (editOnly)
        {
            Title = "编辑会话";
            PrimaryActionButton.Content = "保存";
            SaveProfileBox.IsChecked = true;
            SaveProfileBox.Visibility = Visibility.Collapsed;
        }

        ProtocolBox.ItemsSource = Enum.GetValues<ConnectionProtocol>();
        SshAuthBox.ItemsSource = new[]
        {
            new EnumOption<SshAuthenticationMode>("密码", SshAuthenticationMode.Password),
            new EnumOption<SshAuthenticationMode>("私钥", SshAuthenticationMode.PrivateKey)
        };
        SerialPortBox.ItemsSource = SerialPort.GetPortNames().OrderBy(value => value).ToArray();

        var value = profile ?? new SessionProfile();
        _terminalAppearance = value.GetTerminalAppearance();
        var scrollbackOptions = new List<int>
        {
            20_000, 50_000, 100_000, 200_000, 500_000, 1_000_000
        };
        ScrollbackLinesBox.ItemsSource = scrollbackOptions;

        var baudRates = new List<int>
        {
            300, 600, 1200, 2400, 4800, 9600, 14400, 19200,
            28800, 38400, 57600, 115200, 128000, 230400, 256000,
            460800, 921600
        };
        if (value.BaudRate > 0 && !baudRates.Contains(value.BaudRate))
        {
            baudRates.Add(value.BaudRate);
            baudRates.Sort();
        }
        BaudRateBox.ItemsSource = baudRates;

        var dataBitsOptions = new List<int> { 5, 6, 7, 8 };
        if (!dataBitsOptions.Contains(value.DataBits))
        {
            dataBitsOptions.Add(value.DataBits);
            dataBitsOptions.Sort();
        }
        DataBitsBox.ItemsSource = dataBitsOptions;

        ParityBox.ItemsSource = new[]
        {
            new EnumOption<Parity>("无（N）", Parity.None),
            new EnumOption<Parity>("奇校验（O）", Parity.Odd),
            new EnumOption<Parity>("偶校验（E）", Parity.Even),
            new EnumOption<Parity>("标记（M）", Parity.Mark),
            new EnumOption<Parity>("空格（S）", Parity.Space)
        };

        StopBitsBox.ItemsSource = new[]
        {
            new EnumOption<StopBits>("1", StopBits.One),
            new EnumOption<StopBits>("1.5", StopBits.OnePointFive),
            new EnumOption<StopBits>("2", StopBits.Two)
        };

        HandshakeBox.ItemsSource = new[]
        {
            new EnumOption<Handshake>("无", Handshake.None),
            new EnumOption<Handshake>("XON/XOFF", Handshake.XOnXOff),
            new EnumOption<Handshake>("RTS/CTS", Handshake.RequestToSend),
            new EnumOption<Handshake>("RTS/CTS + XON/XOFF", Handshake.RequestToSendXOnXOff)
        };

        NameBox.Text = value.Name;
        ProtocolBox.SelectedItem = value.Protocol;
        HostBox.Text = value.Host;
        PortBox.Text = value.Port.ToString();
        UsernameBox.Text = value.Username;
        SshAuthBox.SelectedValue = value.SshAuthentication;
        PrivateKeyPathBox.Text = value.PrivateKeyPath;
        SerialPortBox.Text = value.SerialPort;
        BaudRateBox.Text = value.BaudRate.ToString();
        DataBitsBox.SelectedItem = value.DataBits;
        ParityBox.SelectedValue = value.Parity;
        StopBitsBox.SelectedValue = value.StopBits;
        HandshakeBox.SelectedValue = value.Handshake;
        DtrEnableBox.IsChecked = value.DtrEnable;
        RtsEnableBox.IsChecked = value.RtsEnable;
        ScrollbackLinesBox.Text = value.ScrollbackLines.ToString();
        _lastProtocol = value.Protocol;
        UpdateProtocolUi(value.Protocol, setDefaultPort: false);
        UpdateAuthenticationUi(value.SshAuthentication);
        UpdateRtsAvailability(value.Handshake);

        RememberPasswordBox.IsChecked = value.RememberPassword;
        if (value.RememberPassword)
        {
            if (SecretProtector.TryUnprotect(value.ProtectedPassword, out var savedPassword))
            {
                PasswordBox.Password = savedPassword;
            }
            else
            {
                RememberPasswordBox.IsChecked = false;
                ValidationText.Text = "已保存的密码无法由当前 Windows 用户解密，请重新输入。";
            }
        }
        UpdateRememberPasswordAvailability();
    }

    public SessionProfile? ResultProfile { get; private set; }

    // For private-key authentication this carries the key passphrase instead.
    // It only lives in memory for the lifetime of the terminal tab.
    public string Password { get; private set; } = string.Empty;
    public bool SaveProfile => SaveProfileBox.IsChecked == true;

    private void ProtocolBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProtocolBox.SelectedItem is not ConnectionProtocol protocol)
        {
            return;
        }

        var changed = protocol != _lastProtocol;
        _lastProtocol = protocol;
        UpdateProtocolUi(protocol, changed);
    }

    private void SshAuthBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SshAuthBox.SelectedValue is SshAuthenticationMode mode)
        {
            UpdateAuthenticationUi(mode);
        }
    }

    private void HandshakeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (HandshakeBox.SelectedValue is Handshake handshake)
        {
            UpdateRtsAvailability(handshake);
        }
    }

    private void DigitsOnly_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = !e.Text.All(char.IsAsciiDigit);
    }

    private void DigitsOnly_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Filtering is done in PreviewTextInput; allow editing/navigation keys and clipboard paste.
        if (e.Key == Key.Space)
        {
            e.Handled = true;
        }
    }

    private static bool TryParseNumber(string? text, int min, int max, out int value)
    {
        var trimmed = text?.Trim() ?? string.Empty;
        if (int.TryParse(trimmed, out value) && value >= min && value <= max)
        {
            return true;
        }

        value = 0;
        return false;
    }

    private void UpdateProtocolUi(ConnectionProtocol protocol, bool setDefaultPort)
    {
        if (NetworkBorder is null || SerialPanel is null || SshCredentialsPanel is null)
        {
            return;
        }

        var serial = protocol == ConnectionProtocol.Serial;
        NetworkBorder.Visibility = serial ? Visibility.Collapsed : Visibility.Visible;
        SerialPanel.Visibility = serial ? Visibility.Visible : Visibility.Collapsed;
        SshCredentialsPanel.Visibility = protocol == ConnectionProtocol.Ssh
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (setDefaultPort)
        {
            PortBox.Text = protocol == ConnectionProtocol.Ssh ? "22" : "23";
        }
        UpdateRememberPasswordAvailability();
    }

    private void UpdateAuthenticationUi(SshAuthenticationMode mode)
    {
        if (PasswordPanel is null || PrivateKeyPanel is null)
        {
            return;
        }

        PasswordPanel.Visibility = mode == SshAuthenticationMode.Password
            ? Visibility.Visible
            : Visibility.Collapsed;
        PrivateKeyPanel.Visibility = mode == SshAuthenticationMode.PrivateKey
            ? Visibility.Visible
            : Visibility.Collapsed;
        UpdateRememberPasswordAvailability();
    }

    private void SaveProfileBox_Changed(object sender, RoutedEventArgs e) =>
        UpdateRememberPasswordAvailability();

    private void UpdateRememberPasswordAvailability()
    {
        if (RememberPasswordBox is null || SaveProfileBox is null ||
            ProtocolBox is null || SshAuthBox is null)
        {
            return;
        }

        var canRemember = SaveProfileBox.IsChecked == true &&
                          ProtocolBox.SelectedItem is ConnectionProtocol.Ssh &&
                          SshAuthBox.SelectedValue is SshAuthenticationMode.Password;
        RememberPasswordBox.Visibility = canRemember
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void UpdateRtsAvailability(Handshake handshake)
    {
        if (RtsEnableBox is null)
        {
            return;
        }

        var controlledByHandshake = handshake is Handshake.RequestToSend or Handshake.RequestToSendXOnXOff;
        RtsEnableBox.IsEnabled = !controlledByHandshake;
        RtsEnableBox.ToolTip = controlledByHandshake
            ? "RTS 由所选硬件流控自动管理"
            : "手动控制 RTS 信号";
    }

    private void TerminalAppearance_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new TerminalSettingsDialog(_terminalAppearance) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Result is not null)
        {
            _terminalAppearance = dialog.Result;
        }
    }

    private void BrowsePrivateKey_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择 SSH 私钥",
            Filter = "SSH 私钥|id_*;*.pem;*.key;*.ppk|所有文件|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (File.Exists(PrivateKeyPathBox.Text))
        {
            dialog.InitialDirectory = Path.GetDirectoryName(PrivateKeyPathBox.Text);
            dialog.FileName = Path.GetFileName(PrivateKeyPathBox.Text);
        }

        if (dialog.ShowDialog(this) == true)
        {
            PrivateKeyPathBox.Text = dialog.FileName;
        }
    }

    private void Connect_Click(object sender, RoutedEventArgs e)
    {
        ValidationText.Text = string.Empty;
        if (ProtocolBox.SelectedItem is not ConnectionProtocol protocol)
        {
            ValidationText.Text = "请选择连接协议。";
            return;
        }

        if (string.IsNullOrWhiteSpace(NameBox.Text))
        {
            ValidationText.Text = "请输入会话名称。";
            return;
        }

        var profile = _source?.Clone() ?? new SessionProfile();
        profile.Name = NameBox.Text.Trim();
        profile.Protocol = protocol;
        if (!TryParseNumber(ScrollbackLinesBox.Text, 0, 1_000_000, out var scrollbackLines))
        {
            ValidationText.Text = "终端滚屏行数必须是 0 ~ 1000000 之间的整数。";
            ScrollbackLinesBox.Focus();
            return;
        }
        profile.ScrollbackLines = scrollbackLines;
        profile.TerminalFontFamily = _terminalAppearance.FontFamily;
        profile.TerminalFontSize = _terminalAppearance.FontSize;
        profile.TerminalTheme = _terminalAppearance.Theme;
        profile.TerminalCursorStyle = _terminalAppearance.CursorStyle;
        profile.TerminalCursorBlink = _terminalAppearance.CursorBlink;
        profile.TerminalPreviewText = _terminalAppearance.PreviewText;

        if (protocol == ConnectionProtocol.Serial)
        {
            if (string.IsNullOrWhiteSpace(SerialPortBox.Text))
            {
                ValidationText.Text = "请选择或输入串口名称。";
                return;
            }
            if (!TryParseNumber(BaudRateBox.Text, 1, 10_000_000, out var baudRate))
            {
                ValidationText.Text = "波特率必须是大于 0 的整数（1 ~ 10000000）。";
                BaudRateBox.Focus();
                return;
            }
            if (DataBitsBox.SelectedItem is not int dataBits ||
                ParityBox.SelectedValue is not Parity parity ||
                StopBitsBox.SelectedValue is not StopBits stopBits ||
                HandshakeBox.SelectedValue is not Handshake handshake)
            {
                ValidationText.Text = "请选择数据位、校验位、停止位和流控方式。";
                return;
            }

            profile.SerialPort = SerialPortBox.Text.Trim();
            profile.BaudRate = baudRate;
            profile.DataBits = dataBits;
            profile.Parity = parity;
            profile.StopBits = stopBits;
            profile.Handshake = handshake;
            profile.DtrEnable = DtrEnableBox.IsChecked == true;
            profile.RtsEnable = RtsEnableBox.IsChecked == true;
        }
        else
        {
            if (string.IsNullOrWhiteSpace(HostBox.Text))
            {
                ValidationText.Text = "请输入主机名或 IP 地址。";
                return;
            }
            if (!int.TryParse(PortBox.Text, out var port) || port is < 1 or > 65535)
            {
                ValidationText.Text = "端口必须在 1 到 65535 之间。";
                return;
            }
            if (protocol == ConnectionProtocol.Ssh && string.IsNullOrWhiteSpace(UsernameBox.Text))
            {
                ValidationText.Text = "SSH 连接需要用户名。";
                return;
            }

            profile.Host = HostBox.Text.Trim();
            profile.Port = port;
            profile.Username = UsernameBox.Text.Trim();
        }

        if (protocol == ConnectionProtocol.Ssh)
        {
            if (SshAuthBox.SelectedValue is not SshAuthenticationMode mode)
            {
                ValidationText.Text = "请选择 SSH 认证方式。";
                return;
            }

            profile.SshAuthentication = mode;
            profile.PrivateKeyPath = PrivateKeyPathBox.Text.Trim();
            if (mode == SshAuthenticationMode.PrivateKey)
            {
                if (string.IsNullOrWhiteSpace(profile.PrivateKeyPath))
                {
                    ValidationText.Text = "请选择 SSH 私钥文件。";
                    return;
                }
                if (!File.Exists(profile.PrivateKeyPath))
                {
                    ValidationText.Text = "指定的 SSH 私钥文件不存在。";
                    return;
                }
                Password = PrivateKeyPassphraseBox.Password;
                profile.RememberPassword = false;
                profile.ProtectedPassword = string.Empty;
            }
            else
            {
                Password = PasswordBox.Password;
                profile.RememberPassword = SaveProfile && RememberPasswordBox.IsChecked == true;
                if (profile.RememberPassword)
                {
                    try
                    {
                        profile.ProtectedPassword = SecretProtector.Protect(Password);
                    }
                    catch (Exception exception)
                    {
                        ValidationText.Text = $"密码加密失败：{exception.Message}";
                        return;
                    }
                }
                else
                {
                    profile.ProtectedPassword = string.Empty;
                }
            }
        }
        else
        {
            Password = string.Empty;
            profile.RememberPassword = false;
            profile.ProtectedPassword = string.Empty;
        }

        ResultProfile = profile;
        DialogResult = true;
    }

    private sealed record EnumOption<T>(string Display, T Value) where T : struct, Enum;
}
