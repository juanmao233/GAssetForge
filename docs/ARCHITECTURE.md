# WPF 桌面架构

## 总体结构

```text
GAssetForge.Core
  Projects/          项目 Schema、校验、序列化与默认项目
  Workflows/         五类业务入口定义
  ArtStyles/         风格组、参考素材元数据、校验与 JSON 契约
  Generation/        AI 约束包、Tilemap 规则、单图任务状态机、Run Schema 与阶段日志

GAssetForge.App
  ViewModels/        Shell 与 UI 编辑器状态/命令
  Views/             原生 WPF 面板和画布
  Services/          项目、布局、主题、设置、生成 Harness、Run 工作区、日志与 Agent Runtime
  Services/AgentRuntime/
                     Codex App Server stdio JSONL、codex exec JSONL 降级与统一会话契约
  Themes/            暗亮调色板与控件资源
  MainWindow         AvalonDock 外壳及面板注册

GAssetForge.McpServer
  Transports         loopback Streamable HTTP 与兼容 stdio
  Dispatcher         共享 JSON-RPC 握手、会话状态和工具目录
  Context            读取桌面端当前设置与有效 Assets 根目录
  Generation Tools   暴露认领、模型源文件提交、兼容后处理、完成与失败回传
  Submission         软件导入模型原图、归一化、剔色、校验并原子提交最终资产
```

## 核心原则

### AvalonDock 只管理外壳

AvalonDock 的模块接口只承担面板拓扑、大小、停靠位置、隐藏状态和活动页签。图层、Prefab、画布元素及工作流数据不进入布局 XML。

稳定 `ContentId` 是布局反序列化的接口：

| ContentId | 内容 | 默认位置 |
| --- | --- | --- |
| `ui-tools` | 工具栏 | 左侧 |
| `ui-canvas` | 设计画布 / 工作流文档 | 中央 |
| `ui-properties` | 属性 | 右上 |
| `ui-layers` | 图层 | 右下 |
| `ui-project` | Project / Prefab | 画布下方 |
| `art-style-library` | 美术风格素材库 | 与 Project 共用画布下方标签停靠区 |
| `workflow-blueprint` | Agent 与工具工作流蓝图 | 与 Project 共用画布下方标签停靠区 |
| `mcp-agent-connection` | MCP Agent 连接管理 | 默认隐藏；拉起后位于宽底部区域 |

`MainWindow` 维护面板注册表。布局恢复时，`AvalonDockLayoutService` 仅根据 `ContentId` 把真实 WPF View 重新挂接到布局模型；恢复失败则重建默认布局。

### 独立状态生命周期

| 状态 | 适配器 | 生命周期 |
| --- | --- | --- |
| 项目清单 | `ProjectWorkspaceService` | `<ProjectRoot>\ProjectSettings\gassetforge.project.json`；项目身份、工作流与 Tilemap 参数 |
| UI 编辑数据 | `JsonProjectRepository` | `<ProjectRoot>\ProjectSettings\ui-project.pfui.json`；自动保存、备份、导入、导出、Undo / Redo |
| 停靠布局 | `AvalonDockLayoutService` | 窗口关闭保存、启动恢复、可独立重置 |
| 用户设置 | `UserSettingsService` | 主题、最近项目、Agent/MCP 配置、Codex thread ID + 工作目录与素材生成表格草稿 |
| 美术风格素材库 | `JsonArtStyleLibraryRepository` | 风格组与当前选择即时原子保存；参考图片复制到受管目录 |
| AI 生成任务 | `JsonGenerationTaskStore` | 本地跨进程持久化单图任务、原子状态迁移、风格快照与冻结交付计划 |
| 生成 Run | `GenerationRunWorkspaceService` | `<Assets>\Generated\GAssetForge\Runs\<batchId>`；manifest、提示、参考图、模型原图、中间结果与 timeline |
| 工作台日志 | `WorkbenchConsoleService` | `%LocalAppData%\GAssetForge\Desktop\Logs\workbench-*.jsonl`；Agent、Turn、工具和调度阶段的结构化会话日志 |

Shell 会在一次项目激活中协调项目清单、UI repository、Project Browser 和全局设置，但项目内容不会复制进
用户设置或 AvalonDock 布局。用户设置只保存最近项目指针，以及供无项目状态和 MCP 跨进程读取使用的状态
镜像。删除布局数据不会影响项目文件；删除用户设置会丢失最近项目自动恢复、主题、MCP 和生成页偏好，
但不会删除磁盘上的正式项目。

`ArtStyleLibraryView` 是 ContentId 为 `art-style-library` 的跨工作流共享面板。每个风格组保存稳定 ID、名称、
描述、AI 风格提示词和多张参考图片元数据；图片导入后以稳定文件名复制到
`Assets\StyleLibrary\assets\<group-id>`。清单只保存受控相对路径，并校验扩展名、媒体类型、哈希和目录边界。
顶部“视图”菜单使用可勾选开关同步该面板的显示/隐藏状态。当前选中的风格组与素材会随清单持久化，
后续生成流程可直接把它们转换为 provider-neutral 风格约束。

AI 生成任务是第四条本地运行时 seam，保存于 `Generation\generation-tasks.v1.json`。`AssetGenerationView`
以表格收集多行内容描述、数量和输出目录，并把当前风格组与 Tilemap 参数组合为 provider-neutral 约束包。
数量只负责把一行展开成若干个彼此独立的单图任务：新合同固定 `output.count=1`、`outputIndex=0`，任务间并发
交给当前 Agent Host 的默认调度，不再把页面“并行图片数”作为生成协议的一部分。参考图仍先冻结到全局
`Generation\ReferenceSnapshots`，随后由客户端镜像到项目内 Run；交付计划冻结任务创建时的绝对 Assets 根、
项目内运行路径和唯一最终目标路径。任务队列不写入 UI 项目 Schema、Undo/Redo 或 AvalonDock 布局，也不依赖
Agent 或 MCP HTTP 会话持续在线。

### 客户端 + Harness + Skill + MCP/ACP 分层

生成工作台按职责而非按进程名分为四层：

| 层 | 当前职责 | 明确不负责 |
| --- | --- | --- |
| WPF 客户端 | 收集风格、描述、数量和项目输出位置；显示任务、产物、阶段耗时与风险状态 | 不直接调用图像模型，不让 UI 状态充当任务真相源 |
| 本地 Harness | 展开单图任务、创建 Run、冻结合同、归档中间物、归一化画布、背景剔除、验证、原子提交与结构化日志 | 不生成或伪造美术内容 |
| Skill / 图像模型 | 根据冻结提示和参考素材生成一张真实模型图片 | 不建项目目录，不搬运、缩放、抠图、改 Alpha 或决定最终交付路径 |
| Agent Control + MCP | Codex thread/Turn 负责会话与调度；MCP 负责最小工具调用、认领与结果提交 | MCP 不负责会话持久化，Agent 不重复实现 Harness 的确定性步骤 |

项目内部的“MCACP”仍只是 Agent Control 与 MCP 组合的架构简称，不声明为标准协议。后续把风格素材库稳定为
一次性提取并持久化的提示词，或把更多确定性步骤下沉为客户端工具时，只需收窄 Skill 输入和 Agent 指令，
无需改变任务队列、Run manifest 与最终提交合同。

### Agent Runtime、MCP 与进程边界

顶部 `MCP 接入 → 打开 Agent 连接面板` 拉起 ContentId 为 `mcp-agent-connection` 的单实例
`AgentConnectionView`。它与属性、图层和项目区域使用同一 AvalonDock 生命周期，可隐藏、浮动、重新停靠
并随布局 XML 持久化；重复执行菜单命令只会恢复并聚焦同一面板。隐藏面板不会中断会话，主窗口退出时按
Agent Runtime → MCP Server 的顺序回收桌面端启动的子进程。

```text
AgentConnectionView / LayoutAnchorable(mcp-agent-connection)
  ↕ binding / command
AgentConnectionViewModel
  ├─ Codex profile；OpenCode Adapter 明确标记待接入
  ├─ 认证、Runtime、MCP、thread、Turn 与日志状态
  └─ 组合启动/停止与 Turn 调度
          ├──────────────────────────────────────┐
          ↓                                      ↓
McpServerProcessService                 CodexAgentRuntimeService
  ├─ 启动发行 EXE / 开发 DLL               ├─ 首选 CodexAppServerTransport
  ├─ loopback 端口与 /health               │    └─ codex app-server --stdio
  ├─ 256 bit 临时 Bearer Token              ├─ 降级 CodexExecTransport
  └─ Endpoint + Token 环境变量              │    └─ codex exec --json
          ↓                                 └─ thread / Turn / 认证状态
GAssetForge.McpServer                         ↓
  └─ POST/DELETE /mcp + GET /health          本机 Codex CLI
          ↑                                      │
          └────────── MCP tools + Bearer ─────────┘
```

启动组合操作先让 `McpServerProcessService` 进入健康状态，再调用
`CodexAgentRuntimeService.EnsureSessionAsync`。受管工作目录取正式项目根；没有正式项目时使用 Project 面板
根目录。App Server 通道通过 stdio JSONL 执行 `initialize` / `initialized`、`account/read`、
`thread/start` / `thread/resume`、`turn/start` / `turn/interrupt`，并等待
`mcpServer/startupStatus/updated` 将 `gasset_forge` 标记为 ready。认证 Required 是可恢复的账号状态：保留
App Server 供用户完成 ChatGPT device-code 登录，不把它错误降级为 exec。

App Server 不兼容、协议异常、启动超时或进程故障时，统一 Runtime 明确切换到 `ExecFallback`，按 Turn 启动
`codex exec --json` 或 `codex exec resume --json`。降级通道可从 JSONL 事件更新 thread ID，但没有常驻进程、
账号管理和 App Server 的完整 MCP startup 通知。用户取消不触发降级；Turn 使用串行 gate，避免同一 thread
并发执行。

Codex thread ID 必须与规范化工作目录成对保存。Shell 仅在当前工作目录与保存值一致时请求
`thread/resume`；新建或切换项目清空旧 thread，失效 thread 则由 Runtime 创建新 thread。thread 是 Agent
上下文 checkpoint，不是本地生成任务的状态容器。

MCP Endpoint 通过临时 Codex `-c` 配置注入，配置只引用 `bearer_token_env_var`。WPF 启动时生成的 256 bit
Base64Url Token 只存在于 MCP 与 Codex 子进程环境，且所有 Runtime / 进程日志均执行脱敏；Token 不落盘、
不进入项目文件、静态配置或 `/health`。HTTP 仍采用 `Mcp-Session-Id` 隔离，90 秒 TTL 只影响临时工具连接，
不会删除 Codex thread 或本地 `Pending` 任务。stdio MCP 复用同一 dispatcher，但不引入 HTTP Token、
Session Header 或 Agent 类型限制。

MCP 负责工具调用和授权边界，不负责 Agent 持久会话。项目内部的“MCACP”只是对 Agent Control + MCP
组合的架构简称，不作为标准协议、产品兼容性或外部配置格式宣传。OpenCode 当前只有不可启动的 profile 与
Adapter seam；在实现并验证真实 Adapter 前不能呈现为可用连接。

实现以官方 OpenAI 文档为基线：

- [Codex App Server](https://developers.openai.com/codex/app-server/)
- [Codex CLI reference](https://developers.openai.com/codex/cli/reference/)
- [Codex MCP guide](https://developers.openai.com/codex/mcp/)

### Agent 与 AI 生成边界

本地队列负责排队与状态持久化，MCP Server 只把工作台状态、约束、任务操作和结果校验暴露为 tools；两者
都不直接假装拥有图像模型。批次成功落盘后，`AssetGenerationViewModel.BatchPublished` 把稳定任务 ID 交给
Shell，Shell 使用 `GenerationAgentPromptBuilder` 构造 prompt，并通过受管 Codex Runtime 启动串行 Turn。
分发失败只记录 dispatch 错误，任务继续保持 `Pending`，因此 Runtime 故障不会吞掉本地待办。

批量表格单行允许 1–16 张、最多 128 行、整批最多 256 张；发布时每个数量变体都创建一个独立单图任务，
而不是让一条任务内部管理多张图片和自定义并行槽位。某一图片失败不阻塞同批其它任务；重试会创建新的
任务/批次并保留旧失败记录。新任务的 Agent 执行链固定为：

```text
claim_generation_task
  → 使用 preferredSkill 调用真实 ImageGen，得到本地 sourcePath
  → submit_generation_output(taskId, agentId, outputIndex=0, sourcePath)
  → 软件自动完成任务登记
```

Agent 不再自行创建目录、扫描目标、搬运文件、调整尺寸、补底色、抠图、修改 Alpha，或重复调用旧
`process_generation_output` / `complete_generation_task`。这些确定性步骤由
`GeneratedOutputSubmissionService` 和本地后处理器执行。只有没有 `delivery.workspace` 的历史任务继续使用
旧多图/staging 合同。

每次发布先在当前项目 Assets 内创建以下 Run；中间文件保留供人工检查和性能分析：

```text
Assets/Generated/GAssetForge/Runs/<batchId>/
  00-manifest/run.v1.json
  10-style-reference/
  20-prompts/
  30-model-output/<itemId>/*.model.png
  35-normalized-input/<itemId>/*.normalized.png
  40-background-removed/<itemId>/*.transparent.png
  50-final/
  90-diagnostics/stages.v1.jsonl
```

`00-manifest/run.v1.json` 将每个 `itemId`、`taskId`、表格行、变体序号、模型原图、归一化图、剔色图和最终
目标关联起来。`50-final` 是 Run 内预建的最终阶段分类 seam；权威交付文件仍按用户选定位置写入
`<输出目录>/Tilemap/<batchId>/Final/*.png`，并由任务冻结的 `delivery.targetPaths` 决定，避免回落到 Codex
默认 `generated_images` 或用户后来切换的项目。

`GenerationRunJournal` 以 append-only JSONL 记录 `runId`、`batchId`、`taskId`、`outputIndex`、阶段、状态、
起止时间、`elapsedMilliseconds`、Agent、期望/观察路径和错误码。当前稳定阶段包括 `workspace.prepare`、
`style.snapshot.project_copy`、`task.claim`、`model_output.import`、`model_output.normalize`、
`output.process_and_commit`、`task.complete` 与 `task.fail`。同时，Workbench 会话日志记录调度阶段和 Agent
工具活动；控制台命令 `generation timeline` 可直接查看当前阶段、当前耗时、最长阶段和图片落盘/验证数量。

完成校验使用任务创建时的 `delivery.assetRoot`，不读取用户后来切换的新 Project 根；Tilemap PNG 会真实
解压 IDAT、校验精确尺寸，并要求瓦片有效轮廓外 Alpha 为 0。Core 同时冻结 A/B 两条边的绝对屏幕角度；
90°/0° 被明确解释为画布轴对齐正方形，Agent 不得二次概括为 diamond/rhombus，校验器也会拒绝四个角部
缺失的内接 45° 钻石形。任务认领使用 30 分钟租约，异常退出的 Agent 不会
让任务永久停留在 `Claimed`。这样桌面端持久化、Agent 编排、MCP 工具和具体图像供应商保持解耦。

产物校验对 Tilemap 的参数、派生 metrics、轴向量和背景合同执行 fail-closed；瓦片有效轮廓内必须存在可见像素。
PNG 解析同时限制文件、解压像素、chunk、IDAT 与非图像数据预算，避免畸形任务或压缩文件占满内存与 CPU。

第一版 Tilemap 约束构建器统一输出画布尺寸、菱形角、旋转、投影、像素密度、结构化轴向量与真实
Alpha 透明规则。桌面属性面板和 MCP 共用同一 Core 构建器，避免两份提示词数学发生漂移。
旋转新契约以屏幕 A/X 轴向右为 `0°`、正值顺时针；Validator 仍识别旧版菱形对称轴零角契约，保证
已持久化任务可以继续完成。

风格参考图在任务创建时异步流式复制并增量计算 SHA-256；Core、风格库和快照服务共享最多 128 张、
单图 512 MB、总量 2 GB 的边界，避免大风格组阻塞 UI 或在不同层产生不一致结果。

HTTP 传输只允许绑定和访问 loopback 地址，请求体限制为 4 MiB，并拒绝非 JSON 的 MCP POST。
WPF 启动时生成的 256 bit Bearer Token 不落盘、不写日志；`/health` 无需 Token 以供桌面轮询，但仍严格限制
loopback 且不返回认证信息。该端口仍不得经代理或端口转发发布到局域网或公网。

### 正式项目工作区

`文件 → 新建项目…`（`Ctrl+N`）是业务模板的正式入口，顶部不再保留“业务模板”菜单。新建页收集项目
名称、已存在的父目录和工作流；选择模板只更新待创建配置，创建成功后才切换中央业务主视图。

`ProjectWorkspaceService` 先在所选父目录创建唯一 staging 目录，写入并验证完整项目，再通过同父目录
`Directory.Move` 激活最终目录。项目名必须符合 Windows 文件名规则；目标必须是所选父目录的直接子目录；
磁盘根目录、已存在目标、Windows 保留设备名和 reparse point 会被拒绝，失败时清理 staging 目录。

正式项目激活后，`JsonProjectRepository` 切换到项目内 UI 文件，Project 面板固定以
`<ProjectRoot>\Assets` 为根并默认选中 `Assets\Generated`，且禁止从 Project 面板跳出项目资产边界。
`user-settings.json` 保存 `lastProjectPath`。启动时若项目目录、manifest、UI 数据和必要子目录通过验证，Shell
会恢复业务主视图、项目级 Tilemap 参数、UI repository 与 Project 面板；路径失效或主文件和备份均无效时
不会激活该项目，也不会删除原目录。更完整的目录与恢复契约见 [项目工作区说明](PROJECTS.md)。

### 业务工作流

`WorkflowCatalog` 是模板注册模块。五类工作流在新建项目页中选择；创建前不改变当前工作区，创建成功后把
模板 key 写入项目 manifest，并由 `ShellViewModel` 将中央文档切换为 `EditorWorkspaceViewModel`、
`TilemapWorkspaceViewModel` 或 `WorkflowPlaceholderViewModel`。未实现的业务仍使用相同基础业务面板位置；
美术风格素材库、蓝图窗口和 MCP 连接面板作为跨工作流共享模块独立复用。蓝图窗口提供系统起始、说明、
Harness 工具、功能集合与 If 逻辑节点，支持端点和有向无环连线、画布平移缩放、节点整理及项目级 JSON
持久化。图文件显式版本化、限制为 8 MiB，并拒绝越界路径与重解析点。开始执行时先冻结带类型化路由的版本化计划并独立复验，
再把计划和审计时间线落入项目 `Assets/Generated/GAssetForge/BlueprintRuns`；`run-state.v1.json` 最后写入并作为 planned 提交标记，
在真实 Adapter 接入前不会误触发 Agent 或图片处理。详细合同见 [蓝图 Harness 协议](blueprint-harness-protocol.md)。

蓝图编辑器把持久图与交互瞬态分离：节点放置虚影和端口牵引线不会写入 Graph，第二次左键确认后才创建节点；输入、输出端点均可作为牵线起点，最终统一规范为 Output → Input。默认鼠标只负责选择、滚轮缩放及右/中键平移，右键双击恢复 `(0,0,1)` 视口；对象移动工具会拦截节点整卡输入并拖动对象。添加按钮与画布空白区共享四类型右键工具组，常驻工具栏不再占用独立类型下拉框。
目前尚无手动“打开已有项目”入口，已有项目只通过 `lastProjectPath` 在重启时自动恢复。

Tilemap 第一阶段由 `TilemapWorkspaceView` 和 `TilemapGridSurface` 组成。网格使用单个 WPF `FrameworkElement.OnRender` 绘制可见范围内的主/次网格、XY 轴与悬停格，不创建海量 `Line` 控件；当前提供中键平移、光标锚点缩放、格坐标换算以及暗亮主题资源。瓦片数据、笔刷和图层模型将在此渲染 seam 上继续扩展。

### UI 编辑器

`EditorWorkspaceViewModel` 当前集中处理第一阶段编辑事务：

- 选择、移动和文字创建
- 图层与文件夹变更
- 图层树归组、同级排序与循环引用防护
- Prefab 实例化及 ID 重映射
- 属性修改
- 历史快照和自动保存

View 只负责 WPF 输入适配与渲染。连续拖动通过 `BeginMove` / `PreviewMove` / `EndMove` 形成一次历史事务，避免每个鼠标像素产生一条 Undo 记录。

文件夹的 `Visible`、`Locked` 与 `Opacity` 会递归计算为子图层的 `EffectiveVisibility`、`EffectiveLocked` 和 `EffectiveOpacity`。画布移动与属性修改使用有效锁定状态，而不是只检查当前节点自身值。

Shell 的 UI 编辑命令通过 `GatedCommand` 接到稳定 seam：非 UI 工作流或业务选择层打开时，保存、导入导出、撤销重做、删除和缩放都无法穿透到隐藏的 UI 项目。

## 验证

`GAssetForge.SmokeTests` 是无第三方测试框架的 STA WPF 验证程序，使用隔离临时目录。发布前的覆盖
清单包括：

- 五个业务面板加默认隐藏 MCP 面板的拓扑与稳定 ContentId
- AvalonDock 保存、恢复、内容重绑和备份恢复
- 暗亮主题与非法设置回退
- 五业务入口、Tilemap 独立网格工作区及 UI 命令门控
- 文件菜单的新建项目入口、旧业务模板入口移除及选择模板不提前切换工作区
- Unity 式目录创建、非法名称、重复目录、staging 清理和 Project Browser Assets 边界锁定
- 项目内 UI repository 与全局兼容工作区隔离，以及工作流、Tilemap 参数和最近项目的重启恢复
- 图层、文件夹、归组、排序、锁定、透明度、文字与 Prefab
- Undo / Redo、项目保存重载和轻量会话退出保存
- MCP 菜单与单实例停靠面板、隐藏/浮动/恢复、Codex profile，以及 OpenCode 不可用状态不被伪装
- HTTP 子进程启动停止、initialize / initialized / tools/list 握手、健康状态和日志
- Bearer Token 注入、401 拒绝、日志脱敏与 `/health` 不泄密
- fake process 下的 App Server initialize、账号读取、thread start/resume、MCP ready、Turn 完成与异常退出
- `codex exec --json` 降级、thread ID + 工作目录恢复，以及切换项目不误用旧 thread
- 数量展开为一图一任务、页面并行数不进入 Agent prompt、批次调度失败仍保留 `Pending`
- 项目内 Run 目录、manifest、提示与参考图镜像、中间图保留和结构化阶段 timeline
- `claim → ImageGen → submit_generation_output`、源图归档、尺寸归一化、底色补齐、背景剔除、最终校验与自动完成
- `submit_generation_output` 的幂等、认领归属、路径边界与单图合同，以及旧多图 staging/process/complete 兼容
- Workbench Agent 活动持久化、阶段耗时、最长步骤与 `generation timeline` 控制台输出
- 原有 stdio MCP 工具目录与历史任务流程兼容性

这些自动测试不得启动真实模型 Turn 或产生图像生成费用；图像产物验证使用隔离测试文件，只验证合同与
状态机。

## 项目 Schema

正式项目包含两个独立的版本化契约：

- Project manifest Schema v1：项目 ID、名称、工作流、创建/更新时间及项目级 Tilemap 参数。
- UI Project Schema v2：documents、layers、Prefab library、editor session 和预留 `workflowGraphs`。

manifest 与 UI 文件分别回退读取自己的 `.bak`，不能用其中一个文件代替另一个。UI Project Schema v2
当前包含：

- `documentsById` / `activeDocumentId`
- Canvas、`rootIds`、归一化 `layersById`
- component / text / group 图层
- 资源文件夹与 Prefab payload
- 预留 `workflowGraphs`

图层顺序保持“最上层在前”；WPF 渲染前转换成 bottom-first，并为每个可绘制图层设置 `ZIndex`。

## 后续可扩展 seam

- `workflowGraphs`：ComfyUI 式节点图、Node Registry、执行队列
- 风格素材库的稳定提示词提取/版本化缓存，让重复批次直接复用已审核风格描述
- 可插拔图像模型 Skill Adapter，同时保持单图 sourcePath 提交合同不变
- Run `50-final` 镜像、清理/归档策略和按阶段重放工具
- Photoshop MCP：PSD 打开、图层同步、后处理
- OpenCode Agent Runtime / ACP Adapter（完成前保持不可启动）
- 远程部署所需的独立认证与细粒度权限（仅在出现真实远程需求后评估；当前 loopback Bearer 不适用公网）
- Prefab 关联实例：Apply / Revert / Variant / Nested Prefab
- 多文档、浮动窗口和多显示器布局

只有出现第二个真实适配器时再抽象新的公共接口，避免为尚未变化的实现提前制造浅模块。
