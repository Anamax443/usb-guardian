@echo off
setlocal enabledelayedexpansion
rem ============================================================
rem Archive-AgentVersion.cmd — uloz balicek agenta do archivu verzi.
rem
rem PROC ARCHIV:
rem   Bez nej neni kam se vratit. Kdyz se nova verze ukaze jako spatna,
rem   navrat zpet = Set-AgentVersion.cmd se starsim commitem — ale jen
rem   pokud ten balicek jeste existuje.
rem
rem PROC HO ZAROVEN UKLIZET:
rem   Kazdy balicek ma pres 100 MB. Bez stropu by archiv za rok snedl disk
rem   a nikdo by si toho nevsiml, dokud by nedosel.
rem   Strop je v souboru keep.txt v korenu archivu (pise ho konzole
rem   z Nastaveni), vychozi 10. Aktualne SCHVALENA verze se nemaze nikdy,
rem   i kdyby byla mimo strop — jinak by slo prijit o to, co prave bezi.
rem
rem POUZITI:  Archive-AgentVersion.cmd <ZDROJ> <COMMIT> [ARCHIV] [PUBLISH]
rem ============================================================

set "SRC=%~1"
set "COMMIT=%~2"
set "ARCH=%~3"
set "PUB=%~4"
if "%ARCH%"=="" set "ARCH=C:\Apps\USBGuardianAgentVersions"
if "%PUB%"=="" set "PUB=C:\Apps\USBGuardianAgentPublish"
if "%SRC%"=="" goto :usage
if "%COMMIT%"=="" goto :usage

set "LOGDIR=%ProgramData%\USBGuardian\deploy"
if not exist "%LOGDIR%" mkdir "%LOGDIR%" >nul 2>&1
set "LOG=%LOGDIR%\agent-version.log"

if not exist "%SRC%\USBGuardian.exe" (
  echo CHYBA: ve zdroji "%SRC%" neni USBGuardian.exe.
  exit /b 2
)

if not exist "%ARCH%" mkdir "%ARCH%" >nul 2>&1

echo Ukladam verzi %COMMIT% do archivu...
robocopy "%SRC%" "%ARCH%\%COMMIT%" /MIR /R:2 /W:2 /NFL /NDL /NJH /NP >> "%LOG%" 2>&1
if %ERRORLEVEL% GEQ 8 (
  echo CHYBA: ulozeni do archivu selhalo ^(robocopy %ERRORLEVEL%^).
  exit /b 3
)
call :log "%DATE% %TIME%  archiv += %COMMIT%  (%USERNAME%)"

rem ── strop poctu verzi ────────────────────────────────────────
set "KEEP=10"
if exist "%ARCH%\keep.txt" (
  for /f "usebackq tokens=1 delims= " %%K in ("%ARCH%\keep.txt") do set "KEEP=%%K"
)
rem Nesmysl v souboru nesmi vest ke smazani vseho.
set /a KEEPN=%KEEP% 2>nul
if not defined KEEPN set /a KEEPN=10
if %KEEPN% LSS 2 set /a KEEPN=2

rem Ktere verze jsou prave v kanalech - ty nemazat za zadnou cenu.
rem Stable i beta: prijit o balicek, ktery prave nekde bezi nebo se zkousi,
rem znamena prijit o moznost vratit se zpet.
set "AKTIVNI="
set "AKTIVNI2="
if exist "%PUB%\VERSION.txt" (
  for /f "usebackq tokens=1 delims= " %%V in ("%PUB%\VERSION.txt") do (
    if not defined AKTIVNI set "AKTIVNI=%%V"
  )
)
if exist "%PUB%Beta\VERSION.txt" (
  for /f "usebackq tokens=1 delims= " %%V in ("%PUB%Beta\VERSION.txt") do (
    if not defined AKTIVNI2 set "AKTIVNI2=%%V"
  )
)

echo Strop archivu: %KEEPN% verzi ^(stable "%AKTIVNI%" a beta "%AKTIVNI2%" se nemazou^).
set /a I=0
for /f "delims=" %%D in ('dir /b /ad /o-d "%ARCH%" 2^>nul') do (
  if exist "%ARCH%\%%D\USBGuardian.exe" (
    set /a I+=1
    if !I! GTR %KEEPN% (
      if /I "%%D"=="%AKTIVNI%" (
        echo   ponechavam %%D - je to stable
        call :log "  ponechano %%D (stable, mimo strop)"
      ) else if /I "%%D"=="%AKTIVNI2%" (
        echo   ponechavam %%D - je to beta
        call :log "  ponechano %%D (beta, mimo strop)"
      ) else (
        echo   mazu starou verzi %%D
        rd /s /q "%ARCH%\%%D" 2>nul
        call :log "  smazano %%D (mimo strop %KEEPN%)"
      )
    )
  )
)

echo.
echo   HOTOVO. V archivu:
for /f "delims=" %%D in ('dir /b /ad /o-d "%ARCH%" 2^>nul') do (
  if exist "%ARCH%\%%D\USBGuardian.exe" (
    if /I "%%D"=="%AKTIVNI%" ( echo      %%D   ^<- stable
    ) else if /I "%%D"=="%AKTIVNI2%" ( echo      %%D   ^<- beta
    ) else ( echo      %%D )
  )
)
echo.
exit /b 0

:usage
echo Pouziti: Archive-AgentVersion.cmd ^<ZDROJ^> ^<COMMIT^> [ARCHIV] [PUBLISH]
echo   napr.: Archive-AgentVersion.cmd "C:\Apps\_stage" 560722b
exit /b 1

:log
echo %~1>> "%LOG%"
exit /b 0
