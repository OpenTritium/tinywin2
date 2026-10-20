@echo off
rem TinyWin kiosk per-user patch. Triggered by Active Setup
rem (HKLM\SOFTWARE\Microsoft\Active Setup\Installed Components\{GUID}\StubPath),
rem which runs once per user profile at logon, before the shell starts, so the
rem taskbar and search values are in place when Explorer first reads them.
rem Do not delete this file: every new profile needs it.
reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced" /v TaskbarAl /t REG_DWORD /d 0 /f
reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced" /v ShowTaskViewButton /t REG_DWORD /d 0 /f
reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced" /v TaskbarDa /t REG_DWORD /d 0 /f
reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced" /v ShowCopilotButton /t REG_DWORD /d 0 /f
reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Search" /v SearchboxTaskbarMode /t REG_DWORD /d 0 /f
reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Search" /v SearchboxTaskbarModeCache /t REG_DWORD /d 0 /f
powershell -NoProfile -Command "$names = 'Microsoft.SecHealthUI','Microsoft.Windows.SecHealthUI'; foreach ($n in $names) { Get-AppxPackage -Name $n -ErrorAction SilentlyContinue | Remove-AppxPackage -ErrorAction SilentlyContinue }"
exit /b 0
