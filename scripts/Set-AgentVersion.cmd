@echo off
setlocal enabledelayedexpansion
rem ============================================================
rem Set-AgentVersion.cmd — ktera verze agenta je STABLE a ktera BETA.
rem
rem PROC DVA KANALY:
rem   Nova verze jde nejdriv na vzorek stroju (beta) a teprve kdyz se
rem   osvedci, na zbytek (stable). Bez oddelenych kanalu by "vyzkouset to
rem   na par strojich" znamenalo prepsat to, co se rozvazi vsem.
rem
rem   stable  -> ...\USBGuardianAgentPublish\       (uloha UpdateAgent)
rem   beta    -> ...\USBGuardianAgentPublishBeta\   (uloha UpdateAgentBeta)
rem   ostatni verze v archivu = archiv, nerozvazi se nikam
rem
rem NAVRAT ZPET je tentyz prikaz se starsim commitem:
rem   Set-AgentVersion.cmd f2bb194 stable
rem   schtasks /Run /TN "\USBGuardian\USBGuardian-UpdateAgent"
rem
rem POUZITI:  Set-AgentVersion.cmd <commit> [stable|beta] [ARCHIV]
rem           vychozi kanal: stable
rem ============================================================

set "COMMIT=%~1"
set "KANAL=%~2"
set "ARCH=%~3"
if "%KANAL%"=="" set "KANAL=stable"
if "%ARCH%"=="" set "ARCH=C:\Apps\USBGuardianAgentVersions"
if "%COMMIT%"=="" goto :usage

if /I "%KANAL%"=="stable" (
  set "PUB=C:\Apps\USBGuardianAgentPublish"
) else if /I "%KANAL%"=="beta" (
  set "PUB=C:\Apps\USBGuardianAgentPublishBeta"
) else (
  echo CHYBA: kanal musi byt "stable" nebo "beta", ne "%KANAL%".
  exit /b 2
)

set "ZDROJ=%ARCH%\%COMMIT%"

rem Log vedle archivu, NE v ProgramData\USBGuardian - viz Archive-AgentVersion.cmd:
rem ten adresar je ACL zamceny na SYSTEM/Administrators, bezny ucet do nej nezapise.
set "LOGDIR=%ARCH%\_logs"
if not exist "%LOGDIR%" mkdir "%LOGDIR%" >nul 2>&1
set "LOG=%LOGDIR%\agent-version.log"
if not exist "%LOGDIR%" (
  echo CHYBA: log adresar "%LOGDIR%" se nepodarilo vytvorit - zkontroluj prava zapisu do "%ARCH%".
  exit /b 9
)

echo.
if not exist "%ZDROJ%\USBGuardian.exe" (
  echo CHYBA: v archivu neni verze "%COMMIT%".
  echo.
  echo K dispozici:
  for /d %%D in ("%ARCH%\*") do (
    if exist "%%D\USBGuardian.exe" echo    %%~nxD
  )
  exit /b 3
)

set "STARA=?"
if exist "%PUB%\VERSION.txt" (
  for /f "usebackq tokens=1 delims= " %%V in ("%PUB%\VERSION.txt") do (
    if "!STARA!"=="?" set "STARA=%%V"
  )
)

echo Kanal %KANAL%: %COMMIT%   ^(dosud: !STARA!^)
call :log "%DATE% %TIME%  %KANAL%: !STARA! -> %COMMIT%  (%USERNAME%)"

rem /MIR schvalne: kanalova slozka musi byt PRESNA kopie archivu. Jinak by
rem po starsi verzi zustaly soubory, ktere uz do balicku nepatri, a na
rem stanici by vznikla smes.
robocopy "%ZDROJ%" "%PUB%" /MIR /XF VERSION.txt /R:2 /W:2 /NFL /NDL /NJH /NP >> "%LOG%" 2>&1
if %ERRORLEVEL% GEQ 8 (
  echo CHYBA: kopie z archivu selhala ^(robocopy %ERRORLEVEL%^).
  call :log "CHYBA robocopy %ERRORLEVEL%"
  exit /b 4
)
if not exist "%PUB%\USBGuardian.exe" (
  echo CHYBA: po kopii neni v "%PUB%" USBGuardian.exe - prepnuti kanalu fakticky neprobehlo.
  call :log "CHYBA: %PUB% bez USBGuardian.exe po kopii"
  exit /b 4
)

> "%PUB%\VERSION.txt" echo %COMMIT% kanal %KANAL% nastaveno %DATE% %TIME% uzivatelem %USERNAME%

echo.
echo   HOTOVO. Kanal %KANAL% = %COMMIT%
if /I "%KANAL%"=="beta" (
  echo   Rozvoz na vzorek: stanice do %ProgramData%\USBGuardian\deploy\update-beta.txt
  echo                     a spustit ulohu USBGuardian-UpdateAgentBeta.
) else (
  echo   Rozvoz na zbytek: stanice do %ProgramData%\USBGuardian\deploy\update.txt
  echo                     a spustit ulohu USBGuardian-UpdateAgent.
)
echo.
exit /b 0

:usage
echo Pouziti: Set-AgentVersion.cmd ^<commit^> [stable^|beta] [ARCHIV]
echo.
echo Verze v archivu:
for /d %%D in ("%ARCH%\*") do (
  if exist "%%D\USBGuardian.exe" echo    %%~nxD
)
exit /b 1

:log
echo %~1>> "%LOG%"
exit /b 0
