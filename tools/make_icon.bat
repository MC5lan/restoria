@echo off
rem make_icon.bat - build assets\app.ico from tools\IconGen.cs
rem The SVG design source is assets\icon.svg; this generator redraws the same geometry with GDI+.
setlocal
cd /d "%~dp0.."
set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe
%CSC% /noconfig /nostdlib+ /nologo /target:exe /platform:anycpu /optimize+ /codepage:65001 /r:"C:\Windows\Microsoft.NET\Framework64\v2.0.50727\mscorlib.dll" /r:"C:\Windows\Microsoft.NET\Framework64\v2.0.50727\System.dll" /r:"C:\Windows\Microsoft.NET\Framework64\v2.0.50727\System.Drawing.dll" /out:"tools\icongen.exe" tools\IconGen.cs
if errorlevel 1 echo BUILD FAILED & pause & exit /b 1
tools\icongen.exe "assets\app.ico"
if errorlevel 1 echo ICON FAILED & pause & exit /b 1
echo OK
pause
