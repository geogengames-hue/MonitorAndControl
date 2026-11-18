using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

internal static class CodeSigningTests
{
    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, "sign.ps1")) && File.Exists(Path.Combine(dir, "publish.ps1")))
            {
                return dir;
            }
            dir = Path.GetDirectoryName(dir);
        }
        throw new DirectoryNotFoundException("Could not find the repository root (sign.ps1).");
    }

    private static readonly string RepoRoot = FindRepoRoot();

    public static Task NoSigningKeyIsCommittedToTheRepository()
    {
        // A private key in the repository would let anyone sign programs with it.
        var keyFiles = Directory.EnumerateFiles(RepoRoot, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".pfx", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".p12", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".snk", StringComparison.OrdinalIgnoreCase))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}"))
            .ToList();

        if (keyFiles.Count > 0)
        {
            throw new InvalidOperationException("Signing key file(s) found in the repository: " + string.Join(", ", keyFiles));
        }

        return Task.CompletedTask;
    }

    public static Task SignScriptHasNoFallbackCertificateOrCommandLinePassword()
    {
        string script = File.ReadAllText(Path.Combine(RepoRoot, "sign.ps1"));

        if (script.Contains("sample-code-signing", StringComparison.OrdinalIgnoreCase)
            || script.Contains("MonitorAndControlSample", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("sign.ps1 must not fall back to a built-in certificate.");
        }

        if (script.Contains("'/p'", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("sign.ps1 must not pass the certificate password to signtool on its command line (/p).");
        }

        return Task.CompletedTask;
    }

    public static Task Base64CertEnvironmentVariableFormat()
    {
        // Same format as the CODE_SIGN_CERT_BASE64 secret: Base64 of a .pfx file protected by a password.
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=Test Only", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        const string password = "test-only-password";
        string base64 = Convert.ToBase64String(created.Export(X509ContentType.Pfx, password));

        using var roundtrip = new X509Certificate2(Convert.FromBase64String(base64), password);
        if (!roundtrip.HasPrivateKey || roundtrip.Thumbprint != created.Thumbprint)
        {
            throw new InvalidOperationException("Failed to load the certificate from the Base64 decoded bytes.");
        }

        return Task.CompletedTask;
    }
}
