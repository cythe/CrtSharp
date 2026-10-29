using System.IO;
using System.Text;

namespace CrtSharp.Services;

public sealed class TerminalLogWriter : IDisposable
{
    private readonly object _sync = new();
    private readonly BufferedStream _stream;
    private bool _disposed;

    public TerminalLogWriter(string sessionName)
    {
        var dayDirectory = Path.Combine(LogsRoot, DateTime.Now.ToString("yyyy-MM-dd"));
        Directory.CreateDirectory(dayDirectory);

        var safeName = MakeSafeFileName(sessionName);
        var suffix = Guid.NewGuid().ToString("N")[..6];
        FilePath = Path.Combine(
            dayDirectory,
            $"{DateTime.Now:HHmmss}-{safeName}-{suffix}.log");

        _stream = new BufferedStream(new FileStream(
            FilePath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read,
            16 * 1024,
            FileOptions.SequentialScan));
        WriteText($"# CrtSharp 会话日志\r\n# 开始时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss zzz}\r\n\r\n");
    }

    public static string LogsRoot
    {
        get
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "CrtSharp",
                "Logs");
            Directory.CreateDirectory(path);
            return path;
        }
    }

    public string FilePath { get; }

    public void Write(ReadOnlySpan<byte> data)
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }
            _stream.Write(data);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            WriteTextCore($"\r\n# 结束时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss zzz}\r\n");
            _stream.Flush();
            _stream.Dispose();
            _disposed = true;
        }
    }

    private void WriteText(string text)
    {
        lock (_sync)
        {
            WriteTextCore(text);
        }
    }

    private void WriteTextCore(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        _stream.Write(bytes);
    }

    private static string MakeSafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var filtered = new string(value
            .Select(character => invalid.Contains(character) ? '_' : character)
            .ToArray())
            .Trim();
        if (string.IsNullOrWhiteSpace(filtered))
        {
            filtered = "session";
        }
        return filtered.Length <= 64 ? filtered : filtered[..64];
    }
}
