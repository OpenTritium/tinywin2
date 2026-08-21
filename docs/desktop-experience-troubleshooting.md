# Desktop Experience 版排查经验（Server 2025 / Hyper-V）

> 来源：桌面版精简镜像在 Hyper-V Gen2 VM 上的完整排查实录（分层构建 → 首登 → 交互登录 → 桌面稳定性）。
> 适用于 Server 2025 Desktop Experience 镜像在 Hyper-V 上的部署与精简验证。

## 一、首登卡"请等候本地会话管理器"

**症状**：全新部署后首次登录无限转圈"请等候本地会话管理器"，`logonui.exe` 占满 CPU。

**根因**：`TextInputManagementService`（TSF 文本输入管理服务）被禁用。LogonUI 密码框的输入栈（TSF）初始化时服务不可用 → 死循环。

**判定线索**：卡住时 guest 内 `Get-Process logonui` CPU 持续增长；`quser` 无会话；CBS/TiWorker 早已结束（TiWorker 扫描约 5 分钟即完成，**不是**卡死原因）。

**修复**：桌面版保留 TSF（`service.text-input` plan 仅限 Core）。`AutoAdminLogon` 走不同路径，会掩盖此问题（当时误判过），**必须用全新 OOBE 的 VM 验证**。

## 二、桌面"闪烁"＝ explorer 崩溃循环

**症状**：进桌面后屏幕"一闪一闪"，任务栏反复消失重现。

**根因**：explorer/ShellHost 崩溃循环，`ucrtbase.dll` 异常 `0xc0000409`（`__fastfail(7)` FAST_FAIL_INVALID_ARG）。

**最终定位（干净 VM 二分）**：`camsvc`（Connected Devices Platform 功能访问管理器）禁用 → 右键桌面"显示设置"（`ms-settings:display` URI 激活）失败，弹"找不到打开方式"，随后 explorer 进入崩溃循环。

**为什么难查**：
- 闪烁是**交互触发**的（要点右键显示设置才发作），不是开机即现 → 反复出现"同状态不同结果"。
- 同一 VM 多次重启会被一次性初始化（AppX 注册队列、runonce、WER 状态）污染，实验不可比。
- AppX 注册风暴（`0x80070422`，62% CPU）与崩溃**同窗并发**，容易误判为因果（实际无关——风暴只是日志噪音，对使用无感）。

**修复**：`camsvc` 从 `service.privacy-broker` 移除（`8ac4900`）。NcbService 单独禁用经验证安全。

## 三、方法论教训

1. **干净环境二分**：每轮实验用全新 VM（全新 OOBE），禁止复用已初始化 VM——oneshot 初始化状态会污染结果。
2. **冷启动 vs 热重启**：`Stop-VM/Start-VM`（冷）与 guest 内 `shutdown /r`（热）不等价，跨轮次混用会引入变量。
3. **并行二分**：16GB 宿主最多同时 2~3 台 4GB VM；dism apply 有全局锁，**apply 必须串行**（并行报 error 21 / 0x800705AA）；盘符要参数化（每台独立 EFI/OS 盘符，否则 New-Partition 冲突）。
4. **自动判定优于人眼**：explorer 崩溃计数（Application 日志 1000/1002）是客观指标；人眼观察受时机影响。
5. **热二分可行但看窗口**：崩溃循环有 shell 重启退避（指数拉长），短窗口（12s）测不到，需重启对照或长窗口。
6. **先读日志再猜**：WER Report.wer 给出异常码（0xc0000409 + 异常数据 7 = fastfail INVALID_ARG）比盲试服务快得多。

## 四、其他已确认问题

| 问题 | 根因 | 修复 |
|---|---|---|
| 构建产物无法引导 | `make-vm.ps1` 传正斜杠 WIM 路径 → dism apply 静默失败（`0x80070057` 扩展名错误 / exit 87） | 反斜杠路径 + `$LASTEXITCODE` 检查 |
| 构建 3 组步骤失败 | regini 脚本拼接了 `HKLM\` 前缀 → `\Registry\Machine\HKLM\...` 无效（exit 1 / error 87） | `cb639b2` 剥离前缀 |
| 桌面闪烁（内存侧） | 2GB VM 被 Hyper-V 动态显存抢占 ~1.25GB → 系统剩 ~800MB → 换页风暴 | VM ≥ 4GB |
| RDP 三件套禁用 | 曾怀疑卡登录——实证**无罪**（当时是 TSF 卡死 + AutoAdminLogon 掩盖），plan 已保守改 manual | `bf5a2b6` |
| TiWorker 首登扫描 | DISM 删除组件后首次启动 CBS 对删除包做 DetectUpdate（~5 分钟），无害背景噪音 | 无需处理 |
| AppX 注册风暴 | 后半组服务禁用 → 注册失败重试（CPU 62%、错误日志刷屏），重试耗尽/服务恢复即静默 | 日志噪音，无实害 |

## 五、桌面版 vs Core 版配方差异

- **桌面版必须保留**：`TextInputManagementService`（TSF）、`camsvc`（AppX/ms-settings 激活）；VM ≥ 4GB。
- **桌面版可禁用**（已验证）：edgeupdate/edgeupdatem、音频（Audiosrv/AudioEndpointBuilder）、蓝牙/生物（BTAGService/bthserv/BluetoothUserService/DeviceAssociationService/WbioSrvc）、MSDTC、tapisrv、NcbService。
- **服务禁用类 plan 只在 Core 版放开**；桌面版 profile 排除含 camsvc/TSF 的 plan（`extreme-desktop-profile.json`，173→152）。
- 首次部署后前 1~2 次登录可能有初始化窗口（TiWorker/AppX），之后永久稳定——可接受，或构建后做"预初始化"（自动登录跑 2 次再捕获 WIM）。

## 六、Hyper-V 冒烟工具（F:\hyperv-smoke2）

- `make-vm.ps1`：Gen2 建盘 + apply + bcdboot + unattend（含 `Domain .` 修复与反斜杠路径修复）。
- `smoke-unattend.xml`：AutoLogon（`<Domain>.</Domain>` 必须）、FirstLogonCommands 采集 + 90s 自动关机。
- `bisect-set.ps1`：离线批量设置服务 Start 值（-Mode disable/restore -Csv ... -VmName ...）。
- `clean-round.ps1`：干净环境二分——每轮全新 VM（独立盘符、串行 apply、TSF 强制保留 + AutoAdminLogon），首登自动观测。
- PS Direct 前置：`Enable-VMIntegrationService -Name "Guest Service Interface"`；凭据 `Administrator / TinyWin2Audit!2026`。
