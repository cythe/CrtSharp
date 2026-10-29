using System.IO.Ports;
using CrtSharp.Models;

namespace CrtSharp.Connections;

public sealed class SerialTerminalConnection : ITerminalConnection
{
    private readonly SessionProfile _profile;
    private SerialPort? _port;

    public SerialTerminalConnection(SessionProfile profile)
    {
        _profile = profile;
    }

    public bool IsConnected => _port?.IsOpen == true;
    public event EventHandler<byte[]>? DataReceived;
    public event EventHandler<string>? StatusChanged;
    public event EventHandler<Exception>? Faulted;

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        var port = new SerialPort(
            _profile.SerialPort,
            _profile.BaudRate,
            _profile.Parity,
            _profile.DataBits,
            _profile.StopBits)
        {
            Handshake = _profile.Handshake,
            DtrEnable = _profile.DtrEnable,
            ReadTimeout = 500,
            WriteTimeout = 2000
        };

        if (_profile.Handshake is not Handshake.RequestToSend and
            not Handshake.RequestToSendXOnXOff)
        {
            port.RtsEnable = _profile.RtsEnable;
        }

        port.DataReceived += PortOnDataReceived;
        _port = port;
        StatusChanged?.Invoke(this, $"正在打开串口 {_profile.DisplayTarget}…");
        try
        {
            await Task.Run(port.Open, cancellationToken).ConfigureAwait(false);
            StatusChanged?.Invoke(this, "已连接");
        }
        catch
        {
            port.DataReceived -= PortOnDataReceived;
            port.Dispose();
            if (ReferenceEquals(_port, port))
            {
                _port = null;
            }
            throw;
        }
    }

    public async Task SendAsync(
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default)
    {
        var port = _port;
        if (port?.IsOpen != true)
        {
            throw new InvalidOperationException("串口尚未打开。");
        }

        await port.BaseStream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
        await port.BaseStream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task ResizeAsync(
        int columns,
        int rows,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task DisconnectAsync()
    {
        var port = Interlocked.Exchange(ref _port, null);
        if (port is not null)
        {
            port.DataReceived -= PortOnDataReceived;
            try
            {
                if (port.IsOpen)
                {
                    port.Close();
                }
            }
            finally
            {
                port.Dispose();
            }
        }

        StatusChanged?.Invoke(this, "已断开");
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync().ConfigureAwait(false);

    private void PortOnDataReceived(object sender, SerialDataReceivedEventArgs args)
    {
        try
        {
            var port = _port;
            if (port?.IsOpen != true)
            {
                return;
            }

            var count = port.BytesToRead;
            if (count <= 0)
            {
                return;
            }

            var buffer = new byte[count];
            var read = port.Read(buffer, 0, buffer.Length);
            if (read > 0)
            {
                DataReceived?.Invoke(this, buffer[..read]);
            }
        }
        catch (Exception exception)
        {
            Faulted?.Invoke(this, exception);
        }
    }
}
