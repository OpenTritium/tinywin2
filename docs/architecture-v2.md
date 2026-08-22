# TinyWin2 architecture (v2)

TinyWin2 是 TinyWin 的第二代：把每一次精简动作建模为**期望状态的收敛**（ensure 语义），
在 **VHDX 差分链**上逐层执行，每层原子提交/丢弃，从而可以精确回答
**"哪一层 plan 精简过度导致了功能异常"**。

```text
TinyWin2.Gui (WinUI 3 四页向导, unpackaged, requireAdministrator)
        |
        | spawn + JSONL 事件流 (--json-events)         ← 与 CI 同一通道
TinyWin2.Cli (tinywin2: build / inspect / preview / plan / profile / layer / doctor)
        |
TinyWin2.Core
   Plans      目录加载校验 → arg 绑定($arg/$map) → BuildPlanResolver（依赖/冲突/分组）
   Executers  9 类资源执行器（Inspect 读态 / Apply 收敛，增改删统一为 ensure）
   Layers     VhdLayerStack（base.vhdx + L00n.vhdx 差分链 + layers.json）
   Pipeline   BuildEngine 编排 / SourceImageResolver / OutputBuilder / PreviewRunner / LayerInspector
   Profiles   Profile 导入导出（plan + 参数集合）
        |
   dism.exe /English · reg.exe · diskpart · robocopy · oscdimg（全部进程调用，/English 固定输出键）
```

## 核心模型

### 1. Executer（需求 1、7）

一个 exec 是纯数据声明：资源 + 期望状态 + 参数。执行器按资源类型注册：

```csharp
record ExecSpec(string Resource, Ensure Ensure, JsonObject Desired);   // Ensure = Present | Absent
interface IExecuter {
    Task<ResourceDiff> InspectAsync(ctx, spec, ct);   // 读当前态 → 差异清单（幂等/预览）
    Task<ExecResult>  ApplyAsync(ctx, spec, ct);      // 收敛：增/改/删统一，返回 Changes[]
}
```

- `ensure: present` = 添加或修改；`ensure: absent` = 移除。**不是"删除器"而是收敛器**。
- 幂等免费：Apply 先 Inspect，已满足 → Skipped。
- 语义级变更日志 `Changes[]`（Created/Modified/Removed + before/after）汇入层记录。

资源集合（v1 对等 + 统一化）：`registry.value`（增改删值/删键）、`registry.service`
（统一了 v1 Disable/Configure 两个 handler：start ∈ auto|delayedAuto|manual|disabled）、
`dism.feature`、`dism.capability`、`dism.package`、`dism.component-store`（4350 降级跳过）、
`appx.provisioned`、`driver.store`（通过 DISM 按发布名移除第三方驱动，inbox 驱动明确跳过）、`fs.path`（absent 删除/防穿越，
present 从 plan assets 复制入镜像）。预留：`dism.driver` / `dism.update` / `image.unattend`。

### 2. Plan 与组合（需求 2、5、7）

- 叶子 plan = `execs[]`；组合 = 构建时按 group 折叠成 `PlanStep`（原子单位 = 一层）。
- `arguments[]` 声明 enum/int/bool/string 参数（含选项级风险），exec 的 `with` 通过
  `$arg`（引用）与 `$map`（映射）在**解析期**绑定成纯数据——运行期零魔法。
- 依赖递归展开（环检测）、冲突拒绝、显式禁用依赖报错。
- `--granularity group|plan`：默认每大类一层（10~16 层），可切换每 plan 一层（最细归因）。

### 3. VHDX 差分链（需求 3）

```
base.vhdx ← L001.vhdx ← L002.vhdx …        （diskpart create vdisk parent=）
```

- 每个 PlanStep 开新差分层 → attach → 执行 execs → （CheckHealth）→ detach = Commit（保留）。
- 任一失败 → detach + **删除该层 VHDX** = 镜像状态与该步骤前完全一致（原子性）。
- 失败构建**保留层链**供取证；>30 层自动 merge 合并最旧层（链深保护）。
- **证据快照（evidence snapshots）**：每层 commit 前（层仍挂载时）固化该层完整状态：
  `snapshots/L00N.files.tsv`（全文件清单，jump-safe，过滤离线 hive 事务噪音）+
  `snapshots/L00N.registry.reg`（五大 hive 的 reg export 语义文本）。回溯不依赖差分链重挂
  （某些 Windows 构建在构建结束后拒绝重挂差分链——0xC03A000E），且快照比挂载对比更快、不污染层。
- 回溯工具链：
  - `layer list` — 每层 plan/参数/状态/大小/耗时/语义变更摘要
  - `layer diff N-1 N` — 对比两份证据快照：文件增删改 + 注册表语义 diff
    （例：`software\...\EnableLUA  dword:1 → dword:0`、`system\...\LanmanWorkstation\Start dword:2 → dword:4`）
  - `layer extract` — 挂载指定层提取单个文件
  - `layer rollback-to N` — 从第 N 层捕获输出（挂载层；在链重挂受限的构建上不可用时以重放构建代替）
  - `preview` — 基础层上全量 Inspect → 影响预览报告（不执行任何 plan）

默认保留每层文件与注册表证据；只追求最快导出时可使用 `--no-evidence` 显式关闭。

### 4. 输出（需求 4）

所有 plan 完成后，最终叶子层先独立执行一次离线 CBS `dism /Cleanup-Image /ScanHealth`；扫描
失败则停止封装并保留工作区。输出格式与成品打包是两个独立选项：`--out wim|esd|vhdx` 分别生成安装镜像或整链合并后的
单文件 VHDX；WIM/ESD 媒体可再用 `--iso` 交给 oscdimg 打包成双 BIOS+UEFI 可引导 ISO
（`-bootdata:2#p0,e,b etfsboot.com#pEF,e,b efisys[_noprompt].bin`）。
默认仍为 ESD 媒体 + ISO；VHDX 不会额外捕获安装镜像，且不能与 ISO 打包同时请求。
已知取舍：capture 不保留源 WIM 的 FLAGS 元数据（名称/描述保留），端到端验证 setup 可识别安装。

### 5. Profile（需求 8）

```json
{ "schemaVersion": 1, "name": "...", "selections": [ { "planId": "service.workstation", "enabled": true, "args": { "startMode": "manual" } } ] }
```
CLI `profile export|import`；GUI 第 2 页导入/导出；内置 `profiles/standard-safe.json`
（v1 Standard 档等价，83 项）。

### 6. CLI 与 GUI（需求 6、9）

CLI 与 GUI 共享 Core；GUI 一律 spawn CLI（`--json-events` 换行分隔 JSON 事件流），
保证双端行为一致。事件含 phase（prepare/media/base-layer/plan/cbs-scan/capture/package/done）、
planId、layerIndex、progress、message。四页向导：源与输出 → 精简项目（分组折叠/搜索/
tier 过滤/enum 参数下拉含风险徽章/Profile）→ 进度日志（彩色流+进度条+取消）→
结果（产物清单 or 失败层 + `layer diff` 取证命令提示）。

## 工程约定

- **代码风格**：K&R（`csharp_new_line_before_open_brace = none`）+ 120 列 + LF，见 `.editorconfig`；
  `dotnet format` 格式化/校验，`EnforceCodeStyleInBuild` 构建期强制（风格违规 = 编译错误）。

- **dism 一律 `/English`**：输出键名与宿主显示语言解耦（中文系统上"索引:"坑的根治）。
- **VHD 后端优先 Hyper-V cmdlet**（New-VHD/Mount-VHD/Merge-VHD，即 Hyper-V 检查点同款机制），
  diskpart 作为零依赖后备；所有 diskpart 路径规范化（拒正斜杠）、attach 幂等自愈（残留挂载先 detach）。
- 错误分类用 HRESULT/退出码（CBS_E_INVALID_INSTALL_STATE = -2146498541 等），
  弃用 v1 的中英文正则；zh-CN 提示仅作 provider 缺失的兜底。
- 一切 JSON 输出 CJK 友好（UnsafeRelaxedJsonEscaping）。
- **日志 = 双通道**：`BuildEvent`（强类型领域事件，承载 `--json-events` JSONL 进程协议与 manifest）+
  **Serilog**（标准输出通道：彩色控制台 + `out/logs/tinywin2-*.log` UTF-8 文件，phase/planId/layerIndex
  作为结构化属性；`--json-events` 模式下自动关闭控制台 sink 避免污染 JSONL 流）。GUI 场景日志文件同样生成。
- 测试：TUnit，214 个单元/收敛语义/层栈状态机/迁移 golden 测试；
  `TINYWIN2_IT=1` 门控真实 diskpart 集成测试。
- plan identity、缓存/恢复指纹，以及最终 WIM/ESD/VHDX/ISO 和 manifest
  完整性记录统一使用带版本前缀的 `xxh3-v1`。
- v1 → v2 迁移：`tools/migrate-v1`（156 条 → 152 plan；disable/configure 对合并为
  startMode 枚举参数；old→new 映射表见 `migration-report.json`）。

## 目录

```
src/TinyWin2.Core        引擎（无 UI 依赖）
src/TinyWin2.Cli         tinywin2 命令行
src/TinyWin2.Gui         WinUI 3 四页向导
tools/migrate-v1         v1 条目迁移器
plans/                   152 个 v2 plan（迁移产物）+ assets/<planId>/（fs.path present 源）
profiles/                内置 profile
schemas/                 （规划）plan schema
tests/                   TUnit 测试
out/                     构建输出（work/<id>/ 为层链工作区）
```
