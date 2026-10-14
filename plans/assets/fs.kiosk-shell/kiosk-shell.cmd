@echo off
rem TinyWin2 kiosk shell: keep THS running instead of explorer.
rem Launched as: cmd.exe /k C:\Windows\System32\kiosk-shell.cmd
rem Falls back to explorer.exe when the configured THS path is missing,
rem so an image without THS installed still boots to a usable desktop.
rem The watchdog matches the configured exe name, so a renamed main binary
rem still works (no hardcoded hexin.exe).
setlocal enableextensions
set "THS="
for /f "tokens=2,*" %%a in ('reg query "HKLM\SOFTWARE\TinyWin2" /v ThsPath 2^>nul ^| findstr /i ThsPath') do set "THS=%%b"
if not defined THS goto shell
if not exist "%THS%" goto shell
for %%f in ("%THS%") do set "IMG=%%~nxf"
:run
start "" "%THS%"
ping -n 46 127.0.0.1 >nul
:watch
tasklist /fi "imagename eq %IMG%" /nh 2>nul | find /i "%IMG%" >nul || goto run
ping -n 21 127.0.0.1 >nul
goto watch
:shell
start "" explorer.exe
:hold
ping -n 3601 127.0.0.1 >nul
goto hold
