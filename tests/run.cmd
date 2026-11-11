@echo off
rem Tests courts de Capture Zeus (mise a l'echelle de l'image du jeu).
rem   run.cmd            -> VerifEchelle seul (hors ecran, quelques secondes)
rem   run.cmd faux       -> VerifEchelle puis le faux jeu dans les 4 modes (1, aspect, etire, curseur)
rem   run.cmd faux etire -> VerifEchelle puis le faux jeu dans un seul mode
rem Le faux jeu prend la souris et le clavier environ 30 s par mode : ne touchez a rien pendant ce temps.
rem Resultats : bin\resultat_<mode>.txt, journal de la capture : bin\journal_<mode>.txt
setlocal
cd /d "%~dp0"
set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
call ..\build.cmd >nul || (echo Echec de la compilation de Capture Zeus & exit /b 1)
if not exist bin mkdir bin
copy /y "..\dist\Capture Zeus.exe" bin\ >nul
"%CSC%" /nologo /target:exe /platform:x86 /optimize+ /codepage:65001 /out:bin\VerifEchelle.exe /r:System.Drawing.dll VerifEchelle.cs ..\src\ZeusCapture.cs || exit /b 1
"%CSC%" /nologo /target:winexe /platform:x86 /optimize+ /codepage:65001 /out:bin\ZeusFaux.exe /r:System.Drawing.dll /r:System.Windows.Forms.dll ZeusFaux.cs || exit /b 1
bin\VerifEchelle.exe || exit /b 1
if /i not "%~1"=="faux" exit /b 0
set "MODES=1 aspect etire curseur"
if not "%~2"=="" set "MODES=%~2"
set FAILED=0
for %%m in (%MODES%) do (
    if exist bin\resultat_%%m.txt del bin\resultat_%%m.txt
    start "" /wait bin\ZeusFaux.exe %%m "bin\Capture Zeus.exe"
    if exist bin\resultat_%%m.txt (type bin\resultat_%%m.txt) else (echo ECHEC  mode %%m : pas de resultat)
    findstr /b "OK" bin\resultat_%%m.txt >nul 2>&1 || set FAILED=1
)
if %FAILED%==1 (echo Faux jeu : au moins un echec. & exit /b 1)
echo Faux jeu : tout est bon.
