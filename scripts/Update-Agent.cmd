@echo off
setlocal enabledelayedexpansion
rem ============================================================
rem Update-Agent.cmd — aktualizace UŽ NAINSTALOVANÉHO agenta.
rem
rem PROC SAMOSTATNE OD DEPLOY-AGENTFLEET:
rem   Fleet skript umi cistou instalaci. Aktualizace je jina uloha: soubory
rem   drzi BEZICI sluzba, takze se musi napred zastavit. Kdyz se to neudela,
rem   robocopy prepise cast DLL, na zamcenem USBGuardian.exe selze a na
rem   stanici zustane rozjeta smes verzi — a deploy pritom "probehl".
rem
rem PROC .cmd A NE .ps1:
rem   AllSigned z GPO. Davka mu nepodleha, takze zmena aktualizacniho kroku
rem   nevyzaduje pokazde podpis.
rem
rem POUZITI:
rem   Update-Agent.cmd <ZDROJ> <HOST | SOUBOR_S_HOSTY> [NAZEV_SLUZBY]
rem
rem   ZDROJ            slozka s balickem agenta (napr. C:\Apps\USBGuardianAgentPublish)
rem   HOST             jmeno stanice, NEBO cesta k souboru se seznamem (jeden host na radek)
rem   NAZEV_SLUZBY     volitelne, vychozi "USB Guardian"
rem
rem Navratovy kod: 0 = vsechny stanice OK, jinak pocet neuspesnych.
rem ============================================================

set "SRC=%~1"
set "CIL=%~2"
set "SVC=%~3"
if "%SVC%"=="" set "SVC=USB Guardian"
if "%SRC%"=="" goto :usage
if "%CIL%"=="" goto :usage

set "LOGDIR=%ProgramData%\USBGuardian\deploy"
if not exist "%LOGDIR%" mkdir "%LOGDIR%" >nul 2>&1
set "LOG=%LOGDIR%\update-agent.log"
set /a CHYB=0
set /a OK=0

call :log "=== %DATE% %TIME% :: aktualizace agenta ==="
call :log "zdroj: %SRC%"

if not exist "%SRC%\USBGuardian.exe" (
  call :log "CHYBA: ve zdroji neni USBGuardian.exe — spatny balicek?"
  exit /b 99
)

rem Seznam stanic, nebo jedna stanice.
if exist "%CIL%" (
  call :log "seznam stanic: %CIL%"
  for /f "usebackq eol=# tokens=* delims= " %%H in ("%CIL%") do (
    if not "%%H"=="" call :jednaStanice "%%H"
  )
) else (
  call :jednaStanice "%CIL%"
)

call :log "--- hotovo: OK=%OK%  chyb=%CHYB% ---"
exit /b %CHYB%

rem ============================================================
:jednaStanice
set "H=%~1"
call :log ""
call :log "--- %H% ---"

rem 1) je tam vubec sluzba? Aktualizace neni instalace.
sc.exe \\%H% query "%SVC%" >nul 2>&1
if errorlevel 1 (
  call :log "%H%: PRESKOCENO — sluzba tam neni (na cistou instalaci je Deploy-AgentFleet)"
  goto :konecStanice
)

rem 2) zastavit a POCKAT. Bez cekani zustane .exe zamceny.
call :log "%H%: zastavuji sluzbu"
sc.exe \\%H% stop "%SVC%" >nul 2>&1
set /a T=0
:cekejStop
set /a T+=1
sc.exe \\%H% query "%SVC%" 2>nul | find "STOPPED" >nul
if not errorlevel 1 goto :jeStop
if !T! GEQ 20 (
  call :log "%H%: CHYBA — sluzba se do 60 s nezastavila, NEKOPIRUJI"
  set /a CHYB+=1
  goto :konecStanice
)
ping -n 4 127.0.0.1 >nul
goto :cekejStop
:jeStop

rem 3) kopie
robocopy "%SRC%" "\\%H%\C$\Program Files\USBGuardian" /E /XF Install-Agent.cmd Uninstall-Agent.cmd /R:2 /W:5 /NFL /NDL /NJH /NP >> "%LOG%" 2>&1
set RC=!ERRORLEVEL!
if !RC! GEQ 8 (
  call :log "%H%: CHYBA — kopie selhala (robocopy !RC!), startuji zpet starou verzi"
  sc.exe \\%H% start "%SVC%" >nul 2>&1
  set /a CHYB+=1
  goto :konecStanice
)
call :log "%H%: zkopirovano (robocopy !RC!)"

rem 4) start a overeni — deploy, ktery neoveri, ze to bezi, je jen prani
sc.exe \\%H% start "%SVC%" >nul 2>&1
set /a T=0
:cekejRun
set /a T+=1
sc.exe \\%H% query "%SVC%" 2>nul | find "RUNNING" >nul
if not errorlevel 1 goto :jeRun
if !T! GEQ 15 (
  call :log "%H%: CHYBA — sluzba po aktualizaci NENABEHLA"
  set /a CHYB+=1
  goto :konecStanice
)
ping -n 4 127.0.0.1 >nul
goto :cekejRun
:jeRun
call :log "%H%: OK — sluzba bezi"
set /a OK+=1

:konecStanice
exit /b 0

:usage
echo Pouziti: Update-Agent.cmd ^<ZDROJ^> ^<HOST ^| SOUBOR_S_HOSTY^> [NAZEV_SLUZBY]
echo   napr.: Update-Agent.cmd "C:\Apps\USBGuardianAgentPublish" TRNKAMW11
echo          Update-Agent.cmd "C:\Apps\USBGuardianAgentPublish" "C:\ProgramData\USBGuardian\deploy\update.txt"
exit /b 1

:log
echo %~1
echo %~1>> "%LOG%"
exit /b 0
