// ============================================================
// DeployOutcome.cs
// Čisté odvození, jestli má poslední záznam z Deníku aktivity (Source
// "deploy-run") ještě znamenat "chyba" - bez DB/UI, testovatelné odděleně
// od Computers.razor. Stejný důvod jako StationStatus.cs: logika, na
// které záleží (dlaždice "Chyby nasazení" i sloupec "Poslední nasazení"),
// dřív žila jen inline v Razor @code a šla rozbít beze změny v testech.
//
// PROČ reports() hraje roli: jakmile stanice HLÁSÍ agenta, i kdyby poslední
// pokus skončil FAIL/SKIP, agent dneska evidentně běží a historie už nic
// neříká - viz CERNYSW11 (16.09.2026), kde starý SKIP pletl operátora u
// stanice, co mezitím normálně hlásila.
// ============================================================

namespace USBGuardian.Admin.Deploy;

public static class DeployOutcome
{
    /// <summary>Ukázat historii posledního nasazení vůbec (jinak "—")?</summary>
    public static bool ShouldShowHistory(bool reports) => !reports;

    /// <summary>Počítat tenhle záznam do dlaždice/filtru "Chyby nasazení"?</summary>
    public static bool IsRelevantError(bool reports, string? level) =>
        !reports && level == "error";

    public static string PillClass(string level) => level switch
    {
        "error" => "bad",
        "warn"  => "warn",
        _       => "ok",
    };
}
