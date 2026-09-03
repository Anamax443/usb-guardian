@echo off
setlocal enabledelayedexpansion
rem ============================================================
rem Install-Agent.cmd — lokalni instalace agenta na jedne stanici.
rem
rem KDY TOHLE A NE DEPLOY Z .213:
rem   Kdyz se na stanici nedostane deploy kanal (jina lokalita, notebook
rem   na VPN, PC mimo domenu). Balicek se prekopiruje a spusti se tohle.
rem
rem PROC .cmd:
rem   Prostredi vynucuje AllSigned pres GPO. Davka mu nepodleha, takze
rem   instalace nepotrebuje podpis ani vyjimku.
rem
rem CO ZUSTANE A CO NE:
rem   ZUSTANE cilovy adresar - odtud sluzba bezi, to neni zbytek.
rem   NEZUSTANE prenesena kopie balicku (/UKLIDZDROJ) ani docasne soubory.
rem   Pri neuspechu se cilovy adresar smaze, ale JEN kdyz ho zalozila
rem   tahle davka a jeste nevznikla sluzba. Cizi obsah neni nas.
rem
rem POUZITI:
rem   Install-Agent.cmd [CILOVY_ADRESAR] [/UKLIDZDROJ] [/Q]
rem     CILOVY_ADRESAR  vychozi C:\Program Files\USBGuardian
rem     /UKLIDZDROJ     po uspesne instalaci smaze slozku, ze ktere se
rem                     davka spustila (prenesenou kopii balicku)
rem     /Q              nezastavovat na konci ("pokracujte stiskem")
rem                     POVINNE pri vzdalenem spusteni pres schtasks –
rem                     jinak by uloha pod SYSTEM cekala donekonecna
rem ============================================================

set "SRC=%~dp0"
if "%SRC:~-1%"=="\" set "SRC=%SRC:~0,-1%"
set "SVC=USB Guardian"
set "DST="
set "UKLID="
set "TICHO="
set "CREATED_DIR="

rem ── parametry ────────────────────────────────────────────────
:parse
if "%~1"=="" goto :parsed
if /I "%~1"=="/UKLIDZDROJ" ( set "UKLID=1" & shift & goto :parse )
if /I "%~1"=="/Q"          ( set "TICHO=1" & shift & goto :parse )
if not defined DST ( set "DST=%~1" & shift & goto :parse )
shift
goto :parse
:parsed
if not defined DST set "DST=C:\Program Files\USBGuardian"

echo.
echo   USB Guardian - instalace agenta
echo   zdroj: %SRC%
echo   cil:   %DST%
echo.

rem ── 1) prava ─────────────────────────────────────────────────
net session >nul 2>&1
if errorlevel 1 (
  echo CHYBA: potrebuji opravneni spravce.
  echo        Klikni pravym na tuhle davku a zvol "Spustit jako spravce".
  goto :konec_chyba
)

rem ── 2) zdroj ─────────────────────────────────────────────────
if not exist "%SRC%\USBGuardian.exe" (
  echo CHYBA: ve zdrojove slozce neni USBGuardian.exe.
  echo        Spoustis davku ze spravneho balicku?
  goto :konec_chyba
)

rem Pojistka: kdyby nekdo spustil davku primo z ciloveho adresare,
rem uklid zdroje by smazal instalaci. To se stat nesmi.
if /I "%SRC%"=="%DST%" (
  if defined UKLID (
    echo POZNAMKA: zdroj je totozny s cilem - /UKLIDZDROJ se ignoruje.
    set "UKLID="
  )
)

rem ── 3) cilovy adresar: overit, pripadne vytvorit ─────────────
rem Na cizim PC nemusi existovat vubec. Pamatujeme si, jestli jsme ho
rem zalozili my — jen takovy smime pri neuspechu zase uklidit.
if exist "%DST%\" (
  echo Cilovy adresar uz existuje - pouziji ho.
) else (
  echo Cilovy adresar neexistuje - zakladam...
  mkdir "%DST%" 2>nul
  if errorlevel 1 (
    echo CHYBA: adresar "%DST%" nejde vytvorit.
    goto :konec_chyba
  )
  set "CREATED_DIR=1"
)

rem ── 4) zastavit bezici sluzbu (pri preinstalaci) ─────────────
sc.exe query "%SVC%" >nul 2>&1
if not errorlevel 1 (
  echo Sluzba uz existuje - zastavuji kvuli vymene souboru...
  sc.exe stop "%SVC%" >nul 2>&1
  set /a T=0
  :cekej_stop
  set /a T+=1
  sc.exe query "%SVC%" 2>nul | find "STOPPED" >nul
  if not errorlevel 1 goto :je_stop
  if !T! GEQ 20 (
    echo CHYBA: sluzba se nezastavila, soubory jsou zamcene.
    goto :konec_chyba
  )
  ping -n 3 127.0.0.1 >nul
  goto :cekej_stop
  :je_stop
)

rem ── 5) kopirovani ────────────────────────────────────────────
echo Kopiruji soubory...
robocopy "%SRC%" "%DST%" /E /XF Install-Agent.cmd Uninstall-Agent.cmd agent.config.local.json /R:2 /W:2 /NFL /NDL /NJH /NP >nul
if %ERRORLEVEL% GEQ 8 (
  echo CHYBA: kopirovani selhalo ^(robocopy %ERRORLEVEL%^).
  goto :konec_chyba
)

rem ── 6) sluzba ────────────────────────────────────────────────
sc.exe query "%SVC%" >nul 2>&1
if errorlevel 1 (
  echo Vytvarim sluzbu...
  sc.exe create "%SVC%" binPath= "\"%DST%\USBGuardian.exe\"" start= auto obj= LocalSystem DisplayName= "%SVC%" >nul
  if errorlevel 1 (
    echo CHYBA: sluzbu nejde vytvorit.
    goto :konec_chyba
  )
) else (
  echo Aktualizuji nastaveni sluzby...
  sc.exe config "%SVC%" binPath= "\"%DST%\USBGuardian.exe\"" start= auto obj= LocalSystem >nul
)

rem Automaticke zotaveni po padu - nez zabere watchdog.
sc.exe failure "%SVC%" reset= 86400 actions= restart/60000/restart/60000/restart/60000 >nul

rem ── 7) watchdog task (PS-free) ───────────────────────────────
rem Akce je primo "sc start", zadny skript - nepodleha tedy AllSigned.
echo Registruji watchdog...
schtasks /Create /RU SYSTEM /RL HIGHEST /SC MINUTE /MO 3 ^
  /TN "USBGuardian\USBGuardian-Watchdog" /TR "sc start \"%SVC%\"" /F >nul 2>&1

rem ── 8) ToastHelper task (upozorneni uzivateli) ───────────────
if exist "%DST%\ToastHelper\ToastHelper.exe" (
  if exist "%SRC%\tasks\USBGuardian-ToastHelper.xml" (
    echo Registruji ToastHelper...
    set "TMPXML=%TEMP%\usbg-toast-%RANDOM%.xml"
    rem schtasks /XML vyzaduje Unicode - prekoduj.
    powershell -NonInteractive -NoProfile -Command ^
      "(Get-Content '%SRC%\tasks\USBGuardian-ToastHelper.xml' -Raw) | Set-Content '!TMPXML!' -Encoding Unicode" >nul 2>&1
    if exist "!TMPXML!" (
      schtasks /Create /XML "!TMPXML!" /TN "USBGuardian\USBGuardian-ToastHelper" /F >nul 2>&1
      rem Uklid hned - docasny soubor nema kde zustat.
      del /q "!TMPXML!" >nul 2>&1
    )
  )
)

rem ── 9) start a overeni ───────────────────────────────────────
echo Startuji sluzbu...
sc.exe start "%SVC%" >nul 2>&1
set /a T=0
:cekej_run
set /a T+=1
sc.exe query "%SVC%" 2>nul | find "RUNNING" >nul
if not errorlevel 1 goto :bezi
if !T! GEQ 15 (
  echo CHYBA: sluzba po instalaci nenabehla. Podivej se do Event Logu.
  goto :konec_chyba
)
ping -n 3 127.0.0.1 >nul
goto :cekej_run

:bezi
echo.
echo   HOTOVO - sluzba "%SVC%" bezi z "%DST%".
echo   Odinstalace: Uninstall-Agent.cmd

rem ── 10) uklid prenesene kopie ────────────────────────────────
rem Az TEDA, kdyz sluzba prokazatelne bezi. Slozku, ve ktere prave stojime,
rem nejde smazat zevnitr — pusti se oddeleny cmd, ktery chvili pocka a smaze
rem ji az potom. Instalacni adresar se nemaze, z nej sluzba bezi.
if defined UKLID (
  echo   Uklizim prenesenou kopii: %SRC%
  start "" /min cmd /c "ping -n 6 127.0.0.1 >nul & rd /s /q ""%SRC%"""
)
echo.
if not defined TICHO pause
exit /b 0

:konec_chyba
rem Uklid po sobe: cilovy adresar mazeme JEN kdyz jsme ho sami zalozili
rem a jeste nevznikla sluzba. Jinak bychom smazali neco, co nam nepatri.
if defined CREATED_DIR (
  sc.exe query "%SVC%" >nul 2>&1
  if errorlevel 1 (
    echo Uklizim po sobe - mazu "%DST%" ^(zalozili jsme ho ted^).
    rd /s /q "%DST%" 2>nul
  )
)
echo.
echo   INSTALACE NEPROBEHLA.
echo.
if not defined TICHO pause
exit /b 1
