@echo off
rem MeasureFont.bat - measure the PageHead strings in each candidate UI font.
rem Answers: does the choice of font change the sizes the layout snapshot stores?
setlocal
cd /d "%~dp0.."
set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" echo ERROR: C# compiler not found. & exit /b 1

set OUT=%TEMP%\measurefont.exe
%CSC% /nologo /target:exe /platform:anycpu /codepage:65001 ^
  /r:System.Drawing.dll /out:"%OUT%" tools\MeasureFont.cs
if errorlevel 1 echo BUILD FAILED & exit /b 1

"%OUT%"
