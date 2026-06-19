// ============================================================
// WhitelistPublisher.cs
// Serverový AUTO-PODPIS + AUTO-PUBLISH whitelistu.
//
// Po každé změně katalogu (přidat/odebrat/aktivovat/edit) konzole vydá NOVOU verzi:
//   1. snapshot aktivního katalogu → kanonický whitelist.json blob (nová verze, čerstvá platnost)
//   2. PODEPÍŠE interním RSA-4096 klíčem ze serveru (Whitelist:PrivateKeyPath)
//   3. uloží Json + Signature, aktivuje (deaktivuje staré) → agent stáhne ≤2 min (1:1 kopie)
//
// Podpis je INTERNÍ klíč USB Guardianu (jeho public = whitelist_public.pem na agentech),
// NE AXIMA code-signing cert. Agent se nemění – ověřuje stejným veřejným klíčem.
//
// Byte-exact: podepisují se PŘESNĚ ty bajty (UTF-8 bez BOM), které API servíruje na /api/whitelist.
// Trade-off (vědomě zvolený): privátní klíč je na serveru .213 (chránit ACL/DPAPI) výměnou za plnou automatizaci.
// ============================================================

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using USBGuardian.Api.Data;
using USBGuardian.Api.Models;

namespace USBGuardian.Admin.Whitelist;

public static class WhitelistPublisher
{
    /// <summary>Vydá a podepíše novou verzi whitelistu z aktivního katalogu. Vrací (ok, hláška).</summary>
    public static async Task<(bool Ok, string Message)> PublishAsync(
        AppDbContext db, string issuedBy, IConfiguration cfg)
    {
        var keyPath = cfg["Whitelist:PrivateKeyPath"];
        if (string.IsNullOrWhiteSpace(keyPath) || !File.Exists(keyPath))
            return (false, "Auto-podpis VYPNUTÝ: chybí privátní klíč (nastav Whitelist:PrivateKeyPath na .213). " +
                           "Změny katalogu se k agentům nedostanou, dokud klíč nedoplníš.");

        var active = await db.WhitelistDevices.Where(d => d.IsActive)
            .OrderBy(d => d.SerialNumber).ToListAsync();

        var now = DateTime.UtcNow;
        var validityDays = 365;
        var vd = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == "whitelist.validityDays");
        if (int.TryParse(vd?.Value, out var dd) && dd > 0) validityDays = dd;
        var validUntil = now.AddDays(validityDays);

        // unikátní verze yyyy-MM-dd-vN
        var total = await db.WhitelistVersions.CountAsync();
        var ver = $"{now:yyyy-MM-dd}-v{total + 1}";
        while (await db.WhitelistVersions.AnyAsync(v => v.Version == ver))
        { total++; ver = $"{now:yyyy-MM-dd}-v{total + 1}"; }

        // Kanonický blob – PŘESNĚ tyto bajty se podepíší i servírují agentům.
        var blobObj = new
        {
            version = ver,
            issuedAt = now,
            validUntil,
            signature = "",
            devices = active.Select(d => new
            {
                vendorId = d.VendorId, productId = d.ProductId, serialNumber = d.SerialNumber,
                description = d.Description, approvedAt = d.ApprovedAt, approvedBy = d.ApprovedBy,
                validUntil = (DateTime?)null
            })
        };
        var blob = JsonSerializer.Serialize(blobObj, new JsonSerializerOptions { WriteIndented = false });

        // Interní RSA podpis (SHA-256 / Pkcs1) – shodně s WhitelistSigner i agentem.
        string sig;
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(await File.ReadAllTextAsync(keyPath));
            var data = Encoding.UTF8.GetBytes(blob);
            sig = Convert.ToBase64String(rsa.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        }
        catch (Exception ex)
        {
            return (false, "Podpis selhal (klíč?): " + ex.Message);
        }

        foreach (var v in await db.WhitelistVersions.Where(v => v.IsActive).ToListAsync())
            v.IsActive = false;

        db.WhitelistVersions.Add(new WhitelistVersion
        {
            Version = ver, IssuedAt = now, ValidUntil = validUntil, IssuedBy = issuedBy,
            Json = blob, Signature = sig, IsActive = true
        });
        await db.SaveChangesAsync();

        return (true, $"Vydána a podepsána verze {ver} ({active.Count} zařízení) – agenti ji stáhnou do ~2 min.");
    }
}
