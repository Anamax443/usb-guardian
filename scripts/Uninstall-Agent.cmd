@echo off
setlocal enabledelayedexpansion
rem ============================================================
rem Uninstall-Agent.cmd — uklid agenta ze stanice.
rem
rem PROC EXISTUJE:
rem   Kdyz uz nastroj na stanici nema byt, nesmi po nem zustat nic, co
rem   se tvari jako bezici software. Zapomenuty scheduled task, ktery
rem   kazde 3 minuty startuje neexistujici sluzbu, vypada v Planovaci
rem   uloh jako zavadny zbytek — a clovek, ktery si toho vsimne, ma pravdu.
rem
rem CO SMAZE:
rem   sluzbu "USB Guardian", OBA scheduled tasky (Watchdog i ToastHelper),
rem   prazdnou slozku uloh \USBGuardian\ a instalacni adresar.
rem
rem CO NECHA:
rem   C:\ProgramData\USBGuardian — fronta neodeslanych incidentu a whitelist.
rem   To je lokalni auditni stopa; smaze se jen s parametrem /DATA.
rem
rem POUZITI:  Uninstall-Agent.cmd [CILOVY_ADRESAR] [/DATA]
rem ============================================================

set "DST=%~1"
if "%DST%"=="" set "DST=C:\Program Files\USBGuardian"
if /I "%DST%"=="/DATA" (
  set "DST=C:\Program Files\USBGuardian"
  set "SMAZAT_DATA=1"
)
if /I "%~2"=="/DATA" set "SMAZAT_DATA=1"
set "SVC=USB Guardian"
set "DATA=%ProgramData%\USBGuardian"

echo.
echo   USB Guardian - odinstalace agenta
echo   adresar: %DST%
echo.

net session >nul 2>&1
if errorlevel 1 (
  echo CHYBA: potrebuji opravneni spravce.
  echo        Klikni pravym a zvol "Spustit jako spravce".
  pause
  exit /b 1
)

rem ── 1) sluzba ────────────────────────────────────────────────
sc.exe query "%SVC%" >nul 2>&1
if errorlevel 1 (
  echo Sluzba neexistuje - preskakuji.
) else (
  echo Zastavuji a mazu sluzbu...
  sc.exe stop "%SVC%" >nul 2>&1
  set /a T=0
  :cekej
  set /a T+=1
  sc.exe query "%SVC%" 2>nul | find "STOPPED" >nul
  if not errorlevel 1 goto :stoplo
  if !T! GEQ 20 goto :stoplo
  ping -n 3 127.0.0.1 >nul
  goto :cekej
  :stoplo
  sc.exe delete "%SVC%" >nul 2>&1
)

rem ── 2) OBA tasky ─────────────────────────────────────────────
rem Watchdog i ToastHelper. Drive se rusil jen watchdog a ToastHelper
rem na stanici zustaval — po smazani souboru pak pri kazdem prihlaseni
rem selhaval a bylo to videt v Planovaci uloh.
echo Rusim scheduled tasky...
schtasks /Delete /TN "USBGuardian\USBGuardian-Watchdog"    /F >nul 2>&1
schtasks /Delete /TN "USBGuardian\USBGuardian-ToastHelper" /F >nul 2>&1

rem Prazdna slozka uloh \USBGuardian\ by zustala viset taky.
schtasks /Delete /TN "USBGuardian\" /F >nul 2>&1

rem ── 3) instalacni adresar ────────────────────────────────────
if exist "%DST%\" (
  echo Mazu %DST% ...
  rd /s /q "%DST%" 2>nul
  if exist "%DST%\" (
    echo POZOR: adresar se nepodarilo smazat cely - nekdo v nem ma otevreny soubor.
  )
) else (
  echo Instalacni adresar neexistuje - preskakuji.
)

rem ── 4) data (jen na vyzadani) ────────────────────────────────
if defined SMAZAT_DATA (
  if exist "%DATA%\" (
    echo Mazu %DATA% ^(fronta a whitelist^)...
    rd /s /q "%DATA%" 2>nul
  )
) else (
  if exist "%DATA%\" (
    echo Ponechavam %DATA% - fronta neodeslanych incidentu a whitelist.
    echo   Smazat lze parametrem /DATA.
  )
)

echo.
echo   HOTOVO.
echo.
pause
exit /b 0
