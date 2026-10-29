using System.IO;
using CrtSharp.Models;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace CrtSharp.Connections;

public sealed class SshTerminalConnection : ITerminalConnection
{
    private readonly SessionProfile _profile;
    private readonly string _secret;
    private readonly Func<HostKeyDetails, bool> _hostKeyVerifier;
    private readonly CancellationTokenSource _lifetime = new();
    private SshClient? _client;
    private ShellStream? _shell;
    private PrivateKeyFile? _privateKeyFile;
    private Task? _readTask;
    private int _columns = 120;
    private int _rows = 30;
    private bool _disconnectRaised;
    private volatile bool _isConnected;

    public SshTerminalConnection(
        SessionProfile profile,
        string secret,
        Func<HostKeyDetails, bool> hostKeyVerifier)
    {
        _profile = profile;
        _secret = secret;
        _hostKeyVerifier = hostKeyVerifier;
    }

    public bool IsConnected => _isConnected;
    public event EventHandler<byte[]>? DataReceived;
    public event EventHandler<string>? StatusChanged;
    public event EventHandler<Exception>? Faulted;

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        var authentication = CreateAuthenticationMethod();
        var connectionInfo = new ConnectionInfo(
            _profile.Host,
            _profile.Port,
            _profile.Username,
            authentication)
        {
            Timeout = TimeSpan.FromSeconds(20)
        };

        var client = new SshClient(connectionInfo);
        client.HostKeyReceived += (_, args) =>
        {
            var details = new HostKeyDetails(
                _profile.Host,
                _profile.Port,
                args.HostKeyName,
                args.FingerPrintSHA256);
            args.CanTrust = _hostKeyVerifier(details);
        };

        _client = client;
        var authenticationName = _profile.SshAuthentication == SshAuthenticationMode.PrivateKey
            ? "私钥"
            : "密码";
        StatusChanged?.Invoke(this, $"正在建立 SSH 连接（{authenticationName}认证）…");

        using var registration = cancellationToken.Register(client.Dispose);
        await Task.Run(client.Connect, cancellationToken).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        var shell = client.CreateShellStream(
            "xterm-256color",
            (uint)_columns,
            (uint)_rows,
            0,
            0,
            64 * 1024);
        _shell = shell;
        _isConnected = true;
        StatusChanged?.Invoke(this, "已连接");
        _readTask = ReadLoopAsync(shell, _lifetime.Token);
    }

    public async Task SendAsync(
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default)
    {
        var shell = _shell;
        if (!_isConnected || shell is null)
        {
            return;
        }

        try
        {
            await shell.WriteAsync(data, cancellationToken).ConfigureAwait(false);
            await shell.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsNormalRemoteClose(exception))
        {
            MarkRemoteClosed();
        }
    }

    public Task ResizeAsync(
        int columns,
        int rows,
        CancellationToken cancellationToken = default)
    {
        _columns = Math.Max(2, columns);
        _rows = Math.Max(1, rows);
        if (!_isConnected || _shell is null)
        {
            return Task.CompletedTask;
        }

        try
        {
            _shell.ChangeWindowSize((uint)_columns, (uint)_rows, 0, 0);
        }
        catch (Exception exception) when (IsNormalRemoteClose(exception))
        {
            MarkRemoteClosed();
        }
        return Task.CompletedTask;
    }

    public async Task DisconnectAsync()
    {
        if (_disconnectRaised)
        {
            return;
        }

        _disconnectRaised = true;
        _isConnected = false;
        _lifetime.Cancel();
        _shell?.Dispose();
        _shell = null;

        if (_client is not null)
        {
            await Task.Run(() =>
            {
                if (_client.IsConnected)
                {
                    _client.Disconnect();
                }
                _client.Dispose();
            }).ConfigureAwait(false);
            _client = null;
        }

        _privateKeyFile?.Dispose();
        _privateKeyFile = null;
        StatusChanged?.Invoke(this, "已断开");
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        _lifetime.Dispose();
    }

    private AuthenticationMethod CreateAuthenticationMethod()
    {
        if (_profile.SshAuthentication == SshAuthenticationMode.Password)
        {
            return new PasswordAuthenticationMethod(_profile.Username, _secret);
        }

        if (string.IsNullOrWhiteSpace(_profile.PrivateKeyPath))
        {
            throw new InvalidOperationException("未指定 SSH 私钥文件。");
        }

        _privateKeyFile = string.IsNullOrEmpty(_secret)
            ? new PrivateKeyFile(_profile.PrivateKeyPath)
            : new PrivateKeyFile(_profile.PrivateKeyPath, _secret);
        return new PrivateKeyAuthenticationMethod(_profile.Username, _privateKeyFile);
    }

    private async Task ReadLoopAsync(ShellStream shell, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var count = await shell.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (count == 0)
                {
                    break;
                }

                DataReceived?.Invoke(this, buffer[..count].ToArray());
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (IsNormalRemoteClose(exception))
        {
        }
        catch (Exception exception)
        {
            Faulted?.Invoke(this, exception);
        }
        finally
        {
            _isConnected = false;
            if (ReferenceEquals(_shell, shell))
            {
                _shell = null;
            }
            shell.Dispose();
            if (!_disconnectRaised)
            {
                StatusChanged?.Invoke(this, "远程主机已断开");
            }
        }
    }

    private void MarkRemoteClosed()
    {
        if (!_isConnected)
        {
            return;
        }

        _isConnected = false;
        StatusChanged?.Invoke(this, "远程主机已断开");
    }

    private static bool IsNormalRemoteClose(Exception exception) =>
        exception is ObjectDisposedException or IOException or SshConnectionException;
}
