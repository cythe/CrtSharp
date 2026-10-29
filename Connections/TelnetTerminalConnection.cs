using System.Net.Sockets;
using System.Text;
using CrtSharp.Models;

namespace CrtSharp.Connections;

public sealed class TelnetTerminalConnection : ITerminalConnection
{
    private const byte Se = 240;
    private const byte Sb = 250;
    private const byte Will = 251;
    private const byte Wont = 252;
    private const byte Do = 253;
    private const byte Dont = 254;
    private const byte Iac = 255;
    private const byte Binary = 0;
    private const byte Echo = 1;
    private const byte SuppressGoAhead = 3;
    private const byte TerminalType = 24;
    private const byte Naws = 31;

    private readonly SessionProfile _profile;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private TcpClient? _client;
    private NetworkStream? _stream;
    private Task? _readTask;
    private ParseState _state;
    private byte _command;
    private readonly List<byte> _subnegotiation = [];
    private bool _nawsEnabled;
    private int _columns = 120;
    private int _rows = 30;

    public TelnetTerminalConnection(SessionProfile profile)
    {
        _profile = profile;
    }

    public bool IsConnected => _client?.Connected == true && _stream is not null;
    public event EventHandler<byte[]>? DataReceived;
    public event EventHandler<string>? StatusChanged;
    public event EventHandler<Exception>? Faulted;

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        StatusChanged?.Invoke(this, "正在建立 Telnet 连接…");
        var client = new TcpClient { NoDelay = true };
        _client = client;
        await client.ConnectAsync(_profile.Host, _profile.Port, cancellationToken)
            .ConfigureAwait(false);
        _stream = client.GetStream();
        StatusChanged?.Invoke(this, "已连接（Telnet 不加密）");
        _readTask = ReadLoopAsync(_lifetime.Token);
    }

    public async Task SendAsync(
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default)
    {
        var escaped = new List<byte>(data.Length);
        var input = data.ToArray();
        foreach (var value in input)
        {
            escaped.Add(value);
            if (value == Iac)
            {
                escaped.Add(Iac);
            }
        }

        await WriteRawAsync(escaped.ToArray(), cancellationToken).ConfigureAwait(false);
    }

    public async Task ResizeAsync(
        int columns,
        int rows,
        CancellationToken cancellationToken = default)
    {
        _columns = Math.Clamp(columns, 2, ushort.MaxValue);
        _rows = Math.Clamp(rows, 1, ushort.MaxValue);
        if (_nawsEnabled)
        {
            await SendWindowSizeAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public Task DisconnectAsync()
    {
        _lifetime.Cancel();
        _stream?.Dispose();
        _stream = null;
        _client?.Dispose();
        _client = null;
        StatusChanged?.Invoke(this, "已断开");
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        _writeLock.Dispose();
        _lifetime.Dispose();
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        try
        {
            while (!cancellationToken.IsCancellationRequested && _stream is not null)
            {
                var count = await _stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (count == 0)
                {
                    break;
                }

                var payload = await ParseAsync(buffer.AsMemory(0, count), cancellationToken)
                    .ConfigureAwait(false);
                if (payload.Length > 0)
                {
                    DataReceived?.Invoke(this, payload);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Faulted?.Invoke(this, exception);
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                StatusChanged?.Invoke(this, "远程主机已断开");
            }
        }
    }

    private async Task<byte[]> ParseAsync(
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken)
    {
        var output = new List<byte>(bytes.Length);
        var input = bytes.ToArray();
        foreach (var value in input)
        {
            switch (_state)
            {
                case ParseState.Data:
                    if (value == Iac)
                    {
                        _state = ParseState.Iac;
                    }
                    else
                    {
                        output.Add(value);
                    }
                    break;

                case ParseState.Iac:
                    if (value == Iac)
                    {
                        output.Add(Iac);
                        _state = ParseState.Data;
                    }
                    else if (value is Will or Wont or Do or Dont)
                    {
                        _command = value;
                        _state = ParseState.Option;
                    }
                    else if (value == Sb)
                    {
                        _subnegotiation.Clear();
                        _state = ParseState.Subnegotiation;
                    }
                    else
                    {
                        _state = ParseState.Data;
                    }
                    break;

                case ParseState.Option:
                    await HandleNegotiationAsync(_command, value, cancellationToken)
                        .ConfigureAwait(false);
                    _state = ParseState.Data;
                    break;

                case ParseState.Subnegotiation:
                    if (value == Iac)
                    {
                        _state = ParseState.SubnegotiationIac;
                    }
                    else
                    {
                        _subnegotiation.Add(value);
                    }
                    break;

                case ParseState.SubnegotiationIac:
                    if (value == Se)
                    {
                        await HandleSubnegotiationAsync(cancellationToken).ConfigureAwait(false);
                        _state = ParseState.Data;
                    }
                    else
                    {
                        _subnegotiation.Add(Iac);
                        if (value != Iac)
                        {
                            _subnegotiation.Add(value);
                        }
                        _state = ParseState.Subnegotiation;
                    }
                    break;
            }
        }

        return output.ToArray();
    }

    private async Task HandleNegotiationAsync(
        byte command,
        byte option,
        CancellationToken cancellationToken)
    {
        byte response;
        if (command == Do)
        {
            var supported = option is Binary or SuppressGoAhead or TerminalType or Naws;
            response = supported ? Will : Wont;
            if (option == Naws && supported)
            {
                _nawsEnabled = true;
            }
        }
        else if (command == Will)
        {
            var supported = option is Binary or Echo or SuppressGoAhead;
            response = supported ? Do : Dont;
        }
        else
        {
            return;
        }

        await WriteRawAsync([Iac, response, option], cancellationToken).ConfigureAwait(false);
        if (option == Naws && _nawsEnabled)
        {
            await SendWindowSizeAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task HandleSubnegotiationAsync(CancellationToken cancellationToken)
    {
        if (_subnegotiation.Count >= 2 &&
            _subnegotiation[0] == TerminalType &&
            _subnegotiation[1] == 1)
        {
            var terminal = Encoding.ASCII.GetBytes("xterm-256color");
            var response = new List<byte> { Iac, Sb, TerminalType, 0 };
            response.AddRange(terminal);
            response.Add(Iac);
            response.Add(Se);
            await WriteRawAsync(response.ToArray(), cancellationToken).ConfigureAwait(false);
        }
    }

    private Task SendWindowSizeAsync(CancellationToken cancellationToken)
    {
        byte High(int value) => (byte)((value >> 8) & 0xff);
        byte Low(int value) => (byte)(value & 0xff);
        return WriteRawAsync(
            [Iac, Sb, Naws, High(_columns), Low(_columns), High(_rows), Low(_rows), Iac, Se],
            cancellationToken);
    }

    private async Task WriteRawAsync(byte[] data, CancellationToken cancellationToken)
    {
        var stream = _stream ?? throw new InvalidOperationException("Telnet 会话尚未连接。");
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private enum ParseState
    {
        Data,
        Iac,
        Option,
        Subnegotiation,
        SubnegotiationIac
    }
}
