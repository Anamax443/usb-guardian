# ============================================================
# Deploy-AgentFleet.ps1
# Hromadné vzdálené nasazení USB Guardian agenta na stanice.
# Kompatibilní s Windows PowerShell 5.1 i PowerShell 7 (runspace pool).
#
# Mechanismus (stejný jako deploy konzole na .213 – síťový token
# účtu, bez UAC na klientech):
#   1. robocopy self-contained publish -> \\HOST\C$\Program Files\USBGuardian
#   2. vytvoreni sluzby "USB Guardian" pres CIM (Win32_Service.Create, DCOM)
#   3. recovery (sc.exe \\HOST failure) + watchdog (schtasks /S HOST)
#   4. start sluzby
# Per-host vysledek -> audit CSV.
#
# Priklady:
#   .\Deploy-AgentFleet.ps1 -Targets PC01 -SourcePath C:\Apps\USBGuardianAgentPublish -DryRun
#   .\Deploy-AgentFleet.ps1 -TargetsFile .\deploy-targets.txt -SourcePath C:\Apps\USBGuardianAgentPublish -ThrottleLimit 15
# ============================================================

[CmdletBinding()]
param(
    [string[]] $Targets,
    [string]   $TargetsFile,
    [Parameter(Mandatory = $true)] [string] $SourcePath,
    [string]   $InstallDir    = "C:\Program Files\USBGuardian",
    [string]   $ServiceName   = "USB Guardian",
    [int]      $ThrottleLimit = 10,
    [string]   $LogCsv,
    [switch]   $ReinstallExisting,
    [switch]   $DryRun
)

$ErrorActionPreference = "Stop"

# ── Sestavit seznam cilu ─────────────────────────────────────
$targetHosts = @()
if ($Targets)     { $targetHosts += $Targets }
if ($TargetsFile) {
    if (-not (Test-Path $TargetsFile)) { throw "TargetsFile nenalezen: $TargetsFile" }
    $targetHosts += Get-Content $TargetsFile | ForEach-Object { $_.Trim() } | Where-Object { $_ -and -not $_.StartsWith('#') }
}
$targetHosts = $targetHosts | Select-Object -Unique
if ($targetHosts.Count -eq 0) { throw "Zadny cil. Pouzij -Targets nebo -TargetsFile." }

# ── Validace zdroje ──────────────────────────────────────────
$srcExe = Join-Path $SourcePath "USBGuardian.exe"
if (-not (Test-Path $srcExe)) {
    throw "USBGuardian.exe nenalezen v '$SourcePath'."
}
$watchSrc = Join-Path $PSScriptRoot "Watch-USBGuardian.ps1"
if (-not $LogCsv) { $LogCsv = Join-Path $PSScriptRoot ("deploy-fleet-" + (Get-Date -Format 'yyyyMMdd_HHmmss') + ".csv") }

Write-Host "USB Guardian - fleet deploy" -ForegroundColor Cyan
Write-Host "  Cilu:    $($targetHosts.Count)"
Write-Host "  Zdroj:   $SourcePath"
Write-Host "  Soubeh:  $ThrottleLimit"
Write-Host "  Rezim:   $(if ($DryRun) { 'DRY-RUN (nic nemeni)' } else { 'OSTRY' })"
Write-Host "  Audit:   $LogCsv"

# ── Per-host logika (scriptblock pro runspace) ───────────────
$perHost = {
    param($h, $InstallDir, $ServiceName, $SourcePath, $watchSrc, $DryRun, $Reinstall)

    $r = [ordered]@{ Host = $h; Status = ''; Detail = ''; Ts = (Get-Date -Format 'HH:mm:ss') }
    $share = "\\$h\C`$\Program Files\USBGuardian"

    try {
        if (-not (Test-Connection -ComputerName $h -Count 1 -Quiet)) {
            $r.Status = 'OFFLINE'; $r.Detail = 'neodpovida na ping'; return [pscustomobject]$r
        }

        $q = (& sc.exe "\\$h" query $ServiceName 2>&1 | Out-String)
        $exists = ($q -notmatch '1060')
        if ($exists -and -not $Reinstall) {
            $r.Status = 'SKIP'; $r.Detail = 'sluzba uz existuje (pouzij -ReinstallExisting)'; return [pscustomobject]$r
        }

        if ($DryRun) {
            $r.Status = 'WOULD-DEPLOY'; $r.Detail = $(if ($exists) { 'reinstall' } else { 'fresh' }); return [pscustomobject]$r
        }

        & robocopy $SourcePath $share /E /R:2 /W:2 /NFL /NDL /NP /NJH /NJS | Out-Null
        if ($LASTEXITCODE -ge 8) { throw "robocopy selhal (kod $LASTEXITCODE)" }
        & robocopy (Split-Path $watchSrc) "$share\scripts" (Split-Path $watchSrc -Leaf) /R:2 /W:2 /NFL /NDL /NP /NJH /NJS | Out-Null

        $binPath = "`"$InstallDir\USBGuardian.exe`""

        $cim = New-CimSession -ComputerName $h -SessionOption (New-CimSessionOption -Protocol Dcom) -OperationTimeoutSec 60
        try {
            if ($exists) {
                & sc.exe "\\$h" config $ServiceName binPath= $binPath start= auto obj= LocalSystem | Out-Null
            } else {
                $res = Invoke-CimMethod -CimSession $cim -ClassName Win32_Service -MethodName Create -Arguments @{
                    Name            = $ServiceName
                    DisplayName     = $ServiceName
                    PathName        = $binPath
                    ServiceType     = [uint32]16
                    ErrorControl    = [uint32]1
                    StartMode       = 'Automatic'
                    DesktopInteract = $false
                    StartName       = 'LocalSystem'
                }
                if ($res.ReturnValue -ne 0) { throw "Win32_Service.Create vratilo $($res.ReturnValue)" }
            }
        } finally { Remove-CimSession $cim }

        & sc.exe "\\$h" failure $ServiceName reset= 86400 actions= restart/60000/restart/60000/restart/60000 | Out-Null

        $wt = "powershell.exe -NonInteractive -NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$InstallDir\scripts\Watch-USBGuardian.ps1`""
        & schtasks.exe /Create /S $h /RU SYSTEM /RL HIGHEST /SC MINUTE /MO 3 /TN "USBGuardian\USBGuardian-Watchdog" /TR $wt /F 2>&1 | Out-Null

        & sc.exe "\\$h" start $ServiceName | Out-Null
        Start-Sleep -Seconds 2
        $st = (& sc.exe "\\$h" query $ServiceName 2>&1 | Out-String)
        if ($st -match 'RUNNING') { $r.Status = 'OK'; $r.Detail = $(if ($exists) { 'reinstalled' } else { 'installed' }) }
        else                      { $r.Status = 'STARTED?'; $r.Detail = 'sluzba vytvorena, stav nepotvrzen RUNNING' }
    }
    catch { $r.Status = 'FAIL'; $r.Detail = $_.Exception.Message }
    return [pscustomobject]$r
}

# ── Spustit pres runspace pool (PS 5.1 + 7) ──────────────────
$pool = [runspacefactory]::CreateRunspacePool(1, [Math]::Max(1, $ThrottleLimit))
$pool.Open()
$running = @()
foreach ($h in $targetHosts) {
    $ps = [powershell]::Create()
    $ps.RunspacePool = $pool
    [void]$ps.AddScript($perHost).
        AddArgument($h).AddArgument($InstallDir).AddArgument($ServiceName).
        AddArgument($SourcePath).AddArgument($watchSrc).
        AddArgument([bool]$DryRun).AddArgument([bool]$ReinstallExisting)
    $running += [pscustomobject]@{ PS = $ps; Handle = $ps.BeginInvoke() }
}
$results = foreach ($j in $running) { $j.PS.EndInvoke($j.Handle); $j.PS.Dispose() }
$pool.Close(); $pool.Dispose()

# ── Souhrn + audit ───────────────────────────────────────────
$results | Sort-Object Status, Host | Export-Csv -Path $LogCsv -NoTypeInformation -Encoding UTF8

Write-Host ""
Write-Host "Vysledek:" -ForegroundColor Cyan
$results | Group-Object Status | Sort-Object Name | ForEach-Object {
    Write-Host ("  {0,-14} {1}" -f $_.Name, $_.Count)
}
$bad = $results | Where-Object { $_.Status -eq 'FAIL' -or $_.Status -eq 'OFFLINE' -or $_.Status -eq 'STARTED?' }
if ($bad) {
    Write-Host ""
    Write-Host "Problemove stanice:" -ForegroundColor Yellow
    $bad | ForEach-Object { Write-Host ("  {0,-18} {1}  {2}" -f $_.Host, $_.Status, $_.Detail) }
}
Write-Host ""
Write-Host "Audit CSV: $LogCsv" -ForegroundColor Green
