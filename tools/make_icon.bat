@echo off
rem make_icon.bat - build assets\app.ico from tools\IconGen.cs
rem The design source is tools\IconGen.cs (Restoria letter mark: ink-blue rounded
rem square + Palatino Bold serif R + cinnabar patch). Redrawn with GDI+.
setlocal
cd /d "%~dp0.."
set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" echo ERROR: C# compiler not found. Install .NET Framework 4.x first. & exit /b 1
%CSC% /noconfig /nostdlib+ /nologo /target:exe /platform:anycpu /optimize+ /codepage:65001 /r:"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\mscorlib.dll" /r:"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\System.dll" /r:"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\System.Drawing.dll" /out:"tools\icongen.exe" tools\IconGen.cs
if errorlevel 1 echo BUILD FAILED & exit /b 1
tools\icongen.exe "assets\app.ico" "assets\icon-preview.png"
if errorlevel 1 echo ICON FAILED & exit /b 1
echo OK
