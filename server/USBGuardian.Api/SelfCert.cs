// ============================================================
// SelfCert.cs
// Self-contained TLS: API si vygeneruje a persistne vlastní
// self-signed certifikát (bez CA, bez cert store, bez ACL).
// Klíč drží v paměti (EphemeralKeySet) → běží i pod gMSA.
// Persistence .pfx → stabilní otisk přes restarty (kvůli pinningu agentů).
// ============================================================

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace USBGuardian.Api;

public static class SelfCert
{
    public static X509Certificate2 LoadOrCreate(string pfxPath, string subject)
    {
        // 1) Zkus načíst existující (stabilní otisk)
        if (File.Exists(pfxPath))
        {
            try
            {
                return new X509Certificate2(File.ReadAllBytes(pfxPath), (string?)null,
                    X509KeyStorageFlags.EphemeralKeySet);
            }
            catch { /* poškozený → přegeneruj */ }
        }

        // 2) Vygeneruj nový self-signed (Server Authentication)
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest($"CN={subject}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(subject);
        san.AddDnsName("localhost");
        var domain = Environment.GetEnvironmentVariable("USERDNSDOMAIN");
        if (!string.IsNullOrEmpty(domain)) san.AddDnsName($"{subject}.{domain}");
        req.CertificateExtensions.Add(san.Build());

        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false)); // Server Auth
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));

        using var generated = req.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));

        var pfx = generated.Export(X509ContentType.Pfx);

        // 3) Persist .pfx (best-effort – stabilní otisk přes restarty)
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(pfxPath)!);
            File.WriteAllBytes(pfxPath, pfx);
        }
        catch { /* když nejde zapsat, poběží z paměti (otisk se ale po restartu změní) */ }

        return new X509Certificate2(pfx, (string?)null, X509KeyStorageFlags.EphemeralKeySet);
    }
}
