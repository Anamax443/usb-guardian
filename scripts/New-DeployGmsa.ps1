# ============================================================
# New-DeployGmsa.ps1
# Zalozeni gMSA pro auto-enrollment agenta USB Guardian.
#
# SPUSTIT NA DC (nebo se RSAT ActiveDirectory modulem) jako Domain Admin.
# Vytvori: skupinu pro lokalni admina na klientech + gMSA, ktere smi heslo
# cist strojovy ucet konzole (APP_SERVER). Idempotentni (preskoci co uz existuje).
#
# Po tomto skriptu jeste:
#   - na APP_SERVER:  Install-ADServiceAccount gmsa-deploy ; Test-ADServiceAccount gmsa-deploy
#   - GPO:      pridat skupinu $Group do local Administrators na OU klientu
#   (viz docs/auto-deploy-setup.md)
# ============================================================

[CmdletBinding()]
param(
    [string] $GmsaName    = "gmsa-deploy",
    [string] $Group       = "USB-Guardian-Deployers",
    [string] $ConsoleHost = "APP_SERVER",           # strojovy ucet APP_SERVER (bez $)
    [string] $GroupOU,                                # napr. "OU=Service Accounts,DC=domena,DC=loc"; prazdne = default Users
    [string] $DnsSuffix   = $env:USERDNSDOMAIN        # napr. domena.loc
)

Import-Module ActiveDirectory -ErrorAction Stop

Write-Host "USB Guardian – zalozeni deploy gMSA" -ForegroundColor Cyan
Write-Host "  gMSA:        $GmsaName"
Write-Host "  Skupina:     $Group"
Write-Host "  Konzole PC:  $ConsoleHost`$"
Write-Host ""

# ── KDS root key (nutny pro gMSA; nejspis uz existuje kvuli gmsa-api$) ──
$kds = Get-KdsRootKey -ErrorAction SilentlyContinue
if (-not $kds) {
    Write-Warning "KDS root key NEEXISTUJE. gMSA bez nej nepujde."
    Write-Host    "  Produkce: Add-KdsRootKey -EffectiveImmediately  (pak ~10 h replikace)"
    Write-Host    "  Lab/1 DC: Add-KdsRootKey -EffectiveTime ((Get-Date).AddHours(-10))"
    Write-Host    "Spust jednu z variant a skript pak pust znovu." -ForegroundColor Yellow
    return
}

# ── Skupina pro lokalni admina na klientech ──────────────────
if (-not (Get-ADGroup -Filter "Name -eq '$Group'" -ErrorAction SilentlyContinue)) {
    $p = @{ Name = $Group; GroupScope = 'Global'; GroupCategory = 'Security';
            Description = 'USB Guardian – lokalni admin na klientech pro deploy gMSA' }
    if ($GroupOU) { $p.Path = $GroupOU }
    New-ADGroup @p
    Write-Host "Skupina '$Group' vytvorena." -ForegroundColor Green
} else {
    Write-Host "Skupina '$Group' uz existuje – preskakuji."
}

# ── Strojovy ucet konzole (APP_SERVER) – musi existovat ────────────
$consoleComputer = Get-ADComputer -Filter "Name -eq '$ConsoleHost'" -ErrorAction SilentlyContinue
if (-not $consoleComputer) { throw "Strojovy ucet '$ConsoleHost' (konzole APP_SERVER) nenalezen v AD." }

# ── gMSA ─────────────────────────────────────────────────────
if (-not (Get-ADServiceAccount -Filter "Name -eq '$GmsaName'" -ErrorAction SilentlyContinue)) {
    New-ADServiceAccount -Name $GmsaName `
        -DNSHostName "$GmsaName.$DnsSuffix" `
        -PrincipalsAllowedToRetrieveManagedPassword "$ConsoleHost`$" `
        -Description "USB Guardian – deploy ucet (scheduled task na APP_SERVER)"
    Write-Host "gMSA '$GmsaName' vytvoren." -ForegroundColor Green
} else {
    # zajistit, ze APP_SERVER smi cist heslo (kdyby gMSA uz byl)
    Set-ADServiceAccount -Identity $GmsaName -PrincipalsAllowedToRetrieveManagedPassword "$ConsoleHost`$"
    Write-Host "gMSA '$GmsaName' uz existuje – aktualizovano opravneni pro $ConsoleHost`$."
}

# ── gMSA do skupiny deployeru ────────────────────────────────
Add-ADGroupMember -Identity $Group -Members "$GmsaName`$" -ErrorAction SilentlyContinue
Write-Host "gMSA pridan do '$Group'." -ForegroundColor Green

Write-Host ""
Write-Host "HOTOVO. Dalsi kroky:" -ForegroundColor Cyan
Write-Host "  1) Na APP_SERVER:  Install-ADServiceAccount $GmsaName ; Test-ADServiceAccount $GmsaName  (=> True)"
Write-Host "  2) GPO:      pridat '$Group' do local Administrators na OU klientskych stanic"
Write-Host "  3) Scheduled task + zapnuti v konzoli – viz docs/auto-deploy-setup.md"
