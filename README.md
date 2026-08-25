# GAssetForge Desktop

GAssetForge 是面向游戏美术资产生产的 Windows 桌面工作台：用 WPF/AvalonDock 组织可停靠工具，
用确定性的本地 Harness 管理项目、约束、中间文件、背景剔除与交付，再通过 Agent Runtime + MCP 调度
真实图像模型完成创造性生成步骤。

> Development status: early alpha, Windows-only, portable builds are currently unsigned.

核心文档：

- [领域上下文](CONTEXT.md)
- [仓库结构](docs/REPOSITORY.md)
- [架构说明](docs/ARCHITECTURE.md)
- [Agent Runtime 与 MCP](docs/MCP.md)
- [项目工作区](docs/PROJECTS.md)
- [数据与隐私](DATA_AND_PRIVACY.md)
- [贡献指南](CONTRIBUTING.md) / [安全策略](SECURITY.md)

## 技术栈

- .NET 8 / WPF
- Dirkster.AvalonDock 4.74.1
- Dirkster.AvalonDock.Themes.VS2013 4.74.1
- Codex App Server（stdio JSONL）与 `codex exec --json` 降级通道
- ASP.NET Core 本机 loopback Streamable HTTP / stdio MCP Server
- SixLabors.ImageSharp 本地背景色键剔除与透明 PNG 输出
- MVVM + `System.Text.Json` + 本地持久化批次队列

> AvalonDock 5.x 需要 .NET 9/10；本仓库在当前 .NET 8 工具链下固定使用 4.74.1。

## 运行

```powershell
cd GAssetForge
dotnet restore .\GAssetForge.sln
dotnet run --project .\src\GAssetForge.App\GAssetForge.App.csproj
```

执行隔离 smoke test：

```powershell
dotnet run --project .\tests\GAssetForge.SmokeTests\GAssetForge.SmokeTests.csproj -c Release
```

Smoke test 使用独立临时数据根，不会改写 `%LocalAppData%` 中的真实项目。

## 正式 Windows 发行

同时发布桌面端与 Agent MCP Server：

```powershell
.\scripts\publish-distribution-windows-x64.ps1
```

默认产物目录为 `dist\GAssetForge-win-x64`，包含：

```text
GAssetForge.exe             正式桌面入口
GAssetForge.McpServer.exe   外部 Agent 的 HTTP / stdio MCP 入口
MCP-README.md                    Agent Runtime 与 MCP 接入说明
mcp-config.example.json          外部 MCP 客户端的手动配置示例
```

两个 EXE 均为 Windows x64 自包含单文件，不要求目标电脑预装 .NET。当前为未签名便携发行，
尚未包含安装程序和代码签名。发行包同时包含 SHA-256 校验和与第三方许可文本。MCP 的详细流程见
[MCP 接入说明](docs/MCP.md)。

## 新建项目与业务模板

顶部不再提供“业务模板”菜单。通过 `文件 → 新建项目…`（`Ctrl+N`）打开新建项目页，填写项目名称、
选择一个已存在的父目录，并选择业务模板。选择模板时只更新待创建配置；点击“创建并进入项目”后，才会
创建独立项目目录、激活当前项目并切换中央业务主视图。

- Tilemap 制作：已接入原生网格工作区，支持主/次网格、XY 轴、中键平移与滚轮缩放
- 卡牌设计：创建项目后进入独立占位工作区
- 平行透视角物体制作：创建项目后进入独立占位工作区
- 页面 UI 设计工具：创建项目后进入原生编辑器
- 测试模板：创建项目后显示 HTML 原型迁移说明

页面 UI 设计工具当前提供：

- 移动工具、文字工具
- 原生 WPF 设计画布、选择与拖动
- PS 式图层树、新建图层、新建文件夹、显示、锁定和删除
- 图层拖入文件夹、同级前排、嵌套文件夹，以及父级锁定/透明度继承
- 属性编辑
- Unity 式 Project 文件夹与 Prefab 浏览
- 按钮、文本输入框、背景框、Slider、Toggle 五个基础 Prefab
- Prefab 拖入画布与 ID 重映射实例化
- Undo / Redo、自动保存、导入与导出
- 暗色 / 亮色主题
- AvalonDock 布局保存、恢复与重置
- 可停靠的美术风格素材库：可从“视图”菜单勾选开启/隐藏，并支持风格分组、多图参考、描述、人工 AI 风格提示词与当前选择持久化；“分析生成统一提示词”会让已连接 Agent 一次检查当前组全部参考图，并把可复用提示词、摘要、素材指纹、分析器版本和生成时间保存为独立样本，不覆盖人工提示词
- 可停靠的素材生成页：选择风格组和 Tilemap 步骤后，以表格批量填写描述、数量与各自输出目录，再原子创建本地持久化任务批次；默认开启“统一提示词优先”，新鲜样本会直接冻结进任务并跳过重复参考图分析，样本缺失或过期则明确回退到描述、人工提示词与参考图；可把指定纯色底作为第一道生成工序，由 Agent 写入 staging、GAssetForge 本地逐张剔色后再交付透明 PNG；自动调度异常时可复制 Codex 连接/当前批次指令，或为已失败任务创建新的手动重试批次
- 可停靠的背景剔除工具：支持洋红、绿、蓝、黑、白和自定义 `#RRGGBB`，可独立选择图片输出透明 PNG，也与素材生成步骤共享色键、阈值、边缘收缩/羽化、去色边和边缘连通设置
- 可停靠的像素点对齐工具：读取 PNG/JPEG/BMP/GIF/TIFF/WebP，使用源像素整数标尺裁切，支持独立自定义行列、中心像素优先采样，以及比例重整、顺时针旋转和四顶点夹角透视矫正
- 可停靠的蓝图窗口：提供系统起始、说明、Harness 工具、功能集合与 If 逻辑节点；添加工具使用类似 PS 的右键工具组和两段式虚影确认，连线可从输入或输出端点起线并右键取消，默认鼠标负责选择及右/中键画布导航，对象移动工具负责整卡拖拽；同时支持项目级保存、版本化执行计划和运行审计目录
- Tilemap Unity 风格网格基础视图与格坐标换算
- 顶部 `MCP 接入` 菜单与可停靠 Agent 连接面板：管理 Codex 认证、App Server / exec 降级状态、MCP ready、thread、Turn 与实时日志，并可复制不含令牌明文的 Codex 连接检查指令；OpenCode 当前仅保留 Adapter 入口

## Agent Runtime 与 MCP 接入

在桌面端选择 `MCP 接入 → 打开 Agent 连接面板`，会拉起 ContentId 为 `mcp-agent-connection` 的单实例
AvalonDock 面板。它可像属性、图层和项目面板一样停靠、浮动或隐藏，停靠位置和隐藏状态随工作区布局
持久化。选择 Codex 并启动 Agent 会话后，桌面端先启动并健康检查
`GAssetForge.McpServer.exe`，再以当前项目根目录（无正式项目时使用 Project 面板根目录）启动受管
Codex Runtime。

首选通道是官方 Codex App Server：桌面端通过 stdio JSONL 完成 `initialize` / `initialized`，读取账号状态，
使用 `thread/start` 或 `thread/resume` 建立上下文，再通过 `turn/start` 发布批次任务；只有 App Server 报告
`gasset_forge` MCP 已 ready 后才把会话视为可执行。App Server 不兼容、异常退出或初始化失败时，运行时
会明确标记降级并使用 `codex exec --json` 执行 Turn。降级通道仍可按 thread ID 恢复后续 Turn，但不提供
App Server 的常驻进程、账号管理与完整实时状态能力。

Windows 下选择 Codex Runtime 时不会只依据文件是否存在。桌面端会对候选程序执行 `--version` 启动探测，
优先采用 PATH 中可实际启动的官方 `codex.cmd` / `codex.bat` shim，再考虑完整的原生 Runtime 和兼容旧版
`codex.exe`。这样可避开发行目录中的不完整副本或无法由普通桌面进程直接启动的 WindowsApps 程序遮蔽
可用 CLI；所有候选均不可启动时会显示明确的 Runtime 诊断，而不会把状态伪装为已连接。

受管 `gasset_forge` 配置只向 Codex 开放 `server_info`、`get_generation_task`、
`claim_generation_task`、`submit_generation_output`、`run_image_delivery_fallback`、
`process_generation_output`、`complete_generation_task` 和 `fail_generation_task`，并设置
`tool_timeout_sec = 900` 与 `default_tools_approval_mode = "approve"`。前者为最长 8 分钟的 Image API CLI
和本地后处理留出有界余量，避免 Codex 默认 60 秒 MCP 工具超时提前取消任务。点击“开始生成”已构成这条本机受管工作流的明确授权；同时 WPF
Host 已实现 Codex App Server 的 `mcpServer/elicitation/request` 入站请求处理，对当前 Running Turn、当前
批次任务和上述写操作进行范围校验，超出范围仍然拒绝。此前 Host 把这类带 `method + id` 的请求统一返回
`-32601`，会被 Codex 解释成 `user rejected MCP tool call`，正是任务无法被认领的协议根因；现在不再依赖
CLI 窗口中的人工批准。

Codex thread ID 与其规范化工作目录成对写入用户设置。重启时仅在当前工作目录相同的情况下恢复旧 thread；
切换或新建项目时不会把旧项目 thread 误用到新项目。素材批次保存在本地队列中，发布失败时任务仍保持
`Pending`，因此 Agent 进程、HTTP MCP 会话或桌面进程的短暂中断不会丢失待办。MCP 在这里仅是工具边界：
提供上下文读取、任务创建/领取/回报与产物校验，不承担 Agent 对话持久化，也不替代 App Server 的
thread / Turn 生命周期。

Agent 连接面板提供“复制 Codex 连接指令”；素材生成页的“Codex 人工接力”区域同时提供连接指令和
“复制当前批次任务指令”。复制内容包含项目目录、MCP Endpoint、工具白名单、令牌环境变量名和有序
task ID，但绝不包含 Bearer Token 明文。连接指令必须在已经加载 `gasset_forge` MCP 的 Codex Host
中通过真实 `server_info` 调用验证；把配置文字粘贴到聊天本身不能动态注册一个尚未加载的 MCP。

如果一个已经启动的 Codex Host 没有加载 `gasset_forge`，或没有继承桌面端生成的临时 Bearer Token，
正式发行中的 `GAssetForge.McpServer.exe` 还提供一次性本地任务桥接：使用 `--invoke-tool` 指定工具，
并用 `--arguments-base64` 传入 UTF-8 JSON 对象的标准 Base64。该模式每次只执行一个工具、输出 JSON 后退出，
不需要启动 HTTP Server，也不需要 Bearer Token 或 MCP 会话；它与桌面端复用同一任务队列、认领状态机、
路径边界和产物校验。桥接仅允许 `server_info`、`get_generation_task`、`claim_generation_task`、
`submit_generation_output`、`run_image_delivery_fallback`、`process_generation_output`、
`complete_generation_task`、`fail_generation_task`，
拒绝创建或枚举任意任务。
Base64 只是命令行传输编码，不是加密；人工接力仍必须使用当前批次给出的明确 task ID，不能把工具白名单
视为任意任务授权。完整调用示例与退出码见 [MCP 接入说明](docs/MCP.md)。

只有 `Pending` 任务能被人工接力认领。自动 Turn 尚未认领时发生分发故障，原任务仍可直接复制批次指令；
任务一旦进入终态 `Failed`，旧 task ID 不能再次 `claim`。此时必须在素材生成页点击“创建手动重试批次”，
由应用复制原失败项约束并创建新的 `Pending` 任务，再复制新批次指令到 Codex；该动作不会自动启动另一个
Agent Turn，旧失败记录继续保留用于审计。

OpenCode 会显示在 Agent 选择中，但当前明确标记为 Adapter 待接入，不能启动可执行会话；不会伪装成已支持。
项目内部可用“MCACP”指代这组 Agent Control + MCP 工具组合，但它不是对外协议名，也不代表已经存在一个
标准化的 MCACP 协议。

### 风格素材库统一提示词

美术风格素材库中的“分析生成统一提示词”使用当前已连接 Agent 执行一次性视觉分析。软件先冻结风格组 ID、
描述、人工提示词、全部参考素材 ID / 绝对路径 / 媒体类型 / SHA-256 与稳定来源指纹，再在 Agent 工作目录创建：

```text
<Agent WorkingDirectory>\.gassetforge\style-analysis\<requestId>\
  request.v1.json
  result.v1.json
  diagnostics\
```

Agent 只能读取 `request.v1.json` 与其中明确列出的图片，并只允许写入约定的 `result.v1.json`；图片内容、图片
文字、二维码、元数据和文件名全部视为不可信数据。聊天文本不作为交付结果，MCP 也不直接改写风格库 JSON。
WPF 会对结果执行无 BOM UTF-8、严格 JSON 字段、路径边界、请求/风格组/来源指纹身份、参考文件 SHA-256、
长度和时间校验；分析结束时还会重新计算当前组指纹，只有素材与人工定义均未变化时才原子保存样本。失败、取消
或并发修改不会覆盖此前样本，请求和诊断目录会保留用于排查。

统一样本与人工 `StylePrompt` 相互独立，包含提示词正文、摘要、来源指纹、生成时间、分析器及版本、请求 ID
和本次覆盖的素材 ID。修改描述、人工提示词、素材集合、素材哈希或媒体类型后，旧样本不会被删除，但会立即
标记为过期。素材生成页默认开启“统一提示词优先”：样本新鲜时任务使用
`parameters.style.sourceMode=unified-prompt-only`，只冻结样本文本与来源元数据，不复制或交给 Agent 参考图；
样本缺失、过期或用户关闭该开关时，自动回退原有描述、人工提示词与参考图快照链路，避免把旧缓存伪装成
当前风格。

### 背景剔除生成链路

新素材任务按“一张图片一个任务”发布，并把色键设置冻结到 `output.backgroundRemoval`。Agent 只需
`claim_generation_task`、调用真实 ImageGen，并把模型返回的绝对 PNG 路径交给
`submit_generation_output(taskId, agentId, outputIndex=0, sourcePath)`。软件会把原图导入当前项目的
`Assets/Generated/GAssetForge/Runs/<batchId>`，依次保留模型原图、画布/底色归一化图和透明处理图，
再校验并原子写入用户指定目录下的最终 target；Agent 不再创建目录、搬运、缩放、抠图或管理图片并行数。

只有最终 `delivery.targetPaths` 会显示在“生成产物”窗口并计入图片进度。没有 `delivery.workspace` 的历史任务
仍兼容旧 `stagingPaths → process_generation_output → complete_generation_task` 流程；不要把该流程套到新任务。
任务创建阶段仍执行 513 MiB 单图 RGBA 解码预算校验；完整 Run 分类、六种预设和兼容规则见
[MCP 接入说明](docs/MCP.md)。

ContentId 为 `background-removal` 的“背景剔除工具”是单实例 AvalonDock 页，可从“视图”菜单开启、隐藏、
浮动或重新停靠。它支持手动选择现有图片和输出位置、原图/透明结果并排预览，以及洋红、绿色、蓝色、黑色、
白色、自定义 Hex、双阈值、边缘连通、去色边、收缩和羽化；手动处理不会自动登记为生成任务产物。

### 像素点对齐工具

ContentId 为 `pixel-grid-alignment` 的“像素点对齐工具”同样是单实例 AvalonDock 页，可从“视图”菜单拉起。
四条标尺使用源图整数像素坐标，`Right / Bottom` 为排他边界；滚轮缩放、中键平移，右键或双击恢复视图。
用户设置最低整数倍率和独立的目标列/行（各 `1–4096`，同时受安全总像素上限约束）后，工具会计算同时满足横纵
网格整除的最小统一倍率，保持原画幅比例且不使用插值。例如 `50×50`、最低 `10×`、`32×32` 网格会自动提升到
`16×`，形成 `800×800` 虚拟工作画幅、每格 `25×25` 工作像素，最终输出真实 `32×32` PNG。互质行列导致的
精确工作坐标可以只在内存中进行虚拟采样，因此 `50×50 → 1024×513` 仍可生成最终 PNG；只有真实导出中间工作图
时才受物理画幅上限约束，避免创建巨型文件。

中心像素是默认和推荐采样方式，直接读取每格中心对应像素，速度最快且不会混色；主色采样保留出现面积最大的完整
RGBA，平均色采样对 Alpha 做权重处理。最近邻工作图可单独导出用于检查像素边界。

矫正可分别启用，并固定按 `四顶点透视 → 比例重整 → 顺时针旋转 → 网格采样` 执行：

- 比例重整将例如 `2:1` 内容真实重映射为 `1:1`，不是用透明边伪装比例变化；目标比例不会暗中覆盖自定义行列，可用显式按钮同步。
- 角度调整中正值表示屏幕坐标顺时针；非方形图片旋转时会扩展并交换所需画布，避免 90° 或任意角旋转裁掉内容。
- 夹角透视矫正使用 `TL → TR → BR → BL` 四个可拖顶点；坐标为源像素连续边界，允许 `Right/Bottom == Width/Height`，目标 A/B 边夹角范围为 `5°–175°`。

当自定义输出网格比例与矫正画布比例不同时，矫正内容会等比居中并使用透明补边，不会通过非等比拉伸改变指定旋转角
或 A/B 夹角。默认四点会随未编辑的裁切边界移动；启用透视时若四点已超出当前裁切范围，会自动重置到裁切角点。
小图预览保持原生解码尺寸，再由 WPF 以最近邻方式显示，不会先放大产生渐变；极端源图尺寸、工作画幅和高熵
主色统计均有明确上限，超过时会提示缩小裁切、增加网格或切换采样方式，而不是无界占用内存。

WPF 每次启动都会生成新的 256 bit Base64Url Bearer Token，只通过 MCP 与 Codex 子进程环境传递。Codex
启动参数只引用 Token 的环境变量名；Token 本身不写入用户设置、项目文件、复制配置、健康响应或日志。
应用退出时按 Runtime → MCP 的顺序停止由桌面端启动的子进程。

桌面管理的 HTTP 入口为：

```text
POST   http://127.0.0.1:43120/mcp
DELETE http://127.0.0.1:43120/mcp
GET    http://127.0.0.1:43120/health
```

HTTP Server 仅绑定本机 loopback 地址，并拒绝非本机客户端。`POST /mcp` 与 `DELETE /mcp` 还必须携带
`Authorization: Bearer <temporary-token>`；`GET /health` 仅供 loopback 健康检查且不返回认证信息。
受管 Codex 接入、HTTP transport、独立 stdio 兼容方式与安全边界见 [MCP 接入说明](docs/MCP.md)。

对应的官方 OpenAI 文档：

- [Codex App Server](https://developers.openai.com/codex/app-server/)
- [Codex CLI reference](https://developers.openai.com/codex/cli/reference/)
- [Codex MCP guide](https://developers.openai.com/codex/mcp/)

## 项目目录与本地数据

正式项目可创建在用户选择的任意有效父目录下：

```text
<ProjectRoot>\
  Assets\
    Generated\
  Packages\
  ProjectSettings\
    gassetforge.project.json
    ui-project.pfui.json
    *.bak
```

项目激活后，Project 面板固定以 `<ProjectRoot>\Assets` 为根，并默认选中 `Assets\Generated`。工作流、
项目名称和 Tilemap 参数写入项目 manifest；页面 UI 编辑数据写入项目自己的 `ui-project.pfui.json`。

应用级共享数据默认写入：

```text
%LocalAppData%\GAssetForge\Desktop
```

如果新目录尚不存在而旧 `%LocalAppData%\PerspectiveForge\Desktop` 存在，应用会继续读取旧目录，避免升级后
设置、任务队列和素材库看似丢失。新项目使用 `gassetforge.project.json`；旧
`perspective-forge.project.json` 仍可加载和保存。完整兼容决策见
[ADR-0001](docs/adr/0001-gassetforge-identity-and-compatibility.md)。

项目级与应用级数据边界如下：

| 范围 | 内容 |
| --- | --- |
| 项目级 | `Assets`、`Packages`、项目 manifest、UI 项目数据、当前工作流和 Tilemap 参数 |
| 应用全局 | 主题、最近项目路径、Agent/MCP 配置、Codex thread ID 与工作目录、素材生成草稿、背景剔除参数、像素对齐倍率/网格/采样方式与最近目录、AvalonDock 布局、本地生成队列、参考图快照和美术风格素材库 |
| 兼容回退 | `Projects\default.pfui.json` 仅供尚未激活正式项目的旧工作区使用，不是正式项目保存位置 |

全局目录中的主要文件为：

```text
Projects\default.pfui.json          未激活正式项目时的兼容 UI 工作区
Workspace\ui-layout-v1.json         AvalonDock 布局
Settings\user-settings.json         主题、Agent/MCP、Codex thread、生成草稿及统一提示词优先、背景剔除与像素对齐设置
Generation\generation-tasks.v1.json 本地持久化批次任务队列
Generation\ReferenceSnapshots\... 任务创建时冻结的风格参考图
Assets\StyleLibrary\art-style-library.v1.json  风格组、人工提示词、统一提示词样本与当前选择
```

风格参考图片会复制到 `Assets\StyleLibrary\assets\<风格组 ID>`，不依赖原始导入路径。创建生成任务时，
所选参考图还会按 SHA-256 复制为不可变任务快照，之后删除或修改原风格组不会改变已排队任务。风格库清单、
项目 manifest、项目内 UI 数据、全局兼容 UI 数据和 AvalonDock 布局会在后续保存时分别维护自己的 `.bak`。
任务快照使用异步流式复制，单任务最多 128 张参考图、单图最大 512 MB、总量最大 2 GB。布局变化不会进入
项目 Schema、dirty 状态、Undo / Redo 或项目导出。项目创建、恢复和数据边界详见
[项目工作区说明](docs/PROJECTS.md)。

统一提示词样本直接随 `art-style-library.v1.json` 持久化；一次性分析请求及诊断位于 Agent 工作目录的
`.gassetforge\style-analysis`，不属于项目资产或生成任务真相源。新鲜样本创建任务时不会再生成风格参考图
快照；只有回退到原链路的任务才会冻结并镜像参考图片。

发布素材任务只代表把冻结约束与项目内 Run 交给已连接的 Agent Runtime；仓库不会因此宣称图片已经生成。
只有 Agent 实际调用可用的图像生成模型，并由 `submit_generation_output` 把真实模型文件导入项目、完成确定性
处理和 MCP 产物校验后，单图任务才会进入 `Completed`。
“生成产物”窗口会按任务冻结合同扫描 `modelOutputPaths`、`stagingPaths`、`processedPaths` 与
`targetPaths`。模型原图、画布归一化图、背景处理图和最终图中任一文件真实落盘后都会立即显示，并按处理中、
成功、自动重做警告和最终失败使用不同边框颜色。中间文件只用于步骤检查和追踪归属，不计入最终图片进度，
也不会把任务伪报为 `Completed`。可恢复问题默认自动重做，初次生成后最多重做 3 次；开关与次数可在素材生成
页调整并持久化，所有失败尝试的已落盘中间文件继续保留供审计。中断后续作时，Agent 只需重新读取并认领
明确 task ID；已完成任务由
`submit_generation_output` 幂等保留，未完成任务重新调用真实 ImageGen 后提交 sourcePath。只有旧任务才需要人工
检查 staging 与调用 process/complete；无法完成时必须调用 `fail_generation_task`，不能把任务遗留在 `Claimed`
后直接报告完成。

## 历史 HTML 原型

旧 `prototype-art-workbench` 未纳入本 Git 仓库，只作为仓库外的历史视觉参照。桌面主线不得重新引入
WebView、Dockview、DOM 路由或 localStorage 作为业务实现。

更完整的设计见 [架构说明](docs/ARCHITECTURE.md)、[项目工作区说明](docs/PROJECTS.md) 与
[迁移阶段](docs/MIGRATION.md)。

## 名称与许可证状态

`GAssetForge` 精确仓库/包名在 2026-08-22 的查询中未发现占用，但存在同领域且近乎同名的
[`G-Asset-Forge`](https://github.com/kw-96/G-Asset-Forge)，也有多个 `Game Asset Forge` 项目和成熟产品
Kenney Asset Forge。公开发布前请阅读[名称冲突检查](docs/NAME-COLLISION-REVIEW.md)，并考虑增加更独特的
品牌词或执行正式商标检索。

项目主许可证尚未选定，因此当前仓库还不能依法宣称为开源项目，也不应在添加根 `LICENSE` 之前正式接收
外部贡献。第三方组件许可见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。
