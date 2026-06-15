using System.Security.Cryptography;
using System.Text;

namespace MonitorAndControl.Services;

public static class SecretProtector
{
    private const string Prefix = "dpapi:";

    public static string Protect(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "";

        var bytes = Encoding.UTF8.GetBytes(value);
        var protectedBytes = ProtectedData.Protect(bytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
        return Prefix + Convert.ToBase64String(protectedBytes);
    }

    public static string Unprotect(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "";

        if (!value.StartsWith(Prefix, StringComparison.Ordinal))
            return value;

        try
        {
            var protectedBytes = Convert.FromBase64String(value[Prefix.Length..]);
            var bytes = ProtectedData.Unprotect(protectedBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return "";
        }
    }
}
