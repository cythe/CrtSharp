using System.Security.Cryptography;
using System.Text;

namespace CrtSharp.Services;

public static class SecretProtector
{
    private static readonly byte[] Entropy =
        Encoding.UTF8.GetBytes("CrtSharp/session-password/v1");

    public static string Protect(string secret)
    {
        var plaintext = Encoding.UTF8.GetBytes(secret);
        try
        {
            var protectedBytes = ProtectedData.Protect(
                plaintext,
                Entropy,
                DataProtectionScope.CurrentUser);
            try
            {
                return Convert.ToBase64String(protectedBytes);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(protectedBytes);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public static bool TryUnprotect(string protectedSecret, out string secret)
    {
        secret = string.Empty;
        if (string.IsNullOrWhiteSpace(protectedSecret))
        {
            return false;
        }

        byte[]? encrypted = null;
        byte[]? plaintext = null;
        try
        {
            encrypted = Convert.FromBase64String(protectedSecret);
            plaintext = ProtectedData.Unprotect(
                encrypted,
                Entropy,
                DataProtectionScope.CurrentUser);
            secret = Encoding.UTF8.GetString(plaintext);
            return true;
        }
        catch (Exception exception) when (
            exception is FormatException or CryptographicException or PlatformNotSupportedException)
        {
            return false;
        }
        finally
        {
            if (encrypted is not null)
            {
                CryptographicOperations.ZeroMemory(encrypted);
            }
            if (plaintext is not null)
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
    }
}
