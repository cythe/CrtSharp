namespace CrtSharp.Connections;

public interface ITerminalConnection : IAsyncDisposable
{
    bool IsConnected { get; }
    event EventHandler<byte[]>? DataReceived;
    event EventHandler<string>? StatusChanged;
    event EventHandler<Exception>? Faulted;

    Task ConnectAsync(CancellationToken cancellationToken);
    Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);
    Task ResizeAsync(int columns, int rows, CancellationToken cancellationToken = default);
    Task DisconnectAsync();
}

public sealed record HostKeyDetails(
    string Host,
    int Port,
    string Algorithm,
    string Fingerprint);
