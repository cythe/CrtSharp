using System.IO;
using System.Text.Json;

namespace CrtSharp.Services;

public enum HostKeyState
{
    Unknown,
    Match,
    Mismatch
}

public sealed class KnownHostStore
{
    private readonly string _filePath;
    private readonly Dictionary<string, string> _fingerprints;

    public KnownHostStore()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CrtSharp");
        Directory.CreateDirectory(directory);
        _filePath = Path.Combine(directory, "known-hosts.json");
        _fingerprints = Load();
    }

    public HostKeyState Check(string host, int port, string fingerprint)
    {
        if (!_fingerprints.TryGetValue(Key(host, port), out var known))
        {
            return HostKeyState.Unknown;
        }

        return string.Equals(known, fingerprint, StringComparison.Ordinal)
            ? HostKeyState.Match
            : HostKeyState.Mismatch;
    }

    public IReadOnlyDictionary<string, string> Entries => _fingerprints;

    public void Merge(IEnumerable<KeyValuePair<string, string>> entries)
    {
        var changed = false;
        foreach (var (key, fingerprint) in entries)
        {
            if (!_fingerprints.TryGetValue(key, out var known) || !string.Equals(known, fingerprint, StringComparison.Ordinal))
            {
                _fingerprints[key] = fingerprint;
                changed = true;
            }
        }
        if (!changed)
        {
            return;
        }
        var temporaryPath = _filePath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(
            _fingerprints,
            new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporaryPath, _filePath, overwrite: true);
    }

    public void Trust(string host, int port, string fingerprint)
    {
        _fingerprints[Key(host, port)] = fingerprint;
        var temporaryPath = _filePath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(
            _fingerprints,
            new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporaryPath, _filePath, overwrite: true);
    }

    private Dictionary<string, string> Load()
    {
        if (!File.Exists(_filePath))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            var values = JsonSerializer.Deserialize<Dictionary<string, string>>(
                File.ReadAllText(_filePath));
            return values is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static string Key(string host, int port) => $"{host.Trim().ToLowerInvariant()}:{port}";
}
