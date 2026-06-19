// ============================================================
// ExportEndpoints.cs
// Export incidentů z konzole:
//   GET /export/incidents.csv  – surová data (CSV, UTF-8 BOM + ; → otevře Excel CZ)
//   GET /export/manager        – manažerský report (tisknutelné HTML → PDF) s grafy
//
// Oba endpointy dědí FallbackPolicy (Windows auth + AdminGroups) z Program.cs –
// nejsou AllowAnonymous, takže je vidí jen oprávnění uživatelé konzole.
// Filtr (days/action/q) je shodný s Přehledem. Grafy jsou inline SVG (bez knihoven).
// ============================================================

using System.Globalization;
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
            var d   = days ?? 30;
            var all = await Filter(db, d, null, q).ToListAsync();

            var approved = new HashSet<string>(
                (await db.WhitelistDevices.Where(w => w.IsActive && w.SerialNumber != "")
                    .Select(w => w.SerialNumber).ToListAsync()).Select(s => s.Trim()),
                StringComparer.OrdinalIgnoreCase);
            bool Approved(string s) => !string.IsNullOrWhiteSpace(s) && approved.Contains(s.Trim());

            var m = new ManagerData
            {
                Period     = d == 0 ? "celá historie" : $"posledních {d} dní",
                Query      = q,
                Total      = all.Count,
                Blocked    = all.Count(i => i.Action == "Blocked"),
                Warned     = all.Count(i => i.Action == "Warned"),
                Allowed    = all.Count(i => i.Action == "Allowed"),
                Stations   = all.Select(i => i.Hostname).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                Users      = all.Select(i => i.Username).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                Unapproved = all.Where(i => !Approved(i.SerialNumber))
                                .Select(i => i.SerialNumber).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                TopUsers = all.Where(i => i.Action != "Allowed")
                    .GroupBy(i => i.Username).Select(g => (g.Key, g.Count()))
                    .OrderByDescending(x => x.Item2).Take(6).ToList(),
                TopStations = all.Where(i => i.Action != "Allowed")
                    .GroupBy(i => i.Hostname).Select(g => (g.Key, g.Count()))
                    .OrderByDescending(x => x.Item2).Take(6).ToList(),
                TopMedia = all.Where(i => !Approved(i.SerialNumber) && i.SerialNumber != "")
                    .GroupBy(i => new { i.SerialNumber, i.FriendlyName })
                    .Select(g => (g.Key.FriendlyName, g.Key.SerialNumber, g.Count(),
                                  g.Max(x => x.SizeBytes), g.Max(x => x.Timestamp)))
                    .OrderByDescending(x => x.Item3).Take(8).ToList(),
            };

            // časová řada – po dnech (≤45 bodů), jinak po týdnech
            m.Trend = BuildTrend(all);

            // přehled databáze (nezávisle na filtru období)
            m.DbTotal  = await db.Incidents.CountAsync();
            if (m.DbTotal > 0)
            {
                m.DbOldest = await db.Incidents.MinAsync(i => i.Timestamp);
                m.DbNewest = await db.Incidents.MaxAsync(i => i.Timestamp);
            }
            m.DbDevices  = await db.Incidents.Where(i => i.SerialNumber != "")
                                   .Select(i => i.SerialNumber).Distinct().CountAsync();
            m.DbStations = await db.Incidents.Select(i => i.Hostname).Distinct().CountAsync();

            return Results.Content(BuildManagerHtml(m), "text/html; charset=utf-8");
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

    // ── data model reportu ───────────────────────────────────
    private sealed class ManagerData
    {
        public string Period = ""; public string? Query;
        public int Total, Blocked, Warned, Allowed, Stations, Users, Unapproved;
        public List<(string Key, int Count)> TopUsers = new();
        public List<(string Key, int Count)> TopStations = new();
        public List<(string Name, string Serial, int Count, long Size, DateTime Last)> TopMedia = new();
        public List<(string Label, int Blocked, int Warned)> Trend = new();
        public int DbTotal, DbDevices, DbStations;
        public DateTime? DbOldest, DbNewest;
    }

    private static List<(string Label, int Blocked, int Warned)> BuildTrend(List<Incident> all)
    {
        if (all.Count == 0) return new();
        var local = all.Select(i => (D: i.Timestamp.ToLocalTime(), i.Action)).ToList();
        var min = local.Min(x => x.D).Date;
        var max = local.Max(x => x.D).Date;
        var spanDays = (max - min).Days + 1;

        if (spanDays <= 45)
        {
            return Enumerable.Range(0, spanDays).Select(off =>
            {
                var day = min.AddDays(off);
                var dayItems = local.Where(x => x.D.Date == day).ToList();
                return (day.ToString("d.M.", CultureInfo.GetCultureInfo("cs-CZ")),
                        dayItems.Count(x => x.Action == "Blocked"),
                        dayItems.Count(x => x.Action == "Warned"));
            }).ToList();
        }

        // po týdnech (pondělí jako začátek)
        DateTime WeekStart(DateTime dt) => dt.AddDays(-((int)dt.DayOfWeek + 6) % 7).Date;
        return local.GroupBy(x => WeekStart(x.D))
            .OrderBy(g => g.Key)
            .Select(g => (g.Key.ToString("d.M.", CultureInfo.GetCultureInfo("cs-CZ")),
                          g.Count(x => x.Action == "Blocked"),
                          g.Count(x => x.Action == "Warned")))
            .ToList();
    }

    // ── CSV / HTML escaping ──────────────────────────────────
    private static string C(string? v)
    {
        v ??= "";
        return v.IndexOfAny(new[] { ';', '"', '\n', '\r' }) >= 0
            ? "\"" + v.Replace("\"", "\"\"") + "\""
            : v;
    }
    private static string H(string? v) => System.Net.WebUtility.HtmlEncode(v ?? "");
    private static string Gb(long bytes) => bytes > 0 ? $"{bytes / 1_073_741_824.0:F1} GB" : "—";
    private static string Dt(DateTime? t) => t is null ? "—" : t.Value.ToLocalTime().ToString("dd.MM.yyyy HH:mm");

    // ── SVG grafy (bez externích knihoven, tisknutelné) ──────
    private const string CW = "#dc2626", CY = "#d97706", CG = "#16a34a", CB = "#2563eb";

    // stacked sloupcový graf: blokováno (červená) + varování (oranžová)
    private static string StackedBars(List<(string Label, int Blocked, int Warned)> data)
    {
        if (data.Count == 0) return "<p class=\"muted\">Žádná data.</p>";
        int W = 720, Hh = 165, padL = 34, padB = 34, padT = 8;
        int n = data.Count;
        double max = Math.Max(1, data.Max(d => d.Blocked + d.Warned));
        double plotH = Hh - padB - padT, plotW = W - padL - 8;
        double bw = Math.Min(38, plotW / n * 0.7), gap = plotW / n;
        var sb = new StringBuilder();
        sb.Append($"<svg viewBox=\"0 0 {W} {Hh}\" width=\"100%\" preserveAspectRatio=\"xMidYMid meet\" role=\"img\">");
        // osa Y – 3 čáry
        for (int g = 0; g <= 3; g++)
        {
            double val = max * g / 3.0, y = padT + plotH - plotH * g / 3.0;
            sb.Append($"<line x1=\"{padL}\" y1=\"{y:F1}\" x2=\"{W - 8}\" y2=\"{y:F1}\" stroke=\"#e5e7eb\"/>");
            sb.Append($"<text x=\"{padL - 5}\" y=\"{y + 3:F1}\" text-anchor=\"end\" font-size=\"9\" fill=\"#9ca3af\">{Math.Round(val)}</text>");
        }
        for (int i = 0; i < n; i++)
        {
            var d = data[i];
            double x = padL + gap * i + (gap - bw) / 2;
            double hB = plotH * d.Blocked / max, hW = plotH * d.Warned / max;
            double yW = padT + plotH - hW, yB = yW - hB;
            if (d.Warned > 0) sb.Append($"<rect x=\"{x:F1}\" y=\"{yW:F1}\" width=\"{bw:F1}\" height=\"{hW:F1}\" fill=\"{CY}\"/>");
            if (d.Blocked > 0) sb.Append($"<rect x=\"{x:F1}\" y=\"{yB:F1}\" width=\"{bw:F1}\" height=\"{hB:F1}\" fill=\"{CW}\"/>");
            // popisek osy X – každý n-tý, ať se nepřekrývá
            if (n <= 16 || i % (int)Math.Ceiling(n / 16.0) == 0)
                sb.Append($"<text x=\"{x + bw / 2:F1}\" y=\"{Hh - padB + 14}\" text-anchor=\"middle\" font-size=\"9\" fill=\"#6b7280\" transform=\"rotate(35 {x + bw / 2:F1} {Hh - padB + 14})\">{H(d.Label)}</text>");
        }
        sb.Append("</svg>");
        sb.Append($"<div class=\"lg\"><span><i style=\"background:{CW}\"></i>Blokováno</span><span><i style=\"background:{CY}\"></i>Varování</span></div>");
        return sb.ToString();
    }

    // donut: rozpad podle akce
    private static string Donut(int blocked, int warned, int allowed)
    {
        int total = blocked + warned + allowed;
        if (total == 0) return "<p class=\"muted\">Žádná data.</p>";
        double r = 60, cx = 80, cy = 80, circ = 2 * Math.PI * r, off = 0;
        var segs = new (int V, string C, string L)[] { (blocked, CW, "Blokováno"), (warned, CY, "Varování"), (allowed, CG, "Povoleno") };
        var sb = new StringBuilder();
        sb.Append("<svg viewBox=\"0 0 280 160\" width=\"100%\" preserveAspectRatio=\"xMidYMid meet\" role=\"img\">");
        sb.Append($"<g transform=\"rotate(-90 {cx} {cy})\">");
        foreach (var s in segs)
        {
            if (s.V == 0) continue;
            double len = circ * s.V / total;
            sb.Append($"<circle cx=\"{cx}\" cy=\"{cy}\" r=\"{r}\" fill=\"none\" stroke=\"{s.C}\" stroke-width=\"26\" " +
                      $"stroke-dasharray=\"{len:F2} {circ - len:F2}\" stroke-dashoffset=\"{-off:F2}\"/>");
            off += len;
        }
        sb.Append("</g>");
        sb.Append($"<text x=\"{cx}\" y=\"{cy - 2}\" text-anchor=\"middle\" font-size=\"22\" font-weight=\"700\" fill=\"#1a2233\">{total}</text>");
        sb.Append($"<text x=\"{cx}\" y=\"{cy + 16}\" text-anchor=\"middle\" font-size=\"10\" fill=\"#6b7280\">incidentů</text>");
        double ly = 30;
        foreach (var s in segs)
        {
            var pct = total > 0 ? s.V * 100.0 / total : 0;
            sb.Append($"<rect x=\"175\" y=\"{ly}\" width=\"11\" height=\"11\" rx=\"2\" fill=\"{s.C}\"/>");
            sb.Append($"<text x=\"192\" y=\"{ly + 10}\" font-size=\"11\" fill=\"#374151\">{s.L}: {s.V} ({pct:F0} %)</text>");
            ly += 22;
        }
        sb.Append("</svg>");
        return sb.ToString();
    }

    // horizontální pruhy pro top-N
    private static string HBars(List<(string Key, int Count)> data, string color)
    {
        if (data.Count == 0) return "<p class=\"muted\">Žádné incidenty.</p>";
        double max = Math.Max(1, data.Max(d => d.Count));
        var sb = new StringBuilder("<div class=\"hbars\">");
        foreach (var d in data)
        {
            double pct = d.Count / max * 100;
            sb.Append($"<div class=\"hb\"><span class=\"hbk\" title=\"{H(d.Key)}\">{H(d.Key)}</span>" +
                      $"<span class=\"hbt\"><span class=\"hbf\" style=\"width:{pct:F1}%;background:{color}\"></span></span>" +
                      $"<span class=\"hbv\">{d.Count}</span></div>");
        }
        sb.Append("</div>");
        return sb.ToString();
    }

    private static string BuildManagerHtml(ManagerData m)
    {
        string mediaRows = m.TopMedia.Any()
            ? string.Concat(m.TopMedia.Select(x =>
                $"<tr><td>{H(x.Name)}</td><td class=\"mono\">{H(x.Serial)}</td><td>{Gb(x.Size)}</td>" +
                $"<td class=\"num\">{x.Count}</td><td>{x.Last.ToLocalTime():dd.MM.yyyy HH:mm}</td></tr>"))
            : "<tr><td colspan=\"5\" class=\"muted\">Žádná neschválená média.</td></tr>";

        var qNote = string.IsNullOrWhiteSpace(m.Query) ? "" : $" · filtr: „{H(m.Query)}\"";
        var generated = DateTime.Now.ToString("dd.MM.yyyy HH:mm");
        var dbRange = (m.DbOldest is null) ? "—" : $"{Dt(m.DbOldest)} – {Dt(m.DbNewest)}";

        return $$"""
<!DOCTYPE html>
<html lang="cs">
<head>
<meta charset="utf-8">
<title>USB Guardian – manažerský report</title>
<style>
  :root { --ink:#1a2233; --muted:#6b7280; --line:#e5e7eb; --bad:#dc2626; --warn:#d97706; --ok:#16a34a; --brand:#2563eb; }
  * { box-sizing:border-box; }
  body { font-family:Segoe UI,Arial,sans-serif; color:var(--ink); margin:24px; max-width:1000px; }
  h1 { font-size:20px; margin:0 0 2px; }
  h2 { font-size:14px; margin:16px 0 8px; border-bottom:2px solid var(--line); padding-bottom:3px; }
  .sub { color:var(--muted); font-size:12px; margin:0 0 12px; }
  .kpis { display:flex; flex-wrap:wrap; gap:8px; margin:10px 0; }
  .kpi { border:1px solid var(--line); border-radius:9px; padding:8px 12px; min-width:108px; }
  .kpi .n { font-size:21px; font-weight:700; }
  .kpi .l { color:var(--muted); font-size:11px; text-transform:uppercase; letter-spacing:.03em; }
  .kpi.bad .n { color:var(--bad); } .kpi.warn .n { color:var(--warn); } .kpi.ok .n { color:var(--ok); }
  .grid2 { display:grid; grid-template-columns:1.5fr 1fr; gap:20px; align-items:start; }
  .grid2b { display:grid; grid-template-columns:1fr 1fr; gap:24px; align-items:start; }
  @media(max-width:760px){ .grid2,.grid2b { grid-template-columns:1fr; } }
  .card { border:1px solid var(--line); border-radius:10px; padding:12px 14px; }
  .card h3 { font-size:12px; text-transform:uppercase; letter-spacing:.04em; color:var(--muted); margin:0 0 8px; }
  table { width:100%; border-collapse:collapse; font-size:12.5px; }
  th,td { text-align:left; padding:6px 9px; border-bottom:1px solid var(--line); }
  th { color:var(--muted); font-weight:600; font-size:11px; text-transform:uppercase; }
  td.num,th.num { text-align:right; } .mono { font-family:Consolas,monospace; } .muted { color:var(--muted); }
  .lg { display:flex; gap:16px; font-size:11px; color:var(--muted); margin-top:6px; }
  .lg i { display:inline-block; width:10px; height:10px; border-radius:2px; margin-right:4px; vertical-align:middle; }
  .hbars { display:flex; flex-direction:column; gap:6px; }
  .hb { display:grid; grid-template-columns:130px 1fr 32px; align-items:center; gap:8px; font-size:12px; }
  .hbk { white-space:nowrap; overflow:hidden; text-overflow:ellipsis; }
  .hbt { background:#f3f4f6; border-radius:5px; height:14px; overflow:hidden; }
  .hbf { display:block; height:100%; border-radius:5px; }
  .hbv { text-align:right; font-weight:600; }
  .foot { margin-top:18px; color:var(--muted); font-size:10px; border-top:1px solid var(--line); padding-top:6px; }
  .actions { margin:0 0 14px; } .btn { font:inherit; padding:8px 14px; border:1px solid var(--brand);
     background:var(--brand); color:#fff; border-radius:8px; cursor:pointer; }
  h2, .card, table, .grid2, .grid2b { break-inside:avoid; page-break-inside:avoid; }
  @page { size:A4 portrait; margin:11mm; }
  @media print {
    .actions { display:none; } body { margin:0; max-width:none; font-size:11px; }
    h2 { margin:10px 0 6px; } .kpis { margin:7px 0; }
  }
</style>
</head>
<body>
  <h1>USB Guardian — manažerský report</h1>
  <p class="sub">Období: {{m.Period}}{{qNote}} · vygenerováno {{generated}} · monitoring paměťových médií (NIS2)</p>
  <div class="actions"><button class="btn" onclick="window.print()">Tisk / uložit PDF</button></div>

  <div class="kpis">
    <div class="kpi"><div class="n">{{m.Total}}</div><div class="l">Incidentů</div></div>
    <div class="kpi bad"><div class="n">{{m.Blocked}}</div><div class="l">Blokováno</div></div>
    <div class="kpi warn"><div class="n">{{m.Warned}}</div><div class="l">Varování</div></div>
    <div class="kpi ok"><div class="n">{{m.Allowed}}</div><div class="l">Povoleno</div></div>
    <div class="kpi"><div class="n">{{m.Stations}}</div><div class="l">Dotčených stanic</div></div>
    <div class="kpi"><div class="n">{{m.Users}}</div><div class="l">Dotčených uživatelů</div></div>
    <div class="kpi warn"><div class="n">{{m.Unapproved}}</div><div class="l">Neschválených médií</div></div>
  </div>

  <h2>Vývoj a rozpad incidentů</h2>
  <div class="grid2">
    <div class="card"><h3>Incidenty v čase (blokováno / varování)</h3>{{StackedBars(m.Trend)}}</div>
    <div class="card"><h3>Rozpad podle akce</h3>{{Donut(m.Blocked, m.Warned, m.Allowed)}}</div>
  </div>

  <h2>Nejčastější původci (neschválená / varovaná média)</h2>
  <div class="grid2b">
    <div class="card"><h3>Uživatelé</h3>{{HBars(m.TopUsers, CB)}}</div>
    <div class="card"><h3>Stanice</h3>{{HBars(m.TopStations, CW)}}</div>
  </div>

  <h2>Neschválená média (top podle četnosti)</h2>
  <table><thead><tr><th>Médium</th><th>Sériové číslo</th><th>Kapacita</th><th class="num">Výskytů</th><th>Naposledy</th></tr></thead>
  <tbody>{{mediaRows}}</tbody></table>

  <h2>Databáze incidentů (celkový stav)</h2>
  <div class="kpis">
    <div class="kpi"><div class="n">{{m.DbTotal}}</div><div class="l">Incidentů v DB</div></div>
    <div class="kpi"><div class="n">{{m.DbDevices}}</div><div class="l">Unikátních médií</div></div>
    <div class="kpi"><div class="n">{{m.DbStations}}</div><div class="l">Stanic v datech</div></div>
  </div>
  <p class="sub">Rozsah dat v databázi: <strong>{{dbRange}}</strong> · řízeno retenční politikou (Nastavení → Retence dat).</p>

  <p class="foot">USB Guardian · automaticky generovaný report z admin konzole. Grafy a hodnoty odpovídají zvolenému období a filtru (sekce „Databáze incidentů" je za celou DB).</p>
</body>
</html>
""";
    }
}
