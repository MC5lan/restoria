@echo off
rem ============================================================
rem  PicTool setup bootstrap. Keep this file PURE ASCII:
rem  cmd on a zh-CN system reads .bat as GBK, so any UTF-8 Chinese
rem  here would show up as garbage. All Chinese text lives in
rem  setup-gui.exe and install.ps1.
rem
rem  Job: pick the right installer and WAIT for it.
rem    CLR4 present -> graphical installer (setup-gui.exe)
rem    CLR4 missing -> console installer (install.ps1), which can
rem                    still explain how to get .NET Framework 4.8
rem
rem  Note: we must not exit early. The self-extracting archive wipes
rem  its temp folder as soon as this script returns, and the
rem  installer reads its files from that folder.
rem ============================================================
setlocal
cd /d "%~dp0"

set LOG=%TEMP%\PicTool-install.log
>>"%LOG%" echo bootstrap: start, cwd=%CD%

set GUI=%~dp0setup-gui.exe
if not exist "%GUI%" goto TEXT
if exist "%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\clr.dll" goto GUI
if exist "%SystemRoot%\Microsoft.NET\Framework\v4.0.30319\clr.dll" goto GUI
>>"%LOG%" echo bootstrap: no CLR4 - falling back to the console installer
goto TEXT

:GUI
>>"%LOG%" echo bootstrap: launching graphical installer
start /wait "" "%GUI%"
set RC=%ERRORLEVEL%
>>"%LOG%" echo bootstrap: setup-gui exit=%RC%
exit /b 0

:TEXT
>>"%LOG%" echo bootstrap: running install.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1"
set RC=%ERRORLEVEL%
>>"%LOG%" echo bootstrap: install.ps1 exit=%RC%

echo.
if not "%RC%"=="0" goto FAILED
echo   Setup finished. If the report above says the install succeeded,
echo   a shortcut was placed on the desktop.
goto END

:FAILED
echo   Setup did NOT finish (exit=%RC%).
echo   Please send this log file to the maintainer:
echo     %LOG%

:END
echo.
echo   Press any key to close this window...
pause >nul
exit /b %RC%
