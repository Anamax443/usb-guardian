// ============================================================
// ExportEndpoints.cs
// Export incidentů z konzole:
//   GET /export/incidents.csv  – surová data (CSV, UTF-8 BOM + ; → otevře Excel CZ)
//   GET /export/manager        – manažerský report (tisknutelné HTML → PDF z prohlížeče)
//
// Oba endpointy dědí FallbackPolicy (Windows auth + AdminGroups) z Program.cs –
// nejsou AllowAnonymous, takže je vidí jen oprávnění uživatelé konzole.
// Filtr (days/action/q) je shodný s Přehledem, takže export = co je na obrazovce.
// ============================================================

using System.Text;
using Microsoft.EntityFrameworkCore;
using USBGuardian.Api.Data;
using USBGuardian.Api.Models;

namespace USBGuardian.Admin.Export;

public static class ExportEndpoints
{
    public static void MapExportEndpoints(this WebApplication app)
    {
        app.MapGet("/export/incidents.csv", async (
            IDbContextFactory<AppDbContext> factory, int? days, string? action, string? q) =>
        {
            await using var db = await factory.CreateDbContextAsync();
            var rows = await Filter(db, days, action, q)
                .OrderByDescending(i => i.Timestamp).Take(50_000).ToListAsync();

            var sb = new StringBuilder();
            sb.Append('﻿'); // BOM – Excel pozná UTF-8
            sb.AppendLine("Cas;Stanice;Uzivatel;Medium;Typ;VID;PID;Seriove cislo;Velikost GB;Akce;Whitelist verze;Odpojeno");
            foreach (var i in rows)
            {
                sb.AppendLine(string.Join(';', new[]
                {
                    C(i.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")),
                    C(i.Hostname), C(i.Username), C(i.FriendlyName), C(i.DeviceType),
                    C(i.VendorId), C(i.ProductId), C(i.SerialNumber),
                    C(i.SizeBytes > 0 ? (i.SizeBytes / 1_073_741_824.0).ToString("F1") : ""),
                    C(i.Action), C(i.WhitelistVersion),
                    C(i.DisconnectedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "")
                }));
            }

            var bytes = Encoding.UTF8.GetBytes(sb.ToString());
            return Results.File(bytes, "text/csv; charset=utf-8",
                $"usbguardian-incidenty-{DateTime.Now:yyyyMMdd-HHmm}.csv");
        });

        app.MapGet("/export/manager", async (
            IDbContextFactory<AppDbContext> factory, int? days, string? q) =>
        {
            await using var db = await factory.CreateDbContextAsync();
            var d = days ?? 30;
            var all = await Filter(db, d, null, q).ToListAsync();

            var approved = new HashSet<string>(
                (await db.WhitelistDevices.Where(w => w.IsActive && w.SerialNumber != "")
                    .Select(w => w.SerialNumber).ToListAsync()).Select(s => s.Trim()),
                StringComparer.OrdinalIgnoreCase);
            bool Approved(string s) => !string.IsNullOrWhiteSpace(s) && approved.Contains(s.Trim());

            var total      = all.Count;
            var blocked    = all.Count(i => i.Action == "Blocked");
            var warned     = all.Count(i => i.Action == "Warned");
            var stations   = all.Select(i => i.Hostname).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            var users      = all.Select(i => i.Username).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            var unapproved = all.Where(i => !Approved(i.SerialNumber))
                                .Select(i => i.SerialNumber).Distinct(StringComparer.OrdinalIgnoreCase).Count();

            var topUsers = all.Where(i => i.Action != "Allowed")
                .GroupBy(i => i.Username).Select(g => (Key: g.Key, Count: g.Count()))
                .OrderByDescending(x => x.Count).Take(10).ToList();
            var topStations = all.Where(i => i.Action != "Allowed")
                .GroupBy(i => i.Hostname).Select(g => (Key: g.Key, Count: g.Count()))
                .OrderByDescending(x => x.Count).Take(10).ToList();
            var topMedia = all.Where(i => !Approved(i.SerialNumber) && i.SerialNumber != "")
                .GroupBy(i => new { i.SerialNumber, i.FriendlyName })
                .Select(g => (Name: g.Key.FriendlyName, Serial: g.Key.SerialNumber, Count: g.Count(),
                              Size: g.Max(x => x.SizeBytes), Last: g.Max(x => x.Timestamp)))
                .OrderByDescending(x => x.Count).Take(15).ToList();

            var period = d == 0 ? "celá historie" : $"posledních {d} dní";
            var html = BuildManagerHtml(period, q, total, blocked, warned, stations, users, unapproved,
                topUsers, topStations, topMedia);
            return Results.Content(html, "text/html; charset=utf-8");
        });
    }

    private static IQueryable<Incident> Filter(AppDbContext db, int? days, string? action, string? q)
    {
        var d = days ?? 30;
        var from = d == 0 ? DateTime.MinValue : DateTime.UtcNow.AddDays(-d);
        var query = db.Incidents.Where(i => i.Timestamp >= from);
        if (!string.IsNullOrWhiteSpace(action))
            query = query.Where(i => i.Action == action);
        if (!string.IsNullOrWhiteSpace(q))
            query = query.Where(i => i.Hostname.Contains(q) || i.Username.Contains(q)
                                  || i.FriendlyName.Contains(q) || i.SerialNumber.Contains(q));
        return query;
    }

    // CSV pole: zabal do uvozovek a zdvoj uvozovky, kdyz obsahuje ; " nebo newline.
    private static string C(string? v)
    {
        v ??= "";
        return v.IndexOfAny(new[] { ';', '"', '\n', '\r' }) >= 0
            ? "\"" + v.Replace("\"", "\"\"") + "\""
            : v;
    }

    private static string H(string? v) => System.Net.WebUtility.HtmlEncode(v ?? "");
    private static string Gb(long bytes) => bytes > 0 ? $"{bytes / 1_073_741_824.0:F1} GB" : "—";

    private static string BuildManagerHtml(
        string period, string? q,
        int total, int blocked, int warned, int stations, int users, int unapproved,
        List<(string Key, int Count)> topUsers,
        List<(string Key, int Count)> topStations,
        List<(string Name, string Serial, int Count, long Size, DateTime Last)> topMedia)
    {
        string Rows<T>(IEnumerable<T> items, Func<T, string> row, int cols, string empty) =>
            items.Any() ? string.Concat(items.Select(row))
                        : $"<tr><td colspan=\"{cols}\" class=\"muted\">{empty}</td></tr>";

        var usersRows = Rows(topUsers,
            u => $"<tr><td>{H(u.Key)}</td><td class=\"num\">{u.Count}</td></tr>", 2, "Žádné incidenty.");
        var stationsRows = Rows(topStations,
            s => $"<tr><td>{H(s.Key)}</td><td class=\"num\">{s.Count}</td></tr>", 2, "Žádné incidenty.");
        var mediaRows = Rows(topMedia,
            m => $"<tr><td>{H(m.Name)}</td><td class=\"mono\">{H(m.Serial)}</td><td>{Gb(m.Size)}</td>" +
                 $"<td class=\"num\">{m.Count}</td><td>{m.Last.ToLocalTime():dd.MM.yyyy HH:mm}</td></tr>",
            5, "Žádná neschválená média.");

        var qNote = string.IsNullOrWhiteSpace(q) ? "" : $" · filtr: „{H(q)}\"";
        var generated = DateTime.Now.ToString("dd.MM.yyyy HH:mm");

        return $$"""
<!DOCTYPE html>
<html lang="cs">
<head>
<meta charset="utf-8">
<title>USB Guardian – manažerský report</title>
<style>
  :root { --ink:#1a2233; --muted:#6b7280; --line:#e5e7eb; --bad:#b91c1c; --warn:#b45309; --brand:#1d4ed8; }
  * { box-sizing:border-box; }
  body { font-family:Segoe UI,Arial,sans-serif; color:var(--ink); margin:32px; max-width:980px; }
  h1 { font-size:22px; margin:0 0 2px; }
  h2 { font-size:15px; margin:28px 0 8px; border-bottom:2px solid var(--line); padding-bottom:4px; }
  .sub { color:var(--muted); font-size:13px; margin:0 0 18px; }
  .kpis { display:flex; flex-wrap:wrap; gap:12px; margin:14px 0; }
  .kpi { border:1px solid var(--line); border-radius:10px; padding:12px 16px; min-width:140px; }
  .kpi .n { font-size:26px; font-weight:700; }
  .kpi .l { color:var(--muted); font-size:12px; text-transform:uppercase; letter-spacing:.03em; }
  .kpi.bad .n { color:var(--bad); } .kpi.warn .n { color:var(--warn); }
  table { width:100%; border-collapse:collapse; font-size:13px; margin-bottom:8px; }
  th,td { text-align:left; padding:6px 10px; border-bottom:1px solid var(--line); }
  th { color:var(--muted); font-weight:600; font-size:12px; text-transform:uppercase; }
  td.num,th.num { text-align:right; } .mono { font-family:Consolas,monospace; } .muted { color:var(--muted); }
  .foot { margin-top:28px; color:var(--muted); font-size:11px; border-top:1px solid var(--line); padding-top:8px; }
  .actions { margin:0 0 18px; } .btn { font:inherit; padding:8px 14px; border:1px solid var(--brand);
     background:var(--brand); color:#fff; border-radius:8px; cursor:pointer; }
  @media print { .actions { display:none; } body { margin:0; } }
</style>
</head>
<body>
  <h1>USB Guardian — manažerský report</h1>
  <p class="sub">Období: {{period}}{{qNote}} · vygenerováno {{generated}} · monitoring paměťových médií (NIS2)</p>
  <div class="actions"><button class="btn" onclick="window.print()">Tisk / uložit PDF</button></div>

  <div class="kpis">
    <div class="kpi"><div class="n">{{total}}</div><div class="l">Incidentů</div></div>
    <div class="kpi bad"><div class="n">{{blocked}}</div><div class="l">Blokováno</div></div>
    <div class="kpi warn"><div class="n">{{warned}}</div><div class="l">Varování</div></div>
    <div class="kpi"><div class="n">{{stations}}</div><div class="l">Dotčených stanic</div></div>
    <div class="kpi"><div class="n">{{users}}</div><div class="l">Dotčených uživatelů</div></div>
    <div class="kpi warn"><div class="n">{{unapproved}}</div><div class="l">Neschválených médií</div></div>
  </div>

  <h2>Nejčastější uživatelé (neschválená / varovaná média)</h2>
  <table><thead><tr><th>Uživatel</th><th class="num">Incidentů</th></tr></thead><tbody>{{usersRows}}</tbody></table>

  <h2>Nejčastější stanice</h2>
  <table><thead><tr><th>Stanice</th><th class="num">Incidentů</th></tr></thead><tbody>{{stationsRows}}</tbody></table>

  <h2>Neschválená média (top podle četnosti)</h2>
  <table><thead><tr><th>Médium</th><th>Sériové číslo</th><th>Kapacita</th><th class="num">Výskytů</th><th>Naposledy</th></tr></thead><tbody>{{mediaRows}}</tbody></table>

  <p class="foot">USB Guardian · automaticky generovaný report z admin konzole. Data odpovídají zvolenému období a filtru.</p>
</body>
</html>
""";
    }
}
