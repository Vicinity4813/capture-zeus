@echo off
rem Builds "dist\Capture Zeus.exe" with the C# compiler that ships with Windows (.NET Framework 4).
rem Compile "dist\Capture Zeus.exe" avec le compilateur C# fourni avec Windows (.NET Framework 4).
setlocal
cd /d "%~dp0"
set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
    echo .NET Framework 4 compiler not found: %CSC%
    exit /b 1
)
if not exist dist mkdir dist
"%CSC%" /nologo /target:winexe /platform:x86 /optimize+ /codepage:65001 ^
    /win32icon:src\capture-zeus.ico /win32manifest:src\app.manifest ^
    "/out:dist\Capture Zeus.exe" /r:System.Drawing.dll /r:System.Windows.Forms.dll ^
    src\*.cs
if errorlevel 1 exit /b 1
echo Built: dist\Capture Zeus.exe
