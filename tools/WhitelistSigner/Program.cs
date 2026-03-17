// ============================================================
// WhitelistSigner.cs – konzolový nástroj pro IT admina
// 
// Použití:
//   WhitelistSigner generate              → vygeneruje klíčový pár
//   WhitelistSigner sign <whitelist.json> → podepíše whitelist
//   WhitelistSigner verify <whitelist.json> → ověří podpis
//
// Soubory:
//   private_key.pem  → NIKDY nedistribuovat, bezpečně uložit
//   public_key.pem   → zkopírovat do agent/Config/whitelist_public.pem
//   whitelist.json.sig → distribuovat spolu s whitelist.json
//
// Spouštět POUZE na zabezpečené IT admin stanici.
// ============================================================

using System.Security.Cryptography;
using System.Text;

var args_list = args.ToList();
var command   = args_list.ElementAtOrDefault(0)?.ToLower();

// Výchozí cesty
var privateKeyPath = "private_key.pem";
var publicKeyPath  = "public_key.pem";

switch (command)
{
    case "generate":
        GenerateKeyPair();
        break;

    case "sign":
        var whitelistToSign = args_list.ElementAtOrDefault(1);
        if (string.IsNullOrEmpty(whitelistToSign))
        {
            Console.WriteLine("Chybí cesta k whitelist.json");
            Console.WriteLine("Použití: WhitelistSigner sign <whitelist.json>");
            return 1;
        }
        SignWhitelist(whitelistToSign);
        break;

    case "verify":
        var whitelistToVerify = args_list.ElementAtOrDefault(1);
        if (string.IsNullOrEmpty(whitelistToVerify))
        {
            Console.WriteLine("Chybí cesta k whitelist.json");
            Console.WriteLine("Použití: WhitelistSigner verify <whitelist.json>");
            return 1;
        }
        return VerifyWhitelist(whitelistToVerify) ? 0 : 1;

    default:
        Console.WriteLine("USB Guardian – WhitelistSigner");
        Console.WriteLine();
        Console.WriteLine("Příkazy:");
        Console.WriteLine("  generate                   Vygeneruje nový RSA klíčový pár");
        Console.WriteLine("  sign <whitelist.json>      Podepíše whitelist soubor");
        Console.WriteLine("  verify <whitelist.json>    Ověří podpis whitelist souboru");
        Console.WriteLine();
        Console.WriteLine("Příklady:");
        Console.WriteLine("  WhitelistSigner generate");
        Console.WriteLine("  WhitelistSigner sign C:\\whitelist\\whitelist.json");
        Console.WriteLine("  WhitelistSigner verify C:\\whitelist\\whitelist.json");
        return 1;
}

return 0;

// --------------------------------------------------------
// Generování klíčového páru (jednou při inicializaci)
// --------------------------------------------------------
void GenerateKeyPair()
{
    if (File.Exists(privateKeyPath))
    {
        Console.Write($"Soubor {privateKeyPath} již existuje. Přepsat? [y/N]: ");
        if (Console.ReadLine()?.Trim().ToLower() != "y")
        {
            Console.WriteLine("Zrušeno.");
            return;
        }
    }

    Console.WriteLine("Generuji RSA 4096-bit klíčový pár...");
    using var rsa = RSA.Create(4096);

    // Soukromý klíč – PKCS#8 PEM
    File.WriteAllText(privateKeyPath, rsa.ExportPkcs8PrivateKeyPem(), Encoding.ASCII);

    // Veřejný klíč – SubjectPublicKeyInfo PEM
    File.WriteAllText(publicKeyPath, rsa.ExportSubjectPublicKeyInfoPem(), Encoding.ASCII);

    Console.WriteLine();
    Console.WriteLine("✓ Klíčový pár vygenerován:");
    Console.WriteLine($"  Soukromý klíč: {Path.GetFullPath(privateKeyPath)}");
    Console.WriteLine($"  Veřejný klíč:  {Path.GetFullPath(publicKeyPath)}");
    Console.WriteLine();
    Console.WriteLine("Další kroky:");
    Console.WriteLine($"  1. Zkopírovat {publicKeyPath} → agent/Config/whitelist_public.pem");
    Console.WriteLine($"  2. Soukromý klíč bezpečně uložit (trezor, HSM, Azure Key Vault)");
    Console.WriteLine($"  3. NIKDY necommitovat private_key.pem do Gitu!");
}

// --------------------------------------------------------
// Podpis whitelist souboru
// --------------------------------------------------------
void SignWhitelist(string whitelistPath)
{
    if (!File.Exists(whitelistPath))
    {
        Console.WriteLine($"Soubor nenalezen: {whitelistPath}");
        return;
    }

    if (!File.Exists(privateKeyPath))
    {
        Console.WriteLine($"Soukromý klíč nenalezen: {privateKeyPath}");
        Console.WriteLine("Nejdříve vygenerujte klíčový pár: WhitelistSigner generate");
        return;
    }

    var signaturePath = whitelistPath + ".sig";

    var privateKeyPem  = File.ReadAllText(privateKeyPath);
    using var rsa      = RSA.Create();
    rsa.ImportFromPem(privateKeyPem);

    var whitelistBytes = File.ReadAllBytes(whitelistPath);
    var signature      = rsa.SignData(
        whitelistBytes,
        HashAlgorithmName.SHA256,
        RSASignaturePadding.Pkcs1);

    File.WriteAllText(signaturePath, Convert.ToBase64String(signature), Encoding.ASCII);

    Console.WriteLine($"✓ Whitelist podepsán:");
    Console.WriteLine($"  Whitelist: {Path.GetFullPath(whitelistPath)}");
    Console.WriteLine($"  Podpis:    {Path.GetFullPath(signaturePath)}");
    Console.WriteLine();
    Console.WriteLine("Distribuujte oba soubory (JSON + SIG) na agenty.");
}

// --------------------------------------------------------
// Ověření podpisu (diagnostika)
// --------------------------------------------------------
bool VerifyWhitelist(string whitelistPath)
{
    var signaturePath = whitelistPath + ".sig";

    if (!File.Exists(whitelistPath))
    {
        Console.WriteLine($"Soubor nenalezen: {whitelistPath}");
        return false;
    }

    if (!File.Exists(signaturePath))
    {
        Console.WriteLine($"Podpis nenalezen: {signaturePath}");
        return false;
    }

    if (!File.Exists(publicKeyPath))
    {
        Console.WriteLine($"Veřejný klíč nenalezen: {publicKeyPath}");
        return false;
    }

    var publicKeyPem   = File.ReadAllText(publicKeyPath);
    using var rsa      = RSA.Create();
    rsa.ImportFromPem(publicKeyPem);

    var whitelistBytes  = File.ReadAllBytes(whitelistPath);
    var signatureBase64 = File.ReadAllText(signaturePath).Trim();
    var signature       = Convert.FromBase64String(signatureBase64);

    var isValid = rsa.VerifyData(
        whitelistBytes,
        signature,
        HashAlgorithmName.SHA256,
        RSASignaturePadding.Pkcs1);

    if (isValid)
        Console.WriteLine("✓ Podpis PLATNÝ – whitelist nebyl upraven");
    else
        Console.WriteLine("✗ Podpis NEPLATNÝ – whitelist byl upraven nebo podepsán jiným klíčem!");

    return isValid;
}
