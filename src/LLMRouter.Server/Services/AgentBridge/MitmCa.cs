using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace LLMRouter.Server.Services.AgentBridge;

/// <summary>
/// MITM root-CA lifecycle: generate, persist (PEM), regenerate, download, and
/// per-host leaf signing for TLS interception — port of src/mitm/cert/*.
/// Data dir: $HOME/.llmrouter/mitm (override LLMR_MITM_DATA_DIR).
/// </summary>
public sealed class MitmCa
{
    private readonly string _dir;
    private readonly ConcurrentDictionary<string, X509Certificate2> _leafCache = new();

    public MitmCa(string? dataDir = null)
    {
        _dir = dataDir ?? Path.Combine(
            Environment.GetEnvironmentVariable("LLMR_MITM_DATA_DIR")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".llmrouter"),
            "mitm");
    }

    public string CertPath => Path.Combine(_dir, "ca.crt");
    public string KeyPath => Path.Combine(_dir, "ca.key");
    public bool CertExists => File.Exists(CertPath) && File.Exists(KeyPath);

    public (X509Certificate2 cert, RSA key) Generate()
    {
        Directory.CreateDirectory(_dir);
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(
            "CN=LLMRouter MITM Root CA, O=LLMRouter, C=BR",
            rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
        var ca = req.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
        File.WriteAllText(CertPath, ca.ExportCertificatePem());
        File.WriteAllText(KeyPath, rsa.ExportPkcs8PrivateKeyPem());
        _leafCache.Clear();
        return (ca, rsa);
    }

    public (X509Certificate2 cert, RSA key)? Load()
    {
        if (!CertExists) return null;
        var cert = X509Certificate2.CreateFromPemFile(CertPath, KeyPath);
        return (cert, (RSA)cert.GetRSAPrivateKey()!);
    }

    public (X509Certificate2 cert, RSA key) Ensure()
        => Load() ?? Generate();

    /// <summary>Sign a leaf cert for <paramref name="host"/> (cached).</summary>
    public X509Certificate2 LeafFor(string host)
    {
        if (_leafCache.TryGetValue(host, out var cached))
            return cached;
        var (ca, _) = Ensure();
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(
            $"CN={host}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(host);
        req.CertificateExtensions.Add(san.Build());
        var leaf = req.Create(ca, DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(2), RandomNumberGenerator.GetBytes(16));
        var withKey = leaf.CopyWithPrivateKey(rsa);
        _leafCache[host] = withKey;
        return withKey;
    }

    /// <summary>Whether the CA is trusted by the OS store (best-effort port of checkCertInstalled).</summary>
    public bool IsTrusted()
    {
        if (!CertExists) return false;
        try
        {
            var pem = File.ReadAllText(CertPath);
            using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadOnly);
            var thumb = X509Certificate2.CreateFromPem(pem).Thumbprint;
            return store.Certificates.Find(X509FindType.FindByThumbprint, thumb, false).Count > 0;
        }
        catch { return false; }
    }

    public object Info()
    {
        var exists = CertExists;
        string? thumb = null, expiry = null;
        if (exists)
        {
            try
            {
                var c = X509Certificate2.CreateFromPem(File.ReadAllText(CertPath));
                thumb = c.Thumbprint;
                expiry = c.NotAfter.ToUniversalTime().ToString("o");
            }
            catch { }
        }
        return new { certPath = CertPath, certExists = exists, certTrusted = IsTrusted(), thumbprint = thumb, notAfter = expiry };
    }
}
