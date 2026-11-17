@echo off
rem Baut MonitorSwitch.exe mit dem in Windows enthaltenen C#-Compiler (.NET Framework 4.x).
setlocal
cd /d "%~dp0"

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo csc.exe nicht gefunden - ist .NET Framework 4.x installiert?
  pause & exit /b 1
)

"%CSC%" /nologo /target:winexe /optimize+ /platform:anycpu /out:MonitorSwitch.exe ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:Microsoft.VisualBasic.dll MonitorSwitch.cs

if errorlevel 1 (
  echo.
  echo Build fehlgeschlagen.
  pause & exit /b 1
)

echo.
echo MonitorSwitch.exe erstellt.
pause
