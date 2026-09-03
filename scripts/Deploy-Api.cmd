@echo off
setlocal enabledelayedexpansion
rem ============================================================
rem Deploy-Api.cmd — nasazeni API na jeho server.
rem
rem PROC .cmd A NE .ps1:
rem   Prostredi vynucuje AllSigned pres GPO. Davka mu nepodleha, takze
rem   se tenhle krok nemusi pri kazde zmene znovu podepisovat. Zadny
rem   PowerShell tu neni potreba - staci sc.exe a robocopy.
rem
rem PROC SAMOSTATNY UCET:
rem   Spousti se jako scheduled task pod SERVEROVYM gMSA. Klientsky
rem   deploy ucet uz na serveru API zadna prava nema - to je zamer,
rem   hranice mezi vrstvami (fleet x server).
rem
rem POUZITI:
rem   Deploy-Api.cmd <ZDROJ> <HOST> <CILOVA_CESTA> [NAZEV_SLUZBY]
rem
rem   ZDROJ         slozka s publikovanym API (na tomto stroji)
rem   HOST          jmeno nebo IP serveru, kde API bezi
rem   CILOVA_CESTA  cesta na cili ve tvaru admin share, napr. C$\USBGuardian.Api
rem   NAZEV_SLUZBY  volitelne, vychozi "USB Guardian API"
rem
rem Navratovy kod: 0 = sluzba bezi, jinak nenulovy (task to ukaze jako Last Result).
rem ============================================================

set "SRC=%~1"
set "APIHOST=%~2"
set "DST=%~3"
set "SVC=%~4"
if "%SVC%"=="" set "SVC=USB Guardian API"

if "%SRC%"=="" goto :usage
if "%APIHOST%"=="" goto :usage
if "%DST%"=="" goto :usage

set "SHARE=\\%APIHOST%\%DST%"
set "LOG=%ProgramData%\USBGuardian\deploy\api-deploy.log"
if not exist "%ProgramData%\USBGuardian\deploy" mkdir "%ProgramData%\USBGuardian\deploy" >nul 2>&1

call :log "=== %DATE% %TIME% :: nasazeni API na %APIHOST% (sluzba: %SVC%) ==="
call :log "zdroj: %SRC%"
call :log "cil:   %SHARE%"

if not exist "%SRC%\" (
  call :log "CHYBA: zdrojova slozka neexistuje"
  exit /b 2
)

rem ── 1) zastavit sluzbu ───────────────────────────────────────
call :log "zastavuji sluzbu..."
sc.exe \\%APIHOST% stop "%SVC%" >nul 2>&1

rem Cekat na STOPPED. Bez toho zustane USBGuardian.Api.exe zamceny,
rem robocopy na nem selze a na serveru dal bezi STARA verze - a nikdo
rem si toho nevsimne, protoze deploy "probehl".
set /a TRIES=0
:waitstop
set /a TRIES+=1
sc.exe \\%APIHOST% query "%SVC%" 2>nul | find "STOPPED" >nul
if not errorlevel 1 goto :stopped
if %TRIES% GEQ 30 (
  call :log "CHYBA: sluzba se do 60 s nezastavila - nekopiruji, aby nezustala pulka nove verze"
  exit /b 3
)
ping -n 3 127.0.0.1 >nul
goto :waitstop

:stopped
call :log "sluzba zastavena po %TRIES% pokusech"

rem ── 2) zkopirovat ────────────────────────────────────────────
rem appsettings.local.json zustava na serveru - je v nem pripojeni k DB
rem a firemni hodnoty, ktere do balicku nepatri.
robocopy "%SRC%" "%SHARE%" /E /XF appsettings.local.json /R:2 /W:5 /NFL /NDL /NJH /NP >> "%LOG%" 2>&1
set RC=%ERRORLEVEL%
call :log "robocopy navratovy kod: %RC% (0-7 = v poradku)"
if %RC% GEQ 8 (
  call :log "CHYBA: kopirovani selhalo - startuji sluzbu zpet ve stare verzi"
  sc.exe \\%APIHOST% start "%SVC%" >nul 2>&1
  exit /b 4
)

rem ── 3) nastartovat a overit ──────────────────────────────────
call :log "startuji sluzbu..."
sc.exe \\%APIHOST% start "%SVC%" >nul 2>&1

set /a TRIES=0
:waitrun
set /a TRIES+=1
sc.exe \\%APIHOST% query "%SVC%" 2>nul | find "RUNNING" >nul
if not errorlevel 1 goto :running
if %TRIES% GEQ 20 (
  call :log "CHYBA: sluzba po nasazeni NENABEHLA - podivej se do Event Logu na %APIHOST%"
  exit /b 5
)
ping -n 3 127.0.0.1 >nul
goto :waitrun

:running
call :log "HOTOVO: sluzba bezi (%TRIES% pokusu)"
exit /b 0

:usage
echo Pouziti: Deploy-Api.cmd ^<ZDROJ^> ^<HOST^> ^<CILOVA_CESTA^> [NAZEV_SLUZBY]
echo   napr.: Deploy-Api.cmd "C:\Apps\USBGuardianApiPublish" SQL-SERVER "C$\USBGuardian.Api"
exit /b 1

:log
echo %~1
echo %~1>> "%LOG%"
exit /b 0
