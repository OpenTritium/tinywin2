@echo off
rem 安装收尾钩子：setup 结束、首次登录之前以 SYSTEM 身份运行（这是唯一无需提权就能
rem 调用 AppX/DISM 在线接口的时机）。镜像离线阶段已删除载荷与 AppxAllUserStore 注册，
rem 这里补做需要在线 API 的部分，与 ionuttbara/windows-defender-remover 的
rem RemoveSecHealthApp.ps1 等价（它必须借 PowerRun 提权到 TrustedInstaller 才能跑）。
rem 日志留在 %SystemRoot%\Temp\sec-health-setup.log，便于装机后核对。
setlocal
set "LOG=%SystemRoot%\Temp\sec-health-setup.log"
echo [%DATE% %TIME%] SetupComplete: begin >> "%LOG%"
for %%P in (Microsoft.SecHealthUI_8wekyb3d8bbwe Microsoft.Windows.SecHealthUI_8wekyb3d8bbwe) do (
  dism /online /set-nonremovableapppolicy /packagefamily:%%P /nonremovable:0 >> "%LOG%" 2>&1
)
powershell -NoProfile -ExecutionPolicy Bypass -Command "Get-AppxProvisionedPackage -Online -ErrorAction SilentlyContinue | Where-Object { $_.PackageName -like '*SecHealthUI*' -or $_.DisplayName -like '*SecHealthUI*' } | ForEach-Object { Remove-AppxProvisionedPackage -Online -PackageName $_.PackageName -AllUsers -ErrorAction SilentlyContinue | Out-Null }" >> "%LOG%" 2>&1
powershell -NoProfile -ExecutionPolicy Bypass -Command "Get-AppxPackage -AllUsers -ErrorAction SilentlyContinue | Where-Object { $_.Name -like '*SecHealthUI*' } | ForEach-Object { Remove-AppxPackage -Package $_.PackageFullName -AllUsers -ErrorAction SilentlyContinue }" >> "%LOG%" 2>&1
echo [%DATE% %TIME%] SetupComplete: done >> "%LOG%"
rem 不把钩子留给第二次 setup
del /f /q "%~f0"
