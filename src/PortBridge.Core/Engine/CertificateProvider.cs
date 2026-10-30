using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using PortBridge.Core.Config;

namespace PortBridge.Core.Engine;

/// <summary>Loads TLS server certificates, generating a long-lived self-signed one on first use.</summary>
public sealed class CertificateProvider
{
    private readonly ConcurrentDictionary<string, X509Certificate2> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    public string SelfSignedPath => Path.Combine(AppPaths.CertDir, "portbridge-selfsigned.pfx");

    public X509Certificate2 GetServerCertificate(TlsOptions tls)
    {
        if (string.IsNullOrWhiteSpace(tls.CertificatePath)) return GetSelfSigned();
        var key = tls.CertificatePath + "|" + tls.CertificatePassword;
        return _cache.GetOrAdd(key, _ => X509CertificateLoader.LoadPkcs12FromFile(tls.CertificatePath!, tls.CertificatePassword,
            X509KeyStorageFlags.DefaultKeySet));
    }

    public X509Certificate2 GetSelfSigned()
    {
        if (_cache.TryGetValue("self", out var c)) return c;
        lock (_gate)
        {
            if (_cache.TryGetValue("self", out c)) return c;
            if (!File.Exists(SelfSignedPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SelfSignedPath)!);
                File.WriteAllBytes(SelfSignedPath, CreateSelfSigned().Export(X509ContentType.Pfx));
            }
            // SChannel needs a persisted key, so don't use EphemeralKeySet here.
            c = X509CertificateLoader.LoadPkcs12FromFile(SelfSignedPath, null, X509KeyStorageFlags.DefaultKeySet);
            _cache["self"] = c;
            return c;
        }
    }

    private static X509Certificate2 CreateSelfSigned()
    {
        using var rsa = RSA.Create(2048);
        var host = Environment.MachineName;
        var req = new CertificateRequest($"CN={host}, O=PortBridge", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(host);
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        req.CertificateExtensions.Add(san.Build());
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        return req.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(10));
    }

    public static string Sha256Thumbprint(X509Certificate cert) =>
        Convert.ToHexString(SHA256.HashData(cert.GetRawCertData()));
}
