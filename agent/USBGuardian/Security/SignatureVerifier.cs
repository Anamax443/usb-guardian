// ============================================================
// SignatureVerifier.cs
// Ověřuje RSA-SHA256 podpis whitelist.json souboru.
// Veřejný klíč je uložen v PEM formátu v Config/whitelist_public.pem
//
// Bezpečnostní model:
//   - Soukromý klíč: pouze na IT admin stanici / serveru (nikdy na agentu)
//   - Veřejný klíč: distribuován s agentem (součást instalace)
//   - Pokud podpis chybí nebo nesouhlasí → whitelist ODMÍTNUT
//   - Pokud veřejný klíč chybí → agent nenačte whitelist (fail-secure)
//
// Algoritmus: RSA 4096-bit, SHA-256, PKCS1v15 padding
// ============================================================

using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace USBGuardian.Security;

public class SignatureVerifier
{
    private readonly ILogger<SignatureVerifier> _logger;
    private readonly string _publicKeyPath;

    // Načtený veřejný klíč – cachujeme aby se nečetl soubor při každém ověření
    private RSA? _cachedPublicKey;

    public SignatureVerifier(ILogger<SignatureVerifier> logger, string publicKeyPath)
    {
        _logger        = logger;
        _publicKeyPath = publicKeyPath;
    }

    // --------------------------------------------------------
    // Ověří zda je whitelist JSON podepsán platným podpisem.
    // Vrátí false pokud:
    //   - veřejný klíč nenalezen
    //   - podpis soubor nenalezen
    //   - podpis neodpovídá obsahu
    //   - jakákoliv kryptografická chyba
    // --------------------------------------------------------
    public bool Verify(string whitelistPath, string signaturePath)
    {
        try
        {
            // Krok 1: Načteme veřejný klíč
            var publicKey = LoadPublicKey();
            if (publicKey == null)
            {
                _logger.LogError(
                    "RSA ověření: veřejný klíč nenalezen ({Path}) – whitelist ODMÍTNUT",
                    _publicKeyPath);
                return false;
            }

            // Krok 2: Zkontrolujeme existenci podpis souboru
            if (!File.Exists(signaturePath))
            {
                _logger.LogError(
                    "RSA ověření: podpis soubor nenalezen ({Path}) – whitelist ODMÍTNUT. " +
                    "Whitelist musí být podepsán IT oddělením.",
                    signaturePath);
                return false;
            }

            // Krok 3: Načteme obsah whitelistu a podpis
            var whitelistBytes = File.ReadAllBytes(whitelistPath);
            var signatureBase64 = File.ReadAllText(signaturePath).Trim();
            var signature       = Convert.FromBase64String(signatureBase64);

            // Krok 4: Ověříme podpis
            var isValid = publicKey.VerifyData(
                whitelistBytes,
                signature,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);

            if (isValid)
            {
                _logger.LogInformation("RSA ověření: whitelist podpis OK ✓");
            }
            else
            {
                _logger.LogError(
                    "RSA ověření: PODPIS NESOUHLASÍ – whitelist ODMÍTNUT! " +
                    "Soubor mohl být upraven. Kontaktujte IT oddělení.");
            }

            return isValid;
        }
        catch (FormatException ex)
        {
            _logger.LogError(ex,
                "RSA ověření: podpis soubor má neplatný formát (Base64) – whitelist ODMÍTNUT");
            return false;
        }
        catch (CryptographicException ex)
        {
            _logger.LogError(ex,
                "RSA ověření: kryptografická chyba – whitelist ODMÍTNUT");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "RSA ověření: neočekávaná chyba – whitelist ODMÍTNUT (fail-secure)");
            return false;
        }
    }

    // --------------------------------------------------------
    // Načte veřejný klíč z PEM souboru
    // Formát: RSA PUBLIC KEY v PEM (-----BEGIN PUBLIC KEY-----)
    // --------------------------------------------------------
    private RSA? LoadPublicKey()
    {
        // Vrátit z cache pokud je k dispozici
        if (_cachedPublicKey != null) return _cachedPublicKey;

        if (!File.Exists(_publicKeyPath))
            return null;

        try
        {
            var pem = File.ReadAllText(_publicKeyPath);
            var rsa = RSA.Create();
            rsa.ImportFromPem(pem);
            _cachedPublicKey = rsa;
            return rsa;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Chyba při načítání veřejného klíče: {Path}", _publicKeyPath);
            return null;
        }
    }

    // --------------------------------------------------------
    // Vygeneruje nový RSA klíčový pár a uloží je do souborů.
    // Používá se POUZE při inicializaci – jednou na IT admin stanici.
    // Soukromý klíč NIKDY neposílat na agenty!
    // --------------------------------------------------------
    public static void GenerateKeyPair(string privateKeyPath, string publicKeyPath)
    {
        using var rsa = RSA.Create(4096);

        // Exportovat soukromý klíč (PKCS#8 PEM)
        var privateKeyPem = rsa.ExportPkcs8PrivateKeyPem();
        File.WriteAllText(privateKeyPath, privateKeyPem, Encoding.ASCII);

        // Exportovat veřejný klíč (SubjectPublicKeyInfo PEM)
        var publicKeyPem = rsa.ExportSubjectPublicKeyInfoPem();
        File.WriteAllText(publicKeyPath, publicKeyPem, Encoding.ASCII);

        Console.WriteLine($"Klíčový pár vygenerován:");
        Console.WriteLine($"  Soukromý klíč: {privateKeyPath}");
        Console.WriteLine($"  Veřejný klíč:  {publicKeyPath}");
        Console.WriteLine();
        Console.WriteLine("POZOR: Soukromý klíč NIKDY nedistribuovat na agenty!");
        Console.WriteLine("       Veřejný klíč zkopírovat do agent/Config/whitelist_public.pem");
    }

    /// <summary>
    /// Podepíše whitelist soubor soukromým klíčem.
    /// Používá se na IT admin stanici nebo při deployi serveru.
    /// </summary>
    public static void SignWhitelist(string whitelistPath, string privateKeyPath, string signaturePath)
    {
        var privateKeyPem = File.ReadAllText(privateKeyPath);
        using var rsa     = RSA.Create();
        rsa.ImportFromPem(privateKeyPem);

        var whitelistBytes = File.ReadAllBytes(whitelistPath);
        var signature      = rsa.SignData(
            whitelistBytes,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        File.WriteAllText(signaturePath, Convert.ToBase64String(signature), Encoding.ASCII);
        Console.WriteLine($"Whitelist podepsán: {signaturePath}");
    }
}
