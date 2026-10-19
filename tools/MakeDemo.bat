@echo off
rem MakeDemo.bat - build and run the README before/after image generator.
rem Everything it produces is derived from _probe\doc_test.png, which the
rem program generates itself via --make-fixture. No real archive material is
rem read or written anywhere in this process.
setlocal
cd /d "%~dp0.."
set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" echo ERROR: C# compiler not found. & exit /b 1

set OUT=%TEMP%\makedemo.exe
%CSC% /nologo /target:exe /platform:anycpu /optimize+ /r:System.Drawing.dll /out:"%OUT%" tools\MakeDemo.cs
if errorlevel 1 echo BUILD FAILED & exit /b 1

rem The fixture has to exist before the images can be generated.
if not exist "_probe\doc_test.png" (
  echo fixture missing, generating it first...
  set PICTOOL_NO_PAUSE=1
  call build.bat >nul 2>&1
  if exist "dist\Restoria.exe" (
    start /wait "" "dist\Restoria.exe" --make-fixture "_probe\doc_test.png"
  )
)

"%OUT%"
echo.
echo done. images are in docs\images\
