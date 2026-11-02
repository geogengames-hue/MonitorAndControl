using System.Security.Cryptography;
using System.Text;
using MonitorAndControl.Data;

namespace MonitorAndControl.Services;

/// <summary>
/// Single-use links for answering "Not now" to a request for more PC time straight from a phone alert,
/// without signing in to the dashboard. A link can only decline that one request, which grants nothing,
/// so a link that leaks is harmless. Adding time still needs the dashboard login and the remote PIN.
/// </summary>
public static class QuickActions
{
    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public static bool LooksValid(string? token) =>
        token is { Length: >= 20 and <= 100 } && token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    /// <summary>Creates a token that declines this request; only its hash is stored.</summary>
    public static async Task<string> CreateDeclineAsync(UsageDatabase db, string userSid, string requestId, DateTimeOffset expiresUtc)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        await db.AddQuickActionAsync(Hash(token), userSid, requestId, expiresUtc);
        return token;
    }
}
