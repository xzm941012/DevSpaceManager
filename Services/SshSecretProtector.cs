using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DevSpaceManager.Services;

internal static class SshSecretProtector
{
    private const string Prefix = "dpapi:v1:";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("DevSpaceManager.SSH.v1");

    public static string Protect(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser);
        return Prefix + Convert.ToBase64String(bytes);
    }

    public static string Unprotect(string protectedValue)
    {
        if (string.IsNullOrWhiteSpace(protectedValue)) return "";
        if (!protectedValue.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return protectedValue;
        }

        try
        {
            var bytes = Convert.FromBase64String(protectedValue[Prefix.Length..]);
            var plain = ProtectedData.Unprotect(bytes, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            throw new InvalidOperationException("SSH 凭据无法解密，请在设置中重新输入并保存。", ex);
        }
    }

    public static string TakeLegacyPlaintext(DevSpaceManager.Core.SshProfileConfig profile, string name)
    {
        if (profile.LegacyPlaintextFields is null ||
            !profile.LegacyPlaintextFields.TryGetValue(name, out var value) ||
            value.ValueKind != JsonValueKind.String)
        {
            return "";
        }

        profile.LegacyPlaintextFields.Remove(name);
        return value.GetString() ?? "";
    }

    public static int? TakeLegacyInt(DevSpaceManager.Core.SshProfileConfig profile, string name)
    {
        if (profile.LegacyPlaintextFields is null ||
            !profile.LegacyPlaintextFields.TryGetValue(name, out var value))
        {
            return null;
        }

        profile.LegacyPlaintextFields.Remove(name);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : null;
    }
}
