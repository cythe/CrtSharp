using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using CrtSharp.Models;

namespace CrtSharp.Services;

public sealed class SessionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _filePath;

    public SessionStore()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CrtSharp");
        Directory.CreateDirectory(directory);
        _filePath = Path.Combine(directory, "sessions.json");
    }

    public IReadOnlyList<SessionProfile> Load()
    {
        if (!File.Exists(_filePath))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<SessionProfile>>(
                File.ReadAllText(_filePath), JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            var backup = _filePath + ".invalid-" + DateTime.Now.ToString("yyyyMMddHHmmss");
            File.Copy(_filePath, backup, overwrite: false);
            return [];
        }
    }

    public void Save(IEnumerable<SessionProfile> profiles)
    {
        var temporaryPath = _filePath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(profiles, JsonOptions));
        File.Move(temporaryPath, _filePath, overwrite: true);
    }
}
