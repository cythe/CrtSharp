using System.IO.Ports;
using System.Text.Json.Serialization;

namespace CrtSharp.Models;

public enum ConnectionProtocol
{
    Ssh,
    Telnet,
    Serial
}

public enum SshAuthenticationMode
{
    Password,
    PrivateKey
}

public sealed class SessionProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "新会话";
    public ConnectionProtocol Protocol { get; set; } = ConnectionProtocol.Ssh;
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 22;
    public string Username { get; set; } = string.Empty;
    public SshAuthenticationMode SshAuthentication { get; set; } = SshAuthenticationMode.Password;
    public string PrivateKeyPath { get; set; } = string.Empty;
    public bool RememberPassword { get; set; }
    public string ProtectedPassword { get; set; } = string.Empty;
    public string SerialPort { get; set; } = string.Empty;
    public int BaudRate { get; set; } = 115200;
    public int DataBits { get; set; } = 8;
    public Parity Parity { get; set; } = Parity.None;
    public StopBits StopBits { get; set; } = StopBits.One;
    public Handshake Handshake { get; set; } = Handshake.None;
    public bool DtrEnable { get; set; } = true;
    public bool RtsEnable { get; set; } = true;
    public int ScrollbackLines { get; set; } = 200_000;
    public string TerminalFontFamily { get; set; } = "Cascadia Mono";
    public int TerminalFontSize { get; set; } = 14;
    public TerminalColorTheme TerminalTheme { get; set; } = TerminalColorTheme.Dark;
    public TerminalCursorStyle TerminalCursorStyle { get; set; } = TerminalCursorStyle.Block;
    public bool TerminalCursorBlink { get; set; } = true;
    public string TerminalPreviewText { get; set; } = "user@host:~$ echo CrtSharp 终端预览";

    [JsonIgnore]
    public string DisplayTarget => Protocol == ConnectionProtocol.Serial
        ? $"{SerialPort} @ {BaudRate} {DataBits}{ParityCode(Parity)}{StopBitsCode(StopBits)}"
        : $"{Host}:{Port}";

    public SessionProfile Clone() => new()
    {
        Id = Id,
        Name = Name,
        Protocol = Protocol,
        Host = Host,
        Port = Port,
        Username = Username,
        SshAuthentication = SshAuthentication,
        PrivateKeyPath = PrivateKeyPath,
        RememberPassword = RememberPassword,
        ProtectedPassword = ProtectedPassword,
        SerialPort = SerialPort,
        BaudRate = BaudRate,
        DataBits = DataBits,
        Parity = Parity,
        StopBits = StopBits,
        Handshake = Handshake,
        DtrEnable = DtrEnable,
        RtsEnable = RtsEnable,
        ScrollbackLines = ScrollbackLines,
        TerminalFontFamily = TerminalFontFamily,
        TerminalFontSize = TerminalFontSize,
        TerminalTheme = TerminalTheme,
        TerminalCursorStyle = TerminalCursorStyle,
        TerminalCursorBlink = TerminalCursorBlink,
        TerminalPreviewText = TerminalPreviewText
    };

    public TerminalAppearanceSettings GetTerminalAppearance() => new(
        TerminalFontFamily,
        TerminalFontSize,
        TerminalTheme,
        TerminalCursorStyle,
        TerminalCursorBlink,
        TerminalPreviewText);

    private static string ParityCode(Parity parity) => parity switch
    {
        Parity.Even => "E",
        Parity.Odd => "O",
        Parity.Mark => "M",
        Parity.Space => "S",
        _ => "N"
    };

    private static string StopBitsCode(StopBits stopBits) => stopBits switch
    {
        StopBits.OnePointFive => "1.5",
        StopBits.Two => "2",
        _ => "1"
    };
}
