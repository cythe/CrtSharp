using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CrtSharp.Models;

namespace CrtSharp.Services;

/// <summary>
/// 导出/导入用户配置（会话 + known-hosts + 私钥），导出文件整体用
/// AES-256-GCM + PBKDF2(SHA256) 加密，文本编辑器打开只见密文。
/// </summary>
public static class ConfigTransferService
{
    private const string FormatMarker = "CrtSharpConfig";
    private const int Version = 1;
    private const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;
    private const int Pbkdf2Iterations = 210_000;
    private const int Pbkdf2IterationsMinimum = 60_000;
    private const string KdfName = "PBKDF2-SHA256";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    // ---------- 导出文件信封（明文部分只有 KDF 参数与密文） ----------

    private sealed class ExportEnvelope
    {
        public string Format { get; set; } = FormatMarker;
        public int FormatVersion { get; set; } = Version;
        public string Kdf { get; set; } = KdfName;
        public int Iterations { get; set; }
        public string Salt { get; set; } = string.Empty;
        public string Nonce { get; set; } = string.Empty;
        public string Ciphertext { get; set; } = string.Empty;
    }

    /// <summary>加密包内的明文载荷。</summary>
    public sealed class ConfigPayload
    {
        public List<SessionProfile> Sessions { get; set; } = [];
        public Dictionary<string, string> KnownHosts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>会话 Id → 明文密码（仅 RememberPassword 的会话，包整体加密）。</summary>
        public Dictionary<string, string> Passwords { get; set; } = [];
        /// <summary>私钥文件名 → Base64 内容。</summary>
        public Dictionary<string, string> PrivateKeys { get; set; } = [];
    }

    public sealed class ImportResult
    {
        public ConfigPayload Payload { get; init; } = new();
        /// <summary>导入的私钥文件写入路径（供恢复 PrivateKeyPath 用）。</summary>
        public Dictionary<string, string> RestoredKeyPaths { get; init; } = [];
    }

    // ---------- 导出 ----------

    public static void Export(string filePath, string password, IReadOnlyList<SessionProfile> profiles, KnownHostStore knownHosts)
    {
        var payload = new ConfigPayload
        {
            Sessions = profiles.Select(CloneForExport).ToList(),
            KnownHosts = new Dictionary<string, string>(knownHosts.Entries, StringComparer.OrdinalIgnoreCase)
        };

        foreach (var profile in profiles)
        {
            if (profile.RememberPassword && SecretProtector.TryUnprotect(profile.ProtectedPassword, out var plain))
            {
                payload.Passwords[profile.Id.ToString()] = plain;
            }
        }

        foreach (var path in profiles
            .Select(item => item.PrivateKeyPath)
            .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(path);
            if (!payload.PrivateKeys.ContainsKey(name))
            {
                payload.PrivateKeys[name] = Convert.ToBase64String(File.ReadAllBytes(path));
            }
        }

        var plaintext = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        try
        {
            var salt = RandomNumberGenerator.GetBytes(SaltSize);
            var nonce = RandomNumberGenerator.GetBytes(NonceSize);
            var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, KeySize);
            try
            {
                var ciphertext = new byte[plaintext.Length];
                var tag = new byte[TagSize];
                using var aes = new AesGcm(key, TagSize);
                aes.Encrypt(nonce, plaintext, ciphertext, tag);

                var envelope = new ExportEnvelope
                {
                    Iterations = Pbkdf2Iterations,
                    Salt = Convert.ToBase64String(salt),
                    Nonce = Convert.ToBase64String(nonce),
                    Ciphertext = Convert.ToBase64String([.. ciphertext, .. tag])
                };
                File.WriteAllText(filePath, JsonSerializer.Serialize(envelope, JsonOptions));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    /// <summary>导出前克隆：把 DPAPI 密文清空（明文密码单独放进 Passwords 字典）。</summary>
    private static SessionProfile CloneForExport(SessionProfile profile)
    {
        var clone = profile.Clone();
        clone.ProtectedPassword = string.Empty;
        return clone;
    }

    // ---------- 导入 ----------

    public static ImportResult Import(string filePath, string password)
    {
        var envelope = JsonSerializer.Deserialize<ExportEnvelope>(File.ReadAllText(filePath), JsonOptions)
            ?? throw new InvalidDataException("文件不是有效的 CrtSharp 配置导出文件。");
        if (!string.Equals(envelope.Format, FormatMarker, StringComparison.Ordinal))
        {
            throw new InvalidDataException("文件不是有效的 CrtSharp 配置导出文件。");
        }
        if (!string.Equals(envelope.Kdf, KdfName, StringComparison.Ordinal)
            || envelope.Iterations < Pbkdf2IterationsMinimum)
        {
            throw new InvalidDataException("不支持的加密参数，文件可能来自不兼容的版本。");
        }

        var sealedBytes = Convert.FromBase64String(envelope.Ciphertext);
        if (sealedBytes.Length < TagSize)
        {
            throw new InvalidDataException("导出文件已损坏。");
        }

        var salt = Convert.FromBase64String(envelope.Salt);
        var nonce = Convert.FromBase64String(envelope.Nonce);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, envelope.Iterations, HashAlgorithmName.SHA256, KeySize);
        try
        {
            var ciphertext = sealedBytes[..^TagSize];
            var tag = sealedBytes[^TagSize..];
            var plaintext = new byte[ciphertext.Length];
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, ciphertext, tag, plaintext); // 密码错误在这里抛 AuthenticationFailure
            try
            {
                var payload = JsonSerializer.Deserialize<ConfigPayload>(plaintext, JsonOptions)
                    ?? throw new InvalidDataException("配置内容为空。");

                var restored = RestorePrivateKeys(payload.PrivateKeys);
                return new ImportResult { Payload = payload, RestoredKeyPaths = restored };
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
        catch (CryptographicException exception)
        {
            throw new InvalidDataException("解密失败：密码错误或文件已损坏。", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>把包内私钥写入 %APPDATA%\CrtSharp\Keys\（已存在的不覆盖），返回 文件名 → 实际路径。</summary>
    private static Dictionary<string, string> RestorePrivateKeys(Dictionary<string, string> privateKeys)
    {
        var restored = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (privateKeys.Count == 0)
        {
            return restored;
        }

        var keysDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CrtSharp", "Keys");
        Directory.CreateDirectory(keysDirectory);

        foreach (var (name, base64) in privateKeys)
        {
            var safeName = Path.GetFileName(name);
            if (string.IsNullOrWhiteSpace(safeName))
            {
                continue;
            }
            var target = Path.Combine(keysDirectory, safeName);
            if (!File.Exists(target))
            {
                File.WriteAllBytes(target, Convert.FromBase64String(base64));
            }
            restored[safeName] = target;
        }
        return restored;
    }

    /// <summary>把明文密码重新用本机 DPAPI 保护，恢复到会话上。</summary>
    public static void ApplyPasswords(ConfigPayload payload, IEnumerable<SessionProfile> profiles)
    {
        foreach (var profile in profiles)
        {
            if (payload.Passwords.TryGetValue(profile.Id.ToString(), out var plain)
                && profile.RememberPassword)
            {
                profile.ProtectedPassword = SecretProtector.Protect(plain);
            }
        }
    }
}
