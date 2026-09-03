// ============================================================
// HealthExportEndpoints.cs
// Export výsledku kontrol stavu do souboru, který jde poslat dál.
//
//   GET /export/health.csv   – CSV pro Excel (UTF-8 BOM + středník)
//   GET /export/health.txt   – prostý text (do e-mailu, do ticketu, do logu)
//   GET /export/health.html  – tisknutelná stránka; ?tisk=1 rovnou otevře
//                              dialog tisku, odkud se uloží PDF
//
// PROČ PDF PŘES TISK A NE KNIHOVNOU:
//   Stejně jako manažerský report. Generátor PDF by byla další závislost
//   ve službě, která má být co nejmenší; prohlížeč umí "uložit jako PDF"
//   sám a výsledek je stejný. Kdo chce PDF ze skriptu, vezme si HTML.
//
// Kontroly se pro export POUŠTĚJÍ ZNOVU — soubor tak nese aktuální stav,
// ne to, co bylo na obrazovce před hodinou. Bez prodlevy mezi kroky
// (ta je jen kvůli čitelnosti stránky).
//
// Endpointy dědí FallbackPolicy jako zbytek konzole → jen pro oprávněné.
// ============================================================

using System.Text;
using Microsoft.EntityFrameworkCore;
using USBGuardian.Admin.Health;
using USBGuardian.Api.Data;
using USBGuardian.Api.Models;

namespace USBGuardian.Admin.Export;

public static class HealthExportEndpoints
{
    public static void MapHealthExportEndpoints(this WebApplication app)
    {
        app.MapGet("/export/health.csv", async (HealthService health, CancellationToken ct) =>
        {
            var report = await health.RunAsync(progress: null, ct);

            var sb = new StringBuilder();
            sb.Append('﻿');   // BOM – Excel pozná UTF-8
            sb.AppendLine("Skupina;Kontrola;Stav;Zjisteno;Proc;Naprava");
            foreach (var c in report.Checks)
            {
                sb.AppendLine(string.Join(';', new[]
                {
                    C(c.Group), C(c.Name), C(Label(c.State)), C(c.Value), C(c.Why), C(c.Fix),
                }));
            }

            return Results.File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv; charset=utf-8",
                $"usbguardian-kontroly-{DateTime.Now:yyyyMMdd-HHmm}.csv");
        });

        app.MapGet("/export/health.txt", async (HealthService health, CancellationToken ct) =>
        {
            var report = await health.RunAsync(progress: null, ct);

            var sb = new StringBuilder();
            sb.AppendLine("USB Guardian – kontroly stavu");
            sb.AppendLine($"Proběhlo: {report.RanAt:dd.MM.yyyy HH:mm:ss} · trvalo {report.Duration.TotalSeconds:F1} s "
                        + $"· konzole {AppInfo.Commit}");
            sb.AppendLine($"Verdikt: {report.Verdict}");
            sb.AppendLine(report.Summary);
            sb.AppendLine(new string('=', 78));

            foreach (var group in report.Checks.GroupBy(c => c.Group))
            {
                sb.AppendLine();
                sb.AppendLine(group.Key.ToUpperInvariant());
                sb.AppendLine(new string('-', 78));
                foreach (var c in group)
                {
                    sb.AppendLine($"[{Sign(c.State)}] {c.Name} — {Label(c.State)}");
                    sb.AppendLine($"    zjištěno: {c.Value}");
                    sb.AppendLine($"    proč:     {c.Why}");
                    if (!string.IsNullOrEmpty(c.Fix))
                        sb.AppendLine($"    náprava:  {c.Fix}");
                    sb.AppendLine();
                }
            }

            return Results.File(Encoding.UTF8.GetBytes(sb.ToString()), "text/plain; charset=utf-8",
                $"usbguardian-kontroly-{DateTime.Now:yyyyMMdd-HHmm}.txt");
        });

        // Deník aktivity – tentýž výběr, jaký je zrovna na stránce.
        app.MapGet("/export/aktivita.csv", async (
            IDbContextFactory<AppDbContext> factory,
            int? hodin, string? uroven, string? zdroj, string? q, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            IQueryable<ActivityEntry> dotaz = db.ActivityLog;

            if (hodin is > 0)
            {
                var od = DateTime.UtcNow.AddHours(-hodin.Value);
                dotaz = dotaz.Where(a => a.Timestamp >= od);
            }
            if (!string.IsNullOrEmpty(uroven)) dotaz = dotaz.Where(a => a.Level == uroven);
            if (!string.IsNullOrEmpty(zdroj)) dotaz = dotaz.Where(a => a.Source == zdroj);
            if (!string.IsNullOrWhiteSpace(q))
            {
                var h = q.Trim();
                dotaz = dotaz.Where(a => a.Message.Contains(h) || (a.Hostname != null && a.Hostname.Contains(h)));
            }

            // Strop je vyšší než na stránce: soubor se otevře v Excelu, ne v prohlížeči.
            var radky = await dotaz.OrderByDescending(a => a.Timestamp).Take(20_000).ToListAsync(ct);

            var sb = new StringBuilder();
            sb.Append('﻿');
            sb.AppendLine("Cas;Uroven;Zdroj;Stanice;Uzivatel;Zprava");
            foreach (var a in radky)
            {
                sb.AppendLine(string.Join(';', new[]
                {
                    C(a.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")),
                    C(a.Level), C(a.Source), C(a.Hostname), C(a.User), C(a.Message),
                }));
            }

            return Results.File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv; charset=utf-8",
                $"usbguardian-aktivita-{DateTime.Now:yyyyMMdd-HHmm}.csv");
        });

        app.MapGet("/export/health.html", async (HealthService health, int? tisk, CancellationToken ct) =>
        {
            var report = await health.RunAsync(progress: null, ct);
            return Results.Content(BuildHtml(report, tisk == 1), "text/html; charset=utf-8");
        });
    }

    // ── HTML (tisk → PDF) ────────────────────────────────────────
    // Samostatná, světlá a bez závislosti na bance: soubor se posílá ven
    // a musí vypadat stejně u toho, kdo konzoli nikdy neviděl.
    private static string BuildHtml(HealthReport r, bool autoPrint)
    {
        var groups = new StringBuilder();
        foreach (var group in r.Checks.GroupBy(c => c.Group))
        {
            groups.Append($"<h2>{H(group.Key)}</h2><table>");
            groups.Append("<tr><th style=\"width:120px\">Stav</th><th style=\"width:200px\">Kontrola</th><th>Zjištěno</th></tr>");
            foreach (var c in group)
            {
                groups.Append($"<tr class=\"{Css(c.State)}\">"
                    + $"<td><span class=\"pill\">{H(Label(c.State))}</span></td>"
                    + $"<td><b>{H(c.Name)}</b></td>"
                    + $"<td>{H(c.Value)}<div class=\"proc\">{H(c.Why)}</div>"
                    + (string.IsNullOrEmpty(c.Fix) ? "" : $"<div class=\"fix\"><b>Náprava:</b> {H(c.Fix)}</div>")
                    + "</td></tr>");
            }
            groups.Append("</table>");
        }

        var print = autoPrint ? "<script>window.addEventListener('load', function(){ window.print(); });</script>" : "";

        return $$"""
<!DOCTYPE html>
<html lang="cs">
<head>
<meta charset="utf-8">
<title>USB Guardian – kontroly stavu</title>
<style>
  :root { --ink:#1a2233; --muted:#6b7280; --line:#e5e7eb; --bad:#dc2626; --warn:#d97706; --ok:#16a34a; --brand:#2563eb; }
  * { box-sizing:border-box; }
  body { font-family:Segoe UI,Arial,sans-serif; color:var(--ink); margin:24px; max-width:1000px; }
  h1 { font-size:20px; margin:0 0 2px; }
  h2 { font-size:14px; margin:18px 0 8px; border-bottom:2px solid var(--line); padding-bottom:3px; }
  .sub { color:var(--muted); font-size:12px; margin:0 0 12px; }
  .verdikt { border:1px solid var(--line); border-left-width:5px; border-radius:8px; padding:10px 14px; margin:10px 0 14px; }
  .verdikt.ok { border-left-color:var(--ok); } .verdikt.warn { border-left-color:var(--warn); }
  .verdikt.bad { border-left-color:var(--bad); }
  .verdikt .n { font-size:17px; font-weight:700; }
  table { width:100%; border-collapse:collapse; font-size:12.5px; }
  th,td { text-align:left; padding:6px 9px; border-bottom:1px solid var(--line); vertical-align:top; }
  th { color:var(--muted); font-weight:600; font-size:11px; text-transform:uppercase; }
  .proc { color:var(--muted); margin-top:3px; }
  .fix { margin-top:3px; }
  .pill { display:inline-block; padding:1px 8px; border-radius:10px; font-size:11px; font-weight:700; border:1px solid currentColor; }
  tr.ok .pill { color:var(--ok); } tr.warn .pill { color:var(--warn); }
  tr.bad .pill { color:var(--bad); } tr.off .pill { color:var(--muted); }
  .actions { margin:0 0 14px; }
  .btn { font:inherit; padding:8px 14px; border:1px solid var(--brand); background:var(--brand); color:#fff; border-radius:7px; cursor:pointer; }
  .foot { margin-top:18px; color:var(--muted); font-size:10px; border-top:1px solid var(--line); padding-top:6px; }
  @media print { .actions { display:none; } body { margin:0; } }
</style>
</head>
<body>
  <div class="actions"><button class="btn" onclick="window.print()">Tisk / uložit PDF</button></div>
  <h1>USB Guardian – kontroly stavu</h1>
  <p class="sub">Proběhlo {{r.RanAt:dd.MM.yyyy HH:mm:ss}} · trvalo {{r.Duration.TotalSeconds:F1}} s · konzole {{AppInfo.Commit}}</p>
  <div class="verdikt {{VerdictCss(r)}}">
    <div class="n">{{H(r.Verdict)}}</div>
    <div class="sub" style="margin:2px 0 0">{{H(r.Summary)}}</div>
  </div>
  {{groups}}
  <div class="foot">Vygenerovala serverová konzole USB Guardian. Stejná data strojově: /api/health</div>
  {{print}}
</body>
</html>
""";
    }

    // ── pomocné ──────────────────────────────────────────────────

    /// <summary>CSV buňka: středník i uvozovky uvnitř musí přežít cestu do Excelu.</summary>
    private static string C(string? s)
    {
        s = (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        return s.Contains('"') || s.Contains(';') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }

    private static string H(string? s) => System.Net.WebUtility.HtmlEncode(s ?? "");

    private static string Label(HealthState s) => s switch
    {
        HealthState.Ok => "v pořádku",
        HealthState.Warn => "varování",
        HealthState.Bad => "CHYBA",
        HealthState.Off => "vypnuto",
        _ => "čeká na data",
    };

    private static string Sign(HealthState s) => s switch
    {
        HealthState.Ok => "OK",
        HealthState.Warn => " ! ",
        HealthState.Bad => " X ",
        HealthState.Off => " - ",
        _ => " ? ",
    };

    private static string Css(HealthState s) => s switch
    {
        HealthState.Ok => "ok",
        HealthState.Warn => "warn",
        HealthState.Bad => "bad",
        _ => "off",
    };

    private static string VerdictCss(HealthReport r) =>
        r.Bad > 0 ? "bad" : r.Warn > 0 ? "warn" : "ok";
}
