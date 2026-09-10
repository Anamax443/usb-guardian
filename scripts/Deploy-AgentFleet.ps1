# ============================================================
# Deploy-AgentFleet.ps1
# Hromadné vzdálené nasazení USB Guardian agenta na stanice.
# Kompatibilní s Windows PowerShell 5.1 i PowerShell 7 (runspace pool).
#
# Mechanismus (stejný jako deploy konzole na APP_SERVER – síťový token
# účtu, bez UAC na klientech):
#   1. robocopy self-contained publish -> \\HOST\C$\Program Files\USBGuardian
#   2. vytvoreni sluzby "USB Guardian" pres sc.exe \\HOST create (SCM/named-pipes)
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
# ToastHelper je soucast kompletniho klienta (notifikace uzivateli). Bez nej se incidenty
# zaznamenaji, ale uzivatel varovani neuvidi. Build balicku: scripts\Build-AgentPackage.ps1.
if (-not (Test-Path (Join-Path $SourcePath "ToastHelper\ToastHelper.exe"))) {
    Write-Host "  UPOZORNENI: v balicku chybi ToastHelper\ToastHelper.exe – toast notifikace nepojedou." -ForegroundColor Yellow
    Write-Host "             Sestav balicek pres scripts\Build-AgentPackage.ps1." -ForegroundColor Yellow
}
$watchSrc = Join-Path $PSScriptRoot "Watch-USBGuardian.ps1"
if (-not $LogCsv) { $LogCsv = Join-Path $PSScriptRoot ("deploy-fleet-" + (Get-Date -Format 'yyyyMMdd_HHmmss') + ".csv") }

# Velikost balicku pro mereni rychlosti kopirovani - spocitat jednou pro vsechny
# cile, ne per-host (stejny zdroj, zbytecne opakovane prochazeni disku).
$sourceSizeBytes = (Get-ChildItem $SourcePath -Recurse -File | Measure-Object -Property Length -Sum).Sum

Write-Host "USB Guardian - fleet deploy" -ForegroundColor Cyan
Write-Host "  Cilu:    $($targetHosts.Count)"
Write-Host "  Zdroj:   $SourcePath"
Write-Host "  Soubeh:  $ThrottleLimit"
Write-Host "  Rezim:   $(if ($DryRun) { 'DRY-RUN (nic nemeni)' } else { 'OSTRY' })"
Write-Host "  Audit:   $LogCsv"

# ── Per-host logika (scriptblock pro runspace) ───────────────
$perHost = {
    param($h, $InstallDir, $ServiceName, $SourcePath, $watchSrc, $DryRun, $Reinstall, $sourceSizeBytes)

    $r = [ordered]@{ Host = $h; Status = ''; Detail = ''; Ts = (Get-Date -Format 'HH:mm:ss') }
    $share = "\\$h\C`$\Program Files\USBGuardian"

    # Vysvetleni robocopy exit kodu (bitova maska, funguje bez ohledu na jazyk
    # OS - robocopy sam pise hlasky v lokalizaci serveru, tohle ne).
    function Vysvetli-RoboKod([int]$code) {
        $bity = @()
        if ($code -band 1)  { $bity += 'zkopirovano OK' }
        if ($code -band 2)  { $bity += 'v cili navic soubory/adresare' }
        if ($code -band 4)  { $bity += 'nesouhlasici soubory/adresare' }
        if ($code -band 8)  { $bity += 'nektere soubory se NEpodarilo zkopirovat (retry vycerpan)' }
        if ($code -band 16) { $bity += 'VAZNA CHYBA - nezkopirovalo se nic (prava nebo cesta nedostupna)' }
        return $(if ($bity.Count -gt 0) { $bity -join ', ' } else { 'neznamy kod' })
    }

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

        # Vystup se NEzahazuje (drivejsi | Out-Null) - je to jediny zdroj informace PROC
        # kopirovani selhalo (napr. "Access is denied" na cilovem sdileni). Cas se meri
        # sami (Stopwatch), ne parsovanim robocopy souhrnu - ten je lokalizovany dle
        # jazyka serveru, cislo v sekundach ne.
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        $roboOut = & robocopy $SourcePath $share /E /R:2 /W:2 /NFL /NDL /NP /NJH /NJS 2>&1 | Out-String
        $sw.Stop()
        if ($LASTEXITCODE -ge 8) {
            $posledni = ($roboOut -split '\r?\n' | Where-Object { $_.Trim() } | Select-Object -Last 5) -join ' | '
            throw "robocopy selhal (kod $LASTEXITCODE = $(Vysvetli-RoboKod $LASTEXITCODE)): $posledni"
        }
        $secs  = [Math]::Max($sw.Elapsed.TotalSeconds, 0.01)
        $mbps  = [Math]::Round(($sourceSizeBytes / 1MB) / $secs, 1)
        $rychlost = "kopie $([Math]::Round($sourceSizeBytes/1MB,1)) MB za $([Math]::Round($secs,1))s ($mbps MB/s)"
        & robocopy (Split-Path $watchSrc) "$share\scripts" (Split-Path $watchSrc -Leaf) /R:2 /W:2 /NFL /NDL /NP /NJH /NJS | Out-Null

        # Sluzba pres sc.exe (SCM/named-pipes – stejna cesta jako robocopy/SMB, co funguje;
        # CIM/DCOM Win32_Service.Create na klientech selhavalo). cmd.exe kvuli quotingu cesty s mezerou.
        $exe = "$InstallDir\USBGuardian.exe"
        if ($exists) {
            & cmd.exe /c ('sc.exe \\{0} config "{1}" binPath= "\"{2}\"" start= auto obj= LocalSystem' -f $h, $ServiceName, $exe) | Out-Null
        } else {
            $scOut = (& cmd.exe /c ('sc.exe \\{0} create "{1}" binPath= "\"{2}\"" start= auto obj= LocalSystem DisplayName= "{1}"' -f $h, $ServiceName, $exe) 2>&1 | Out-String)
            if ($LASTEXITCODE -ne 0) { throw "sc create selhal ($LASTEXITCODE): $($scOut.Trim())" }
        }

        & sc.exe "\\$h" failure $ServiceName reset= 86400 actions= restart/60000/restart/60000/restart/60000 | Out-Null

        # Watchdog: PS-free scheduled task (zadny podpis/trust na klientech) – nahodi sluzbu kdyz se zastavi.
        # sc start na bezici sluzbu vrati 1056 (neskodne); kdyz je zastavena, spusti ji.
        $wOut = (& cmd.exe /c ('schtasks /Create /S {0} /RU SYSTEM /RL HIGHEST /SC MINUTE /MO 3 /TN "USBGuardian\USBGuardian-Watchdog" /TR "sc start \"{1}\"" /F' -f $h, $ServiceName) 2>&1 | Out-String)
        $wOk = ($LASTEXITCODE -eq 0)

        # ToastHelper task (PS-free na klientovi): logon + unlock trigger, bezi v user session.
        # XML se re-encode na Unicode (schtasks /XML to vyzaduje) a vytvori na klientovi pres /S.
        # ToastHelper.exe se na klienta dostal robocopy /E (podslozka ToastHelper\ v balicku).
        $toOk = $false; $toMsg = 'toast: bez XML v balicku'
        $toastXml = Join-Path $SourcePath 'tasks\USBGuardian-ToastHelper.xml'
        if (Test-Path $toastXml) {
            $tmpXml = Join-Path ([System.IO.Path]::GetTempPath()) ("usbg-toast-{0}.xml" -f $h)
            # Docasny soubor se maze ve finally nize - jinak se v %TEMP% na APP_SERVER
            # hromadi usbg-toast-<HOST>.xml po kazdem nasazeni (jeden na stanici).
            (Get-Content $toastXml -Raw) | Set-Content $tmpXml -Encoding Unicode
            $toOut = (& schtasks /Create /S $h /XML $tmpXml /TN "USBGuardian\USBGuardian-ToastHelper" /F 2>&1 | Out-String)
            Remove-Item $tmpXml -Force -ErrorAction SilentlyContinue
            $toOk  = ($LASTEXITCODE -eq 0)
            $toMsg = $(if ($toOk) { 'toast ok' } else { 'toast FAIL: ' + ($toOut.Trim() -replace '\s+',' ') })
        }

        & sc.exe "\\$h" start $ServiceName | Out-Null
        Start-Sleep -Seconds 2
        $st = (& sc.exe "\\$h" query $ServiceName 2>&1 | Out-String)
        $wd = $(if ($wOk) { 'wd ok' } else { 'wd FAIL: ' + ($wOut.Trim() -replace '\s+',' ') })
        $extra = $rychlost + '; ' + $wd + '; ' + $toMsg
        if ($st -match 'RUNNING') { $r.Status = 'OK'; $r.Detail = ($(if ($exists) { 'reinstalled' } else { 'installed' }) + '; ' + $extra) }
        else                      { $r.Status = 'STARTED?'; $r.Detail = 'sluzba vytvorena, stav nepotvrzen RUNNING; ' + $extra }
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
        AddArgument([bool]$DryRun).AddArgument([bool]$ReinstallExisting).
        AddArgument($sourceSizeBytes)
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

# SIG # Begin signature block
# MIIeXgYJKoZIhvcNAQcCoIIeTzCCHksCAQExDzANBglghkgBZQMEAgEFADB5Bgor
# BgEEAYI3AgEEoGswaTA0BgorBgEEAYI3AgEeMCYCAwEAAAQQH8w7YFlLCE63JNLG
# KX7zUQIBAAIBAAIBAAIBAAIBADAxMA0GCWCGSAFlAwQCAQUABCAMG/vajWWWH6oM
# 42BGXbWyoT3KL+HVdgdtxBsS++cAXaCCGRgwggWNMIIEdaADAgECAhAOmxiO+dAt
# 5+/bUOIIQBhaMA0GCSqGSIb3DQEBDAUAMGUxCzAJBgNVBAYTAlVTMRUwEwYDVQQK
# EwxEaWdpQ2VydCBJbmMxGTAXBgNVBAsTEHd3dy5kaWdpY2VydC5jb20xJDAiBgNV
# BAMTG0RpZ2lDZXJ0IEFzc3VyZWQgSUQgUm9vdCBDQTAeFw0yMjA4MDEwMDAwMDBa
# Fw0zMTExMDkyMzU5NTlaMGIxCzAJBgNVBAYTAlVTMRUwEwYDVQQKEwxEaWdpQ2Vy
# dCBJbmMxGTAXBgNVBAsTEHd3dy5kaWdpY2VydC5jb20xITAfBgNVBAMTGERpZ2lD
# ZXJ0IFRydXN0ZWQgUm9vdCBHNDCCAiIwDQYJKoZIhvcNAQEBBQADggIPADCCAgoC
# ggIBAL/mkHNo3rvkXUo8MCIwaTPswqclLskhPfKK2FnC4SmnPVirdprNrnsbhA3E
# MB/zG6Q4FutWxpdtHauyefLKEdLkX9YFPFIPUh/GnhWlfr6fqVcWWVVyr2iTcMKy
# unWZanMylNEQRBAu34LzB4TmdDttceItDBvuINXJIB1jKS3O7F5OyJP4IWGbNOsF
# xl7sWxq868nPzaw0QF+xembud8hIqGZXV59UWI4MK7dPpzDZVu7Ke13jrclPXuU1
# 5zHL2pNe3I6PgNq2kZhAkHnDeMe2scS1ahg4AxCN2NQ3pC4FfYj1gj4QkXCrVYJB
# MtfbBHMqbpEBfCFM1LyuGwN1XXhm2ToxRJozQL8I11pJpMLmqaBn3aQnvKFPObUR
# WBf3JFxGj2T3wWmIdph2PVldQnaHiZdpekjw4KISG2aadMreSx7nDmOu5tTvkpI6
# nj3cAORFJYm2mkQZK37AlLTSYW3rM9nF30sEAMx9HJXDj/chsrIRt7t/8tWMcCxB
# YKqxYxhElRp2Yn72gLD76GSmM9GJB+G9t+ZDpBi4pncB4Q+UDCEdslQpJYls5Q5S
# UUd0viastkF13nqsX40/ybzTQRESW+UQUOsxxcpyFiIJ33xMdT9j7CFfxCBRa2+x
# q4aLT8LWRV+dIPyhHsXAj6KxfgommfXkaS+YHS312amyHeUbAgMBAAGjggE6MIIB
# NjAPBgNVHRMBAf8EBTADAQH/MB0GA1UdDgQWBBTs1+OC0nFdZEzfLmc/57qYrhwP
# TzAfBgNVHSMEGDAWgBRF66Kv9JLLgjEtUYunpyGd823IDzAOBgNVHQ8BAf8EBAMC
# AYYweQYIKwYBBQUHAQEEbTBrMCQGCCsGAQUFBzABhhhodHRwOi8vb2NzcC5kaWdp
# Y2VydC5jb20wQwYIKwYBBQUHMAKGN2h0dHA6Ly9jYWNlcnRzLmRpZ2ljZXJ0LmNv
# bS9EaWdpQ2VydEFzc3VyZWRJRFJvb3RDQS5jcnQwRQYDVR0fBD4wPDA6oDigNoY0
# aHR0cDovL2NybDMuZGlnaWNlcnQuY29tL0RpZ2lDZXJ0QXNzdXJlZElEUm9vdENB
# LmNybDARBgNVHSAECjAIMAYGBFUdIAAwDQYJKoZIhvcNAQEMBQADggEBAHCgv0Nc
# Vec4X6CjdBs9thbX979XB72arKGHLOyFXqkauyL4hxppVCLtpIh3bb0aFPQTSnov
# Lbc47/T/gLn4offyct4kvFIDyE7QKt76LVbP+fT3rDB6mouyXtTP0UNEm0Mh65Zy
# oUi0mcudT6cGAxN3J0TU53/oWajwvy8LpunyNDzs9wPHh6jSTEAZNUZqaVSwuKFW
# juyk1T3osdz9HNj0d1pcVIxv76FQPfx2CWiEn2/K2yCNNWAcAgPLILCsWKAOQGPF
# mCLBsln1VWvPJ6tsds5vIy30fnFqI2si/xK4VC0nftg62fC2h5b9W9FcrBjDTZ9z
# twGpn1eqXijiuZQwggXaMIIDwqADAgECAhMZAAAAhQfvgiNOkFhuAAAAAACFMA0G
# CSqGSIb3DQEBDQUAMEYxEzARBgoJkiaJk/IsZAEZFgNsb2MxGjAYBgoJkiaJk/Is
# ZAEZFgpheGluZXR3b3JrMRMwEQYDVQQDEwpCLVMtVy1DQTIyMB4XDTI2MDYxNzEx
# MDkzM1oXDTI4MDYxNzExMTkzM1owXTELMAkGA1UEBhMCQ1oxDTALBgNVBAcTBEJy
# bm8xDjAMBgNVBAoTBUF4aW1hMQswCQYDVQQLEwJJVDEiMCAGA1UEAxMZcG93ZXJz
# aGVsbC5heGluZXR3b3JrLmxvYzB2MBAGByqGSM49AgEGBSuBBAAiA2IABMRThCb2
# dEtPjW08Gl+dFvco9KymXyCT3+9CLJ1R9iRyL3QnC41JyqcZTHNWV+RrArLYxBtS
# mspg9WkwavaYsivg4DA5f9e03a6v7P6oo1MYi7jJFzIsozYNccs1febLJ6OCAlYw
# ggJSMA4GA1UdDwEB/wQEAwIHgDATBgNVHSUEDDAKBggrBgEFBQcDAzAdBgNVHQ4E
# FgQUJvJxE/zq3s8V8DdPq9tV8ph79TIwHwYDVR0jBBgwFoAU5WQVg9uY/k5uW+Qo
# fYFsPHB9icEwgcwGA1UdHwSBxDCBwTCBvqCBu6CBuIaBtWxkYXA6Ly8vQ049Qi1T
# LVctQ0EyMixDTj1CLVMtVy1DQSxDTj1DRFAsQ049UHVibGljJTIwS2V5JTIwU2Vy
# dmljZXMsQ049U2VydmljZXMsQ049Q29uZmlndXJhdGlvbixEQz1heGluZXR3b3Jr
# LERDPWxvYz9jZXJ0aWZpY2F0ZVJldm9jYXRpb25MaXN0P2Jhc2U/b2JqZWN0Q2xh
# c3M9Y1JMRGlzdHJpYnV0aW9uUG9pbnQwgb8GCCsGAQUFBwEBBIGyMIGvMIGsBggr
# BgEFBQcwAoaBn2xkYXA6Ly8vQ049Qi1TLVctQ0EyMixDTj1BSUEsQ049UHVibGlj
# JTIwS2V5JTIwU2VydmljZXMsQ049U2VydmljZXMsQ049Q29uZmlndXJhdGlvbixE
# Qz1heGluZXR3b3JrLERDPWxvYz9jQUNlcnRpZmljYXRlP2Jhc2U/b2JqZWN0Q2xh
# c3M9Y2VydGlmaWNhdGlvbkF1dGhvcml0eTA9BgkrBgEEAYI3FQcEMDAuBiYrBgEE
# AYI3FQiFtqJghrGOEYXRmyeD4OUlh/eZcHKGnPdcgqLaaAIBZAIBDzAbBgkrBgEE
# AYI3FQoEDjAMMAoGCCsGAQUFBwMDMA0GCSqGSIb3DQEBDQUAA4ICAQBGEz8TYbhm
# n4zjFKUzh4W5LpslzsYTaEXBaGHSHRexg2n4B81jjyQVDtkBTAZ67WOw5OJIdNEc
# VR+iuUjsjQH8K9I0Z9vk/AlcnDQlO9e7par2YzLSQ+am9E1BGo+KJH7T+n2sQHrp
# C4VoOtWzyz0q7cfu1uwDQ2kc2WKThdK5COT1PDrPaZ7zpPLdu+bCTaVzK/Y2jOCJ
# m9VGpsiluTKQY9n1h7kQrq5643qY3Js8gedWNgMzTii0Y25VNvkRfophP8Zc2NwD
# bGLszFV7Ac2vlFppQdG8mRxXu9XxAozQqECbk1beQFR68I3ZM5GSEwBrjEYPeVoz
# XO6s9xe+Jdcox7RzPFBAaUb36qD0yjB6MswNpN6mX1uRsSygiIcPs0ARhr+VB8Sm
# CKK7AB8ojL2o/PFqvez67Nvy0bCcJNEa0vEACyqsCD5GpVlaTN3KQt7Xes+/PyPT
# Vf80Gm5iO/jDSrmxwdMdxzKBTffCIZG7QFi0D+pINKU+0v7HALhI13jJneaEiIjc
# Vz8/mS/0npRX3cXjEBb2hKDInl7MKeRbMTFyNkyUzAjmf2q6NypdaaRUDtD3XBxM
# KrNgrbqSGcIc1FTGmlmMjsguuJyQQk+y69RoKXCcUJHgBdSO2DS5ewksBLtk16Rx
# 2BSSQ5iCFYkLV4z0aJRfZ+UfQ+pE3IwxSDCCBrQwggScoAMCAQICEA3HrFcF/yGZ
# LkBDIgw6SYYwDQYJKoZIhvcNAQELBQAwYjELMAkGA1UEBhMCVVMxFTATBgNVBAoT
# DERpZ2lDZXJ0IEluYzEZMBcGA1UECxMQd3d3LmRpZ2ljZXJ0LmNvbTEhMB8GA1UE
# AxMYRGlnaUNlcnQgVHJ1c3RlZCBSb290IEc0MB4XDTI1MDUwNzAwMDAwMFoXDTM4
# MDExNDIzNTk1OVowaTELMAkGA1UEBhMCVVMxFzAVBgNVBAoTDkRpZ2lDZXJ0LCBJ
# bmMuMUEwPwYDVQQDEzhEaWdpQ2VydCBUcnVzdGVkIEc0IFRpbWVTdGFtcGluZyBS
# U0E0MDk2IFNIQTI1NiAyMDI1IENBMTCCAiIwDQYJKoZIhvcNAQEBBQADggIPADCC
# AgoCggIBALR4MdMKmEFyvjxGwBysddujRmh0tFEXnU2tjQ2UtZmWgyxU7UNqEY81
# FzJsQqr5G7A6c+Gh/qm8Xi4aPCOo2N8S9SLrC6Kbltqn7SWCWgzbNfiR+2fkHUil
# jNOqnIVD/gG3SYDEAd4dg2dDGpeZGKe+42DFUF0mR/vtLa4+gKPsYfwEu7EEbkC9
# +0F2w4QJLVSTEG8yAR2CQWIM1iI5PHg62IVwxKSpO0XaF9DPfNBKS7Zazch8NF5v
# p7eaZ2CVNxpqumzTCNSOxm+SAWSuIr21Qomb+zzQWKhxKTVVgtmUPAW35xUUFREm
# DrMxSNlr/NsJyUXzdtFUUt4aS4CEeIY8y9IaaGBpPNXKFifinT7zL2gdFpBP9qh8
# SdLnEut/GcalNeJQ55IuwnKCgs+nrpuQNfVmUB5KlCX3ZA4x5HHKS+rqBvKWxdCy
# QEEGcbLe1b8Aw4wJkhU1JrPsFfxW1gaou30yZ46t4Y9F20HHfIY4/6vHespYMQmU
# iote8ladjS/nJ0+k6MvqzfpzPDOy5y6gqztiT96Fv/9bH7mQyogxG9QEPHrPV6/7
# umw052AkyiLA6tQbZl1KhBtTasySkuJDpsZGKdlsjg4u70EwgWbVRSX1Wd4+zoFp
# p4Ra+MlKM2baoD6x0VR4RjSpWM8o5a6D8bpfm4CLKczsG7ZrIGNTAgMBAAGjggFd
# MIIBWTASBgNVHRMBAf8ECDAGAQH/AgEAMB0GA1UdDgQWBBTvb1NK6eQGfHrK4pBW
# 9i/USezLTjAfBgNVHSMEGDAWgBTs1+OC0nFdZEzfLmc/57qYrhwPTzAOBgNVHQ8B
# Af8EBAMCAYYwEwYDVR0lBAwwCgYIKwYBBQUHAwgwdwYIKwYBBQUHAQEEazBpMCQG
# CCsGAQUFBzABhhhodHRwOi8vb2NzcC5kaWdpY2VydC5jb20wQQYIKwYBBQUHMAKG
# NWh0dHA6Ly9jYWNlcnRzLmRpZ2ljZXJ0LmNvbS9EaWdpQ2VydFRydXN0ZWRSb290
# RzQuY3J0MEMGA1UdHwQ8MDowOKA2oDSGMmh0dHA6Ly9jcmwzLmRpZ2ljZXJ0LmNv
# bS9EaWdpQ2VydFRydXN0ZWRSb290RzQuY3JsMCAGA1UdIAQZMBcwCAYGZ4EMAQQC
# MAsGCWCGSAGG/WwHATANBgkqhkiG9w0BAQsFAAOCAgEAF877FoAc/gc9EXZxML2+
# C8i1NKZ/zdCHxYgaMH9Pw5tcBnPw6O6FTGNpoV2V4wzSUGvI9NAzaoQk97frPBtI
# j+ZLzdp+yXdhOP4hCFATuNT+ReOPK0mCefSG+tXqGpYZ3essBS3q8nL2UwM+NMvE
# uBd/2vmdYxDCvwzJv2sRUoKEfJ+nN57mQfQXwcAEGCvRR2qKtntujB71WPYAgwPy
# WLKu6RnaID/B0ba2H3LUiwDRAXx1Neq9ydOal95CHfmTnM4I+ZI2rVQfjXQA1WSj
# jf4J2a7jLzWGNqNX+DF0SQzHU0pTi4dBwp9nEC8EAqoxW6q17r0z0noDjs6+BFo+
# z7bKSBwZXTRNivYuve3L2oiKNqetRHdqfMTCW/NmKLJ9M+MtucVGyOxiDf06VXxy
# KkOirv6o02OoXN4bFzK0vlNMsvhlqgF2puE6FndlENSmE+9JGYxOGLS/D284NHNb
# oDGcmWXfwXRy4kbu4QFhOm0xJuF2EZAOk5eCkhSxZON3rGlHqhpB/8MluDezooIs
# 8CVnrpHMiD2wL40mm53+/j7tFaxYKIqL0Q4ssd8xHZnIn/7GELH3IdvG2XlM9q7W
# P/UwgOkw/HQtyRN62JK4S1C8uw3PdBunvAZapsiI5YKdvlarEvf8EA+8hcpSM9LH
# JmyrxaFtoza2zNaQ9k+5t1wwggbtMIIE1aADAgECAhAIT9wzT35FTtvDD4/5khg1
# MA0GCSqGSIb3DQEBCwUAMGkxCzAJBgNVBAYTAlVTMRcwFQYDVQQKEw5EaWdpQ2Vy
# dCwgSW5jLjFBMD8GA1UEAxM4RGlnaUNlcnQgVHJ1c3RlZCBHNCBUaW1lU3RhbXBp
# bmcgUlNBNDA5NiBTSEEyNTYgMjAyNSBDQTEwHhcNMjYwODA1MDAwMDAwWhcNMzcx
# MTA0MjM1OTU5WjBjMQswCQYDVQQGEwJVUzEXMBUGA1UEChMORGlnaUNlcnQsIElu
# Yy4xOzA5BgNVBAMTMkRpZ2lDZXJ0IFNIQTI1NiBSU0E0MDk2IFRpbWVzdGFtcCBS
# ZXNwb25kZXIgMjAyNiAxMIICIjANBgkqhkiG9w0BAQEFAAOCAg8AMIICCgKCAgEA
# tnum8sn+zUr41JtMZbP9OMYw+HwJDpG5xkIu/lqcfNYmMX81YmsUiHLbh9ykpeWB
# GKTLhYBrAN9Tdg/QEzG32XcObmgIblnr0CoQ3WSAeDZ6nH6X6VkFyYkJw3QBJREw
# vm4UhLzSxmwPA7cFKRTEOMsmEEj6qJk/dqLEAL+oQYuOwE2UuiX1Vnul8YReIyWd
# 4kgLn9gq6LNXM0UplkR6jL/QHxmb6fMoGBJYbnaUI7XD6cKDpekK2SVMld4iDbze
# HDtOaaxldH5IxuNusQ69nd8/ZXEiB5Hbxj3RlK13cX1W4DlFXKdv/CEhM8Cj1vvl
# mvhNroyPdRGbbpBlgyf8Wdu5N6ByhFwURn0U6ozlPoxN22v+fviUhP+6DR547OZn
# pBMWDfei1f5sVGwiiW/KQTWOK97g+4RJpPzPNV4VYMAwO2jM2Aty2QYPVmOQTJm0
# msuXnJrSbl2gf9JylpkJlWXqk1Q4LJsxz+TELoQCZIljbgvTJgoPU2R12ydv8i1U
# qL/adelA0y7U9Pmmtbze9Xx3rtajC5SzQd1jgfwAwsa90v9YcSPdmeoyoBBA/27c
# CL237l5DTYYPDLQ4ON3OLTGWnvRb6jDrf/T75gMRfUzSLCBQfBusm9+mSWRlC/Df
# 6S/e9Q8i13CuhzOT2Jx+V/nlbXM4QoBwlUAhelwwJT0CAwEAAaOCAZUwggGRMAwG
# A1UdEwEB/wQCMAAwHQYDVR0OBBYEFBTJY4owLtRK+26U8+bjQH717M3iMB8GA1Ud
# IwQYMBaAFO9vU0rp5AZ8esrikFb2L9RJ7MtOMA4GA1UdDwEB/wQEAwIHgDAWBgNV
# HSUBAf8EDDAKBggrBgEFBQcDCDCBlQYIKwYBBQUHAQEEgYgwgYUwJAYIKwYBBQUH
# MAGGGGh0dHA6Ly9vY3NwLmRpZ2ljZXJ0LmNvbTBdBggrBgEFBQcwAoZRaHR0cDov
# L2NhY2VydHMuZGlnaWNlcnQuY29tL0RpZ2lDZXJ0VHJ1c3RlZEc0VGltZVN0YW1w
# aW5nUlNBNDA5NlNIQTI1NjIwMjVDQTEuY3J0MF8GA1UdHwRYMFYwVKBSoFCGTmh0
# dHA6Ly9jcmwzLmRpZ2ljZXJ0LmNvbS9EaWdpQ2VydFRydXN0ZWRHNFRpbWVTdGFt
# cGluZ1JTQTQwOTZTSEEyNTYyMDI1Q0ExLmNybDAgBgNVHSAEGTAXMAgGBmeBDAEE
# AjALBglghkgBhv1sBwEwDQYJKoZIhvcNAQELBQADggIBAI3FOmEenVIK35msCYB+
# fShAsWvSYvLBItoNdAgQ2jIqrGsVsluXMJU/+mRebBc52s6lbKAvOVPXaizmKkML
# LflEEKDZQx4CkS2t8aHPjkXha3hYZ010htFa3dhNgmalH5vuWvh3tTCf4frTS7gP
# tGc4Z/xaPhQ2AB1mR8eEe/WbH0RWHvVIl6VwQ3+g5FKNfN2N/DWJkf13w2H+2Gfq
# Efbd35Ww8CvoYBjLNIDTadcPWdgsjsiOaK/7EsKJgLjUNIVgvcaFOLLQ/GlrA+0Z
# HJoFUbOr5SJN8zykPspXIXlpDJY/gqFUZRROeab9GVgmhbdOJcD/63RhxPahFUGb
# ckRONqMe6DYAv6/mOG0pWd3cPStsdcS7buj5DyniwRY8yooMH6ptx5vpP/pZzBPB
# eZD2U4IsthyxB5Jaa8qrOkB5z160TXiM5ADMspZ0TfD9MJoq0tFpFPssKRFhWeED
# YPvcUuN7U7lvcdHl4ezQ3NT/7Ffs1sR1yh/LRbdZ3B3Vc6q2WmD8mDC0p9kzl2o7
# 3iVtS946IkEj7FkRsZGww1teYxERROC745xrtjvcw9ZyyUjHZWGRIpJeMNsPquCD
# f0fkyHtB+J4AiNZqCQk23rxh+KbpyMTNVKItJ5l92Svl20U9NbqMBOVYl1h54NEY
# LJq1/xHWFKPNK903zJZA9P2DMYIEnDCCBJgCAQEwXTBGMRMwEQYKCZImiZPyLGQB
# GRYDbG9jMRowGAYKCZImiZPyLGQBGRYKYXhpbmV0d29yazETMBEGA1UEAxMKQi1T
# LVctQ0EyMgITGQAAAIUH74IjTpBYbgAAAAAAhTANBglghkgBZQMEAgEFAKCBhDAY
# BgorBgEEAYI3AgEMMQowCKACgAChAoAAMBkGCSqGSIb3DQEJAzEMBgorBgEEAYI3
# AgEEMBwGCisGAQQBgjcCAQsxDjAMBgorBgEEAYI3AgEVMC8GCSqGSIb3DQEJBDEi
# BCD7002kqPoWF6cy7aXLIzrd8XoKZ7WXFH3OcL5Vt+W0XjALBgcqhkjOPQIBBQAE
# ZzBlAjEAuKTZ80miCEUJf5oXzrxGWqtIiwbcCziY7XNJCIewMeqC40wNJE6pHSJK
# GzinL9YFAjBA85zkeRn3V/NEnbVem8Z5FxsjtSyE3+4mQcsWSbYnKIU6HcazcsOh
# /4u38upCGyuhggMmMIIDIgYJKoZIhvcNAQkGMYIDEzCCAw8CAQEwfTBpMQswCQYD
# VQQGEwJVUzEXMBUGA1UEChMORGlnaUNlcnQsIEluYy4xQTA/BgNVBAMTOERpZ2lD
# ZXJ0IFRydXN0ZWQgRzQgVGltZVN0YW1waW5nIFJTQTQwOTYgU0hBMjU2IDIwMjUg
# Q0ExAhAIT9wzT35FTtvDD4/5khg1MA0GCWCGSAFlAwQCAQUAoGkwGAYJKoZIhvcN
# AQkDMQsGCSqGSIb3DQEHATAcBgkqhkiG9w0BCQUxDxcNMjYwOTEwMDgwODQ1WjAv
# BgkqhkiG9w0BCQQxIgQgI6MFORcX9hK5qRuFOgZ6Nx/iodg6513d5x1ijZn5pXkw
# DQYJKoZIhvcNAQEBBQAEggIAOsA6+Smk7pCVfxvqjB/GEewTmnVBfW03C8btlN7S
# JmFnapwnA3DJU3GuDcoHXLk3mAP/t5BIzoPdkpOjD0EPZSaMf9R9/hwTww+qzluo
# oao9CfvJOOCPlyyrNyO0FxSR04sJMH28YzXgl+SSaIcCnFU/KutV1P5hpucC+3Vn
# pFmobqfEU3yvfmX0piPLpzpyGuIEIJSoF61AGkK7u7IYKBDPxRXIlWoZYS+X5+QN
# rM9eupRTyWE4KowHUriv+0wr5SsqWtRHdNm4Z6WQX/lN8SRv9+NJHeCLvEEyB2Bm
# 3m7uVDNuxd9qxnwAmHEvlHpNieOaUpHK/YjgD9Jd7bkcSc4/zsyEHV8Tpylf/Y1F
# z4G0qlxBc4bKpAJGRtR5eM4abD5JqYQNMd4WYx4cp8SY+jG6kCV7vUtSYRokg6M5
# jZlT6u7mviorG2cjbAdE+MSwdKB1iMVAt1yb9mSolLG8ruPA1gcDrGRPBYS9NJN2
# JKSegA5+u1sr5fCMQurDAH94bq1ytmctkBAMtyoPNwpJBMN4CH8UmR3PU+bfRY8+
# xLkjPPIu8bV5TJKrnH3QqCe5OeroyJmTCQGtCSUAt1aGhmjNFgEtHoaV/vfsZ6J7
# ycpjWbNJN63hP5pUdz23ZpX0P3Bn6HUsue0AQGmnDfskfoKQjoz9sJKXY6q0sp7t
# d1A=
# SIG # End signature block
