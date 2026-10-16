@echo off
rem 首个用户首次登录补丁：任务栏五项（以当前用户身份写 HKCU）+ 只清当前用户的
rem SecHealthUI 注册。注意：RunOnce 以当前用户身份运行且不带提权，`-AllUsers`
rem 会因权限被拒而静默失败（旧版即如此），所以这里只做用户级移除；
rem 机器级移除由镜像离线步骤 + Windows\Setup\Scripts\SetupComplete.cmd（SYSTEM）完成。
reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced" /v TaskbarAl /t REG_DWORD /d 0 /f
reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced" /v ShowTaskViewButton /t REG_DWORD /d 0 /f
reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced" /v TaskbarDa /t REG_DWORD /d 0 /f
reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced" /v ShowCopilotButton /t REG_DWORD /d 0 /f
reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Search" /v SearchboxTaskbarMode /t REG_DWORD /d 0 /f
powershell -NoProfile -Command "$names = 'Microsoft.SecHealthUI','Microsoft.Windows.SecHealthUI'; foreach ($n in 'Microsoft.SecHealthUI','Microsoft.Windows.SecHealthUI') { Get-AppxPackage -Name $n -ErrorAction SilentlyContinue | Remove-AppxPackage -ErrorAction SilentlyContinue }"
rem 任务栏先于 RunOnce 启动，注册必须伴随一次 explorer 重启才生效
taskkill /f /im explorer.exe >nul 2>&1
start explorer.exe
del "%~f0"
