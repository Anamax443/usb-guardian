// ============================================================
// UiStyleCache.cs
// Vzhled konzole = styl + rozvržení z banky UI (repo Anamax443/Interface-Par).
// Banka leží ve wwwroot/bank, písma ve wwwroot/vendor/fonts.
//
// PROČ CACHE:
//   Styl se čte při KAŽDÉM vykreslení stránky (link na tokeny v <head>).
//   Dotaz do SQL na každý request by byl zbytečný – hodnota se mění výjimečně,
//   a po uložení v Nastavení se cache explicitně přenačte.
//
// PROČ WHITELIST:
//   Hodnota jde do cesty k souboru (`bank/tokens/style/<styl>.css`). Kdyby se
//   do AppSettings dostalo něco jiného než známý styl, byla by to cesta z ruky
//   uživatele do URL. Neznámá hodnota proto tiše spadne na výchozí.
//
// Klíče v AppSettings: ui.style, ui.layout
// ============================================================

using Microsoft.EntityFrameworkCore;
using USBGuardian.Api.Data;

namespace USBGuardian.Admin.Ui;

public class UiStyleCache
{
    public const string DefaultStyle = "hmi-slate";
    public const string DefaultLayout = "side-nav";

    /// <summary>23 stylů banky – jméno souboru v <c>wwwroot/bank/tokens/style/</c>.</summary>
    public static readonly (string Id, string Label)[] Styles =
    {
        ("admin-common",    "Admin Common – neutrální administrace"),
        ("blueprint-dense", "Blueprint Dense – hustý technický výkres"),
        ("carbon-grid",     "Carbon Grid – uhlová mřížka"),
        ("catppuccin-soft", "Catppuccin Soft – měkké pastely"),
        ("console-dark",    "Console Dark – tmavá konzole"),
        ("contrast-a11y",   "Contrast A11y – maximální kontrast"),
        ("fluent-win",      "Fluent Win – vzhled Windows"),
        ("gruvbox-warm",    "Gruvbox Warm – teplá retro paleta"),
        ("hmi-slate",       "Velín (HMI Slate) – průmyslový panel"),
        ("ledger-mono",     "Ledger Mono – účetní kniha"),
        ("mono-brutal",     "Mono Brutal – brutalistní mono"),
        ("nord-calm",       "Nord Calm – studená severská"),
        ("ops-steel",       "Ops Steel – ocelový dohled"),
        ("paper-doc",       "Paper Doc – papírový dokument"),
        ("saas-modern",     "SaaS Modern – moderní webová appka"),
        ("solar-parchment", "Solar Parchment – pergamen"),
        ("swiss-rule",      "Swiss Rule – švýcarská typografie"),
        ("ticker-black",    "Ticker Black – burzovní tabule"),
        ("tokyo-night",     "Tokyo Night – noční neon"),
        ("turbo-tui",       "Turbo TUI – textové rozhraní"),
        ("vivid-gradient",  "Vivid Gradient – sytý přechod"),
        ("winbox-95",       "Winbox 95 – retro Windows"),
        ("zinc-mute",       "Zinc Mute – tlumený zinek"),
    };

    /// <summary>Rozvržení = druhá, nezávislá osa banky (mění kostru okna, ne barvy).</summary>
    public static readonly (string Id, string Label)[] Layouts =
    {
        ("side-nav",       "boční panel (výchozí)"),
        ("top-tabs",       "horní menu"),
        ("rail",           "úzká lišta"),
        ("master-detail",  "seznam + detail"),
        ("split-console",  "mřížka + konzole"),
    };

    private readonly IDbContextFactory<AppDbContext> _factory;

    public UiStyleCache(IDbContextFactory<AppDbContext> factory) => _factory = factory;

    public string Style { get; private set; } = DefaultStyle;
    public string Layout { get; private set; } = DefaultLayout;

    public async Task ReloadAsync()
    {
        try
        {
            await using var db = await _factory.CreateDbContextAsync();
            var all = await db.AppSettings.ToListAsync();
            Style = Normalize(all.FirstOrDefault(s => s.Key == "ui.style")?.Value, Styles, DefaultStyle);
            Layout = Normalize(all.FirstOrDefault(s => s.Key == "ui.layout")?.Value, Layouts, DefaultLayout);
        }
        catch
        {
            // DB nedostupná / tabulka ještě není → zůstane poslední známá (default) hodnota,
            // konzole se kvůli vzhledu nesmí rozbít.
        }
    }

    public static bool IsKnownStyle(string? id) => Contains(Styles, id);
    public static bool IsKnownLayout(string? id) => Contains(Layouts, id);

    private static bool Contains((string Id, string Label)[] set, string? id) =>
        !string.IsNullOrWhiteSpace(id) && set.Any(x => x.Id.Equals(id.Trim(), StringComparison.OrdinalIgnoreCase));

    private static string Normalize(string? value, (string Id, string Label)[] set, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        var match = set.FirstOrDefault(x => x.Id.Equals(value.Trim(), StringComparison.OrdinalIgnoreCase));
        return match.Id ?? fallback;
    }
}
