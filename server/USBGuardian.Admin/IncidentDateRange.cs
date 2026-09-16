// ============================================================
// IncidentDateRange.cs
// Rozsah dat pro filtr incidentu - sdileno Prehledem i oba export endpointy,
// aby CSV/report vzdy videly presne to same obdobi jako obrazovka.
//
// PROC: puvodni filtr znal jen "poslednich N dni od ted" (nebo "vse").
// Pro kontrolu/audit (napr. narok organu dozoru na konkretni mesic) to
// nestaci - "90 dni" je pohyblive okno, ne "1.3.-31.3.". Explicitni
// rozsah (od/do) proto ma prednost pred relativnim oknem.
// ============================================================

namespace USBGuardian.Admin;

public static class IncidentDateRange
{
    /// <summary>
    /// od/do jsou MISTNI kalendarni data (yyyy-MM-dd, z &lt;input type="date"&gt;).
    /// Vraci UTC hranice pro dotaz [FromUtc, ToUtc) a citelny popisek obdobi.
    /// </summary>
    public static (DateTime FromUtc, DateTime ToUtc, string Label) Resolve(
        int days, string? od, string? doParam, DateTime? nowLocal = null)
    {
        var odOk = DateOnly.TryParse(od, out var odD);
        var doOk = DateOnly.TryParse(doParam, out var doD);

        if (odOk || doOk)
        {
            if (odOk && doOk && odD > doD) (odD, doD) = (doD, odD);

            var toLocalExcl = doOk
                ? doD.ToDateTime(TimeOnly.MinValue).AddDays(1)
                : (nowLocal ?? DateTime.Now).Date.AddDays(1);
            var toUtc = ToUtc(toLocalExcl);
            var fromUtc = odOk ? ToUtc(odD.ToDateTime(TimeOnly.MinValue)) : DateTime.MinValue;

            var label = $"{(odOk ? odD.ToString("dd.MM.yyyy") : "…")} – {(doOk ? doD.ToString("dd.MM.yyyy") : "dnes")}";
            return (fromUtc, toUtc, label);
        }

        var fromRel = days == 0 ? DateTime.MinValue : DateTime.UtcNow.AddDays(-days);
        var lbl = days == 0 ? "vše" : days == 365 ? "rok" : $"{days} dní";
        return (fromRel, DateTime.MaxValue, lbl);
    }

    private static DateTime ToUtc(DateTime local) =>
        DateTime.SpecifyKind(local, DateTimeKind.Local).ToUniversalTime();
}
