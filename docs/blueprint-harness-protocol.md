# GAssetForge 蓝图 Harness 协议（v1）

蓝图模块把“编辑中的图”“冻结执行计划”“运行状态”分开保存，避免 Agent 会话、AvalonDock 页面状态或正在修改的节点成为执行真相源。

## 项目文件

- 图定义：`ProjectSettings/Blueprints/<workflowKey>.gafgraph.json`
- 图备份：同名 `.bak`
- 运行目录：`Assets/AI/Blueprint/<runId>/`
- 冻结计划：`execution-plan.v1.json`
- 运行状态：`run-state.v1.json`
- 时间线：`timeline.v1.jsonl`

无正式项目时，图和运行记录分别落在应用 Workspace 下的 `BlueprintDrafts` 与 `BlueprintRuns`，不会写入任意默认图片目录。

图文件必须显式包含 `schemaVersion`，枚举只接受协议中的字符串名称，不接受整数枚举。主文件与备份的单文件上限均为 8 MiB；超限、项目路径无效、目录逃逸、符号链接/重解析点以及文件共享冲突都会在切换上下文前被拒绝。只有 JSON 或模型校验损坏才允许读取 `.bak`，暂时锁定或无权限不能被误判成“内容损坏”。

## 节点到执行器的映射

| 节点 | executor | contractId | 首版行为 |
|---|---|---|---|
| 系统起始蓝图 | `control` | `gassetforge.control.start` v1 | 冻结当前图并开始遍历 |
| 说明蓝图 | `agent` | `gassetforge.agent.instruction` v1 | 把说明文字交给 Agent Adapter |
| 工具类：像素点对齐 | `harness` | `gassetforge.harness.pixel-alignment` v1 | 预留本地 `IPixelAlignmentService` Adapter |
| 工具类：背景剔除 | `harness` | `gassetforge.harness.background-removal` v1 | 预留本地 `IBackgroundRemovalService` Adapter |
| 功能集合蓝图 | `harness` | `gassetforge.harness.function-collection` v1 | 固定功能键和成员节点 ID；后续再做版本化展开 |
| If 逻辑蓝图 | `agent` | `gassetforge.agent.text-condition` v1 | Agent 必须返回结构化判断结果 |

If 的结果合同为：

```json
{
  "schemaVersion": 1,
  "decision": true,
  "reason": "满足用户输入的条件"
}
```

`decision=true` 只沿名称为 `True` 的输出端点继续；`false` 只沿 `False` 继续。执行器不得从自由文本或 Agent Turn 是否结束来猜测节点成功状态。

## 冻结计划与类型化路由

`execution-plan.v1.json` 是图通过校验后编译得到的不可变调度输入。步骤具有连续 `sequence`、稳定 `stepId`、节点合同、参数、前置节点 `dependsOn` 和输出 `routes`。每条 route 同时冻结：

- 输出端点 ID 与名称；
- `kind`（`flow` 或 `data`）、源端点声明的 `sourceValueKind`，以及该目标分支实际绑定的 `valueKind`；
- 目标节点、目标输入端点 ID 与输入名称。

当数据输出声明为 `Any` 时，不同 fan-out 分支可以按各自目标输入收窄 `valueKind`；但同一输出端点的名称、端点种类和 `sourceValueKind` 必须保持一致，不能由计划注入伪造矛盾元数据。

发布前的 `BlueprintExecutionPlanValidator` 会再次独立校验协议名、Schema、步骤/节点唯一性、合同映射、依赖顺序、类型化路由、起点可达性、单输入占用和 DAG。Adapter 只能消费该冻结计划，不能在运行时重新解释正在编辑的 WPF 图。

## 运行提交语义

运行目录按 `execution-plan.v1.json`、`timeline.v1.jsonl`、`README.md` 的顺序准备，最后才以独占新建方式写入 `run-state.v1.json`。因此只有存在且内容为 `status: "planned"` 的 `run-state.v1.json` 才表示批次已经正式提交；缺少该文件的目录是可保留诊断的未提交准备现场，调度器不得认领。时间线使用紧凑的单行 JSONL，每条记录（包括最后一条）都以换行符结束。

## 校验边界

计划发布前会拒绝：未知 Schema、缺失起始节点、错向或不兼容端点、单输入多占用、自连、重复连线、未知节点/端点、依赖/路由不一致，以及任何有向循环。工具节点只保存稳定合同 ID、合同版本和变量，不保存 WPF 类型名或 MCP 显示名称。

当前“开始执行”完成到协议冻结和项目内落盘为止。真实 Harness/Agent Adapter 接入后，应以运行存储中的步骤状态为真相源，不能把 MCP 握手、Codex thread 或 Turn 完成直接视为蓝图步骤完成。
