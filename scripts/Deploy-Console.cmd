@echo off
setlocal enabledelayedexpansion
rem ============================================================
rem Deploy-Console.cmd - nasazeni administratorske konzole na APP_SERVER.
rem
rem PROC TENHLE SKRIPT EXISTUJE (11.09.2026):
rem   Do tohoto dne se konzole nasazovala rucnim robocopy bez /XF
rem   appsettings.local.json - na rozdil od Deploy-Api.cmd, ktery tohle
rem   vyloucil uz od zacatku. Rucni prikaz omylem prepsal produkcni
rem   appsettings.local.json (skutecny SQL server, DevAllowAll=false)
rem   lokalnim vyvojovym souborem (Kestrel na 127.0.0.1, DevAllowAll=true) -
rem   konzole pak byla ~15 minut nedostupna zvenku a bezela s vypnutym
rem   overovanim opravneni. Skript existuje, aby se rucni krok uz nikdy
rem   neopakoval - stejny "stop, kopie s /XF, start, over" vzor jako
rem   Deploy-Api.cmd, jen bez scheduled tasku - konzole bezi primo na
rem   APP_SERVER, kam ma admin ucet (na rozdil od SQL_SERVER) primy pristup.
rem
rem PROC .cmd A NE .ps1:
rem   Prostredi vynucuje AllSigned pres GPO. Davka mu nepodleha, takze
rem   se tenhle krok nemusi pri kazde zmene znovu podepisovat. Zadny
rem   PowerShell tu neni potreba - staci sc.exe a robocopy.
rem
rem POUZITI (spoustet ze stanice s adminem na APP_SERVER, po dotnet publish):
rem   Deploy-Console.cmd <ZDROJ> <APP_SERVER_HOST> [NAZEV_SLUZBY]
rem
rem   ZDROJ            slozka s publikovanou konzoli (na tomto stroji)
rem   APP_SERVER_HOST  jmeno nebo IP serveru, kde konzole bezi (napr. 10.8.2.213)
rem   NAZEV_SLUZBY     volitelne, vychozi "USBGuardianConsole"
rem
rem Navratovy kod: 0 = sluzba bezi, jinak nenulovy.
rem ============================================================

set "SRC=%~1"
set "APPHOST=%~2"
set "SVC=%~3"
if "%SVC%"=="" set "SVC=USBGuardianConsole"

if "%SRC%"=="" goto :usage
if "%APPHOST%"=="" goto :usage

set "SHARE=\\%APPHOST%\C$\Apps\USBGuardianConsole"
rem Log jde na APPHOST (kde bezi konzole), NE na stroj, ze ktereho se skript spousti - ten
rem nemusi mit (a typicky nema) pravo zapisovat do C:\ProgramData na sve vlastni strane.
rem Nedbala verze (11.09.2026) tohle mela na lokalnim %ProgramData% - selhany zapis do
rem neexistujici/nezapisovatelne slozky pokazil errorlevel az k robocopy prikazu, takze
rem se hlasilo "uspesne, 0 zkopirovano" i kdyz robocopy ve skutecnosti vubec neprobehl.
set "LOGDIR=\\%APPHOST%\C$\ProgramData\USBGuardian\deploy"
set "LOG=%LOGDIR%\console-deploy.log"
if not exist "%LOGDIR%" mkdir "%LOGDIR%" >nul 2>&1

call :log "=== %DATE% %TIME% :: nasazeni konzole na %APPHOST% (sluzba: %SVC%) ==="
call :log "zdroj: %SRC%"
call :log "cil:   %SHARE%"

if not exist "%SRC%\" (
  call :log "CHYBA: zdrojova slozka neexistuje"
  exit /b 2
)

rem -- 1) zastavit sluzbu --------------------------------------
call :log "zastavuji sluzbu..."
sc.exe \\%APPHOST% stop "%SVC%" >nul 2>&1

set /a TRIES=0
:waitstop
set /a TRIES+=1
sc.exe \\%APPHOST% query "%SVC%" 2>nul | find "STOPPED" >nul
if not errorlevel 1 goto :stopped
if %TRIES% GEQ 30 (
  call :log "CHYBA: sluzba se do 60 s nezastavila - nekopiruji, aby nezustala pulka nove verze"
  exit /b 3
)
ping -n 3 127.0.0.1 >nul
goto :waitstop

:stopped
call :log "sluzba zastavena po %TRIES% pokusech"

rem -- 2) zkopirovat ---------------------------------------------
rem appsettings.local.json zustava na serveru - je v nem skutecny SQL server,
rem realne AD skupiny a Kestrel binding. PRESNE tohle pole zpusobilo dnesni
rem vypadek, kdyz se vylouceni vynechalo (viz hlavicka souboru).
robocopy "%SRC%" "%SHARE%" /E /XF appsettings.local.json /R:2 /W:5 /NFL /NDL /NJH /NP >> "%LOG%" 2>&1
set RC=%ERRORLEVEL%
call :log "robocopy navratovy kod: %RC% (0-7 = v poradku)"
if %RC% GEQ 8 (
  call :log "CHYBA: kopirovani selhalo - startuji sluzbu zpet ve stare verzi"
  sc.exe \\%APPHOST% start "%SVC%" >nul 2>&1
  exit /b 4
)

rem -- 3) nastartovat a overit -------------------------------------
call :log "startuji sluzbu..."
sc.exe \\%APPHOST% start "%SVC%" >nul 2>&1

set /a TRIES=0
:waitrun
set /a TRIES+=1
sc.exe \\%APPHOST% query "%SVC%" 2>nul | find "RUNNING" >nul
if not errorlevel 1 goto :running
if %TRIES% GEQ 20 (
  call :log "CHYBA: sluzba po nasazeni NENABEHLA - podivej se do Event Logu na %APPHOST%"
  exit /b 5
)
ping -n 3 127.0.0.1 >nul
goto :waitrun

:running
call :log "HOTOVO: sluzba bezi (%TRIES% pokusu)"
exit /b 0

:usage
echo Pouziti: Deploy-Console.cmd ^<ZDROJ^> ^<APP_SERVER_HOST^> [NAZEV_SLUZBY]
echo   napr.: Deploy-Console.cmd "C:\Apps\USBGuardianConsolePublish" 10.8.2.213
exit /b 1

:log
echo %~1
echo %~1>> "%LOG%"
exit /b 0
