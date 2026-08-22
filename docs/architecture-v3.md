# TinyWin2 Architecture v3

TinyWin2 将每个精简动作建模为一个原子 plan，并在 VHDX 差分链中逐层执行。一个 plan 只有一个
`operation`，一个 `PlanStep` 只有一个 plan，一个成功的 step 对应一个可恢复的 VHDX 层。

## Plan Schema

```json
{
  "schemaVersion": 3,
  "id": "service.font-cache",
  "version": "3.0.0",
  "title": "禁用 Windows 字体缓存服务",
  "description": "将 FontCache 配置为禁用。",
  "category": "Language",
  "riskLevel": "Medium",
  "requires": ["feature.extra-fonts"],
  "conflicts": [],
  "parameters": [
    {
      "name": "startMode",
      "type": "enum",
      "label": "启动方式",
      "default": "disabled",
      "options": [
        { "value": "disabled", "label": "禁用", "riskLevel": "Medium" }
      ]
    }
  ],
  "operation": {
    "resource": "registry.service",
    "action": "configure",
    "spec": {
      "services": ["FontCache"],
      "start": {
        "$map": {
          "parameter": "startMode",
          "cases": { "disabled": "disabled" }
        }
      }
    }
  }
}
```

字段职责如下：

| 字段 | 作用 |
| --- | --- |
| `schemaVersion` | JSON 结构版本。当前且唯一支持的 plan 版本为 3。 |
| `id` | 稳定的 plan 标识，也是 profile、依赖、冲突和层 checkpoint 使用的键。 |
| `version` | plan 行为版本。内容或 operation 改变时递增，用于恢复指纹。 |
| `title` / `description` | UI、CLI 和日志中的用户可读信息。 |
| `category` | 展示分类；只影响 UI 过滤和排序，不参与层合并或执行顺序。 |
| `riskLevel` | 整个 plan 的风险级别：`Low`、`Medium`、`High`。 |
| `requires` | 可选的依赖 plan id 数组；依赖按拓扑顺序展开。 |
| `conflicts` | 可选的冲突 plan id 数组；同一构建中不能同时启用。 |
| `parameters` | 可选的用户输入声明，负责类型、默认值和 enum 选项校验。 |
| `operation` | 唯一的原子动作，包含资源、动作类型和资源专属 payload。 |
| `operation.resource` | 执行器注册的资源 id，例如 `registry.service`、`dism.feature`。 |
| `operation.action` | 显式语义：`configure`、`set`、`remove`、`cleanup`、`copy`。 |
| `operation.spec` | 已绑定前的资源参数。解析时替换 `$parameter` / `$map`，执行器随后强类型校验。 |

`category` 不再决定 VHDX 层粒度；`parameters` 也不承载执行逻辑。这样 plan 的元数据、用户输入
和实际动作边界清晰，执行器不需要猜测隐式执行语义。

## Actions

| Resource | Action | 语义 |
| --- | --- | --- |
| `registry.service` | `configure` | 设置离线服务启动模式和触发器。 |
| `registry.value` | `set` / `remove` | 写入、修改或删除注册表值/键。 |
| `dism.feature`, `dism.capability`, `dism.package` | `remove` | 从离线镜像移除 DISM 组件。 |
| `dism.component-store` | `cleanup` | 执行组件存储清理，可选 `resetBase`。 |
| `appx.provisioned` | `remove` | 移除预配 AppX。 |
| `driver.store` | `remove` | 移除可安全识别的第三方驱动包；Inbox 驱动会跳过。 |
| `fs.path` | `remove` / `copy` | 删除镜像路径或从 plan assets 复制文件树。 |

所有执行器都实现 `Validate`、`InspectAsync`、`ApplyAsync`。Apply 先读取状态，满足目标时返回
`Skipped`；硬失败抛出 `ExecException`。运行时只接收已解析的 `OperationSpec`，不会重新解释 JSON DSL。

## Pipeline

1. 解析 catalog、展开 `requires`、拒绝 `conflicts` 并绑定 parameters。
2. 将源镜像应用到 `base.vhdx`。
3. 每个 plan 创建一个差分层，挂载后执行唯一 operation；成功则提交，失败则删除该层。
4. 最终叶子层独立执行 CBS `ScanHealth`。
5. 输出为 WIM、ESD 或合并后的 VHDX；WIM/ESD 可在最后独立打包为 ISO。

`--no-layers` 只改变存储方式，不改变 plan 的执行顺序。`--fast` 跳过每层 CheckHealth，但不跳过
最终 CBS 扫描。

层 manifest 只记录 `operationResult`、指纹、状态和错误。旧 manifest 中的 `boundArgs` / `execResults`
字段会被忽略，新 manifest 不再写入这些 bundle 时代字段。

## Profiles

Profile 使用 schema 3：

```json
{
  "schemaVersion": 3,
  "name": "standard-safe",
  "description": "常规安全项",
  "selections": [
    {
      "planId": "service.font-cache",
      "enabled": true,
      "parameters": { "startMode": "disabled" }
    }
  ]
}
```

Profile 只保存选择和 parameter 值，不复制 plan 定义。`standard-safe` 是仓库提供的显式选择集合，
不依赖 plan 内的隐式分类。`enabled: false` 表示明确禁用该 plan，
可阻止它作为其他 plan 的依赖被自动拉入；`--plan` 或 GUI 重新选择可以覆盖这一状态。导入时会按当前
catalog 检查未知 id，重复选择和未知字段会直接报错。

Plans 使用 schema 3；parser 严格拒绝其他 schema、未知字段、重复依赖/冲突/参数和畸形 profile
选择，避免旧格式或拼写错误被静默按新语义解释。

## Layout

```text
src/TinyWin2.Core       engine, plans, executers, layers, pipeline
src/TinyWin2.Cli        CLI
src/TinyWin2.Gui        WinUI frontend
plans/                  schema v3 atomic plans
profiles/               profile schema 3
tests/                  TUnit tests
out/                    build artifacts and layer workspaces
```
