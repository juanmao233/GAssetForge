# GAssetForge Agent Runtime 与 MCP 接入

`GAssetForge.McpServer.exe` 是 GAssetForge 暴露本地工作流、任务和产物校验能力的 MCP
Server。桌面端同时管理 Codex Agent Runtime：首选通过 Codex App Server 的 stdio JSONL API 保持受控
会话；App Server 不兼容或异常时，明确降级到 `codex exec --json`。

这几类状态必须分开理解：

| 状态 | 所属组件 | 持久性与用途 |
| --- | --- | --- |
| Agent thread | Codex App Server / Codex CLI | `threadId` 与工作目录成对保存，用于恢复同一项目的对话上下文 |
| Agent Turn | Codex Runtime | 一次批次发布对应一次串行执行，可完成、失败或中断 |
| 生成任务 | 本地 `JsonGenerationTaskStore` | 持久化批次队列；独立于 Agent 进程和 MCP 协议会话 |
| MCP 协议会话 | `GAssetForge.McpServer` | 临时工具连接；HTTP 会话有活动 TTL，不保存 Agent 对话 |

```text
GAssetForge.exe
  ├─ AvalonDock Agent 连接面板
  ├─ CodexAgentRuntimeService
  │    ├─ 首选：codex app-server --stdio（JSONL）
  │    │          thread/start | thread/resume | turn/start
  │    └─ 降级：codex exec --json
  ├─ 本地持久化单图任务队列
  ├─ Generation Harness
  │    ├─ 项目内 Run / manifest / prompts / timeline
  │    ├─ 模型原图导入与画布归一化
  │    └─ 背景剔除、约束验证与最终原子提交
  └─ GAssetForge.McpServer.exe（loopback HTTP）
           ├─ 当前项目 / Tilemap / 风格约束
           ├─ 任务创建、领取与模型源文件提交
           ├─ 新合同：submit_generation_output
           ├─ 旧合同：process / complete 兼容工具
           └─ 真实最终产物路径与 PNG 合同校验
                         ↑
                    Codex 调用 MCP tools
```

MCP 只承担工具边界，不承担持久 Agent 会话、Turn 调度或进程保活。任务持久性由本地队列提供；Agent
上下文持久性由 Codex thread 提供。项目内部可以用“MCACP”简称这组 Agent Control + MCP 工具组合，但
它不是公开标准协议名，也不表示项目实现了一个名为 MCACP 的第三方协议。

## 推荐接入：桌面端管理 Codex

前提是本机已安装可用的 Codex CLI。推荐流程：

1. 启动 `GAssetForge.exe` 并创建或恢复项目。
2. 选择 `MCP 接入 → 打开 Agent 连接面板`。
3. 选择 Codex，确认工作目录、MCP 端口和账号状态。
4. 点击“启动 Agent 会话”。桌面端先确保 MCP Server 的 `/health` ready，再启动 Codex Runtime；若 MCP
   已通过“仅启动 MCP”独立运行，则直接复用该服务。
5. 如果账号需要登录，在同一停靠面板发起 ChatGPT device-code 登录，并在登录后刷新认证状态。
6. 确认 Runtime、MCP Server、MCP tool 和 thread 状态均可执行，再从“素材生成”页发布批次。
7. 若自动 Turn 无法认领任务，先在 Agent 面板复制连接检查指令；素材生成页还可复制当前 `Pending` 批次
   指令，或为已经 `Failed` 的批次创建新的手动重试批次后再复制。

Agent 连接面板的 ContentId 为 `mcp-agent-connection`。它是单实例 AvalonDock 面板，可停靠、浮动、隐藏并
随布局恢复；隐藏面板不会结束会话。MCP Server 可与 Codex Runtime 分离启停，便于在 CLI 不可用时单独
检查端口、健康状态和外部握手。Runtime 启动失败不会再回滚已经健康的 MCP Server。应用退出时仍按
Agent Runtime → MCP Server 的顺序回收桌面端启动的子进程。

OpenCode 仍显示为后续适配入口，但当前明确标记为 Adapter 待接入，不能启动持久会话或接收自动任务。
静态 MCP 配置样例不等于 OpenCode Agent Runtime 已实现。

## Codex Runtime 通道

### Windows Runtime 选择

当配置的可执行文件为 `codex` 时，桌面端会枚举候选并用 `codex --version` 做真实启动探测，而不是仅检查
文件是否存在。选择顺序为：

1. 用户在 Agent 面板中明确指定的 `.cmd`、`.bat` 或 `.exe`；脚本通过 `cmd.exe /d /s /c` 启动。
2. `%APPDATA%\npm\codex.cmd` / `codex.bat`，即 npm 全局安装的标准 shim，即使桌面进程继承的 PATH
   不含 npm 目录也会主动检查。
3. `%LOCALAPPDATA%\Programs\OpenAI\Codex\bin\codex.exe`，即官方 Windows standalone 安装位置；
   即使应用启动时继承的 PATH 尚未刷新也会主动检查。
4. PATH 中可启动的 `codex.cmd` / `codex.bat` shim；由 shim 自行解析其原生 Runtime。
5. PATH 中包含 `codex-code-mode-host.exe` 配套组件、且可启动的完整 `codex.exe` Runtime。
6. 应用目录中的完整 Runtime。
7. PATH 中仍可启动的兼容旧版独立 `codex.exe`。

自动模式优先选择 npm shim，是因为官方 launcher 能解析自己的原生资源，而 WindowsApps 下看似存在的
`codex.exe` 可能因应用包执行权限被普通 WPF 进程拒绝。WindowsApps 原始 EXE 会作为明确的跳过项保留在
诊断中，不会遮蔽可用 shim。探测面板逐项显示来源、完整路径、接受/拒绝/跳过状态及失败原因（例如拒绝
访问、超时、非零退出码或 stderr）；失败结果不会永久缓存，可在安装或修复 CLI 后点击“重新检测”。

手动路径会保存在用户设置中，空值表示恢复自动检测。显式路径无效时不会静默改用其他 PATH 候选，避免
软件实际运行的 CLI 与用户选择不一致。

每次新建受管 Agent 会话时，桌面端会重新合并当前进程、Windows Machine 和 User 的 PATH，并把快照显式
传给 Codex 子进程。这样用户在 GAssetForge 已打开后安装 Python 或修复用户 PATH，也不会让新 Agent 会话
继续继承应用启动时的旧 PATH；图片 CLI 指令仍优先使用预检返回的绝对 Python 与脚本路径。

### 首选：Codex App Server

桌面端以当前项目根目录作为工作目录启动：

```text
codex app-server --stdio
```

MCP Endpoint、`required=true` 和 Bearer Token 环境变量名通过该子进程的临时 `-c` 配置注入，Token 值只在
子进程环境中存在。协议流显式使用 UTF-8 no-BOM；正常情况下每行一个 JSON-RPC 消息。兼容分帧器也能从
单行多个 JSON、垃圾前缀或中间片段后安全恢复，非协议内容只记录偏移、长度和短 SHA-256，不进入 Agent
正文，也不会逐条淹没工作台日志。桌面端执行的关键生命周期为：

受管 App Server 与 Exec fallback 都保持 `sandbox_mode = "workspace-write"`，并仅为该 sandbox 显式注入
`sandbox_workspace_write.network_access=true`，让官方 `image_gen.py` 可以尝试访问 Image API；不会为此切换到
`danger-full-access`。该配置只证明权限已声明，不证明用户网络、DNS/TLS、API Key、配额或 Image API 实际可用。
每次 `turn/start` 还会显式冻结 `workspaceWrite`、当前项目唯一 `writableRoots` 和 `networkAccess=true`，避免
进程默认值被 Turn 级策略覆盖。图片交付预检会分别验证输出目录、Python 启动、`image_gen.py --help`、
`from openai import OpenAI` 和 `OPENAI_API_KEY`；因此素材生成状态只显示“CLI 本地前置已就绪 / 网络未验证”，
实际请求失败时保留原始分类诊断。

1. `initialize` / `initialized` 建立 App Server 协议连接并检查运行时版本。
2. `account/read` 读取认证状态；需要登录时保留 App Server，而不是把认证问题误判为传输故障。
3. 工作目录匹配时调用 `thread/resume`；没有可恢复 thread 时调用 `thread/start`。
4. 等待 App Server 的 `mcpServer/startupStatus/updated` 将 `gasset_forge` 报告为 ready。
5. 素材批次发布后调用 `turn/start`，并监听 Turn 完成通知；用户可执行 `turn/interrupt`。

WPF Host 同时处理 App Server 发来的 JSON-RPC 入站请求 `mcpServer/elicitation/request`。本次“任务未被
认领”的实际根因是旧 Host 把所有带 `method + id` 的入站请求当作不支持方法并返回 `-32601`；Codex 因而
把 `claim_generation_task` 的批准结果记录为 `user rejected MCP tool call`。现在连接层会把请求交给
App Server transport，并按协议返回 `accept` 或 `decline`，不再把受管工具调用误判为用户拒绝。

受管 Turn 的 `gasset_forge` 配置固定为：

```toml
enabled_tools = ["server_info", "get_generation_task", "claim_generation_task", "submit_generation_output", "process_generation_output", "complete_generation_task", "fail_generation_task"]
default_tools_approval_mode = "approve"
```

因此 Codex 只能看到任务执行所需的七个工具，不能通过这条受管连接创建或枚举任意任务。点击“开始生成”
就是用户对当前本机批次的显式授权。`default_tools_approval_mode = "approve"` 负责常规自动批准；如果 App
Server 仍发送 elicitation，Host 仅接受当前 Running Turn 中、`gasset_forge` Server 上、当前 prompt
所列 task ID 的 `claim_generation_task`、`submit_generation_output`、`process_generation_output`、
`complete_generation_task` 或 `fail_generation_task`，其余请求全部 fail-closed 拒绝。日志只记录脱敏后的
工具与短 task ID，不记录
Bearer Token。

`threadId` 与规范化工作目录一起写入 `Settings\user-settings.json`。只有恢复后的工作目录与保存值一致时才
复用 thread；新建或切换项目必须清除旧项目 thread。若保存的 thread 已失效，运行时记录警告并创建新
thread，不把旧 ID 当作本地任务队列的身份。

### 自动降级：`codex exec --json`

App Server 版本不兼容、初始化超时、协议异常或子进程故障时，桌面端会显示降级状态，并使用 Codex CLI 的
JSONL 事件输出执行 Turn：

```text
codex exec --json ... -
codex exec resume --json ... <threadId> -
```

降级通道仍注入同一个临时 MCP 配置，并能从 JSONL 事件更新 thread ID。它不是常驻 App Server，因而不提供
账号读取、登录/登出、完整 MCP startup 状态或相同粒度的进程通知；每次 Turn 都是单独 CLI 子进程。界面
必须明确显示 `Exec fallback`，不能把它呈现为 App Server 正常连接。用户主动取消不触发自动降级。

官方 OpenAI 文档：

- [Codex App Server](https://developers.openai.com/codex/app-server/)
- [Codex CLI reference](https://developers.openai.com/codex/cli/reference/)
- [Connect Codex to MCP servers](https://developers.openai.com/codex/mcp/)
- [Run Codex as an MCP server](https://developers.openai.com/codex/mcp-server/)
- [Codex sandbox](https://developers.openai.com/codex/sandboxing/)
- [Codex configuration reference](https://learn.chatgpt.com/docs/config-file/config-reference)
- [Image generation guide](https://developers.openai.com/api/docs/guides/image-generation)

## 素材批次与自动发布

“素材生成”页的步骤 2 使用批量表格。每行指定内容描述、生成数量和 Assets 内输出目录：

- 单行数量为 1–16。
- 最多 128 行。
- 整批最多预分配 256 个产物。
- 点击发布时原子创建批次；每一行按数量展开，每张图片形成一个独立 `GenerationTask`，共享本次风格与
  工作流约束快照。
- 新任务固定 `output.count=1`、`outputIndex=0`。`maxParallelism=1` 只作为旧 Schema 兼容值保留，页面不再
  暴露或向 Agent 传递“并行图片数”；任务间并发和多 Agent 分配由当前 Agent Host 默认调度。
- 单张失败不会阻塞其他任务；重试失败项时创建新任务和新批次，旧失败记录保留用于审计。
- 发布前由软件创建项目内 Run、最终分类目录、manifest、提示文件和固定交付路径；Agent 无需判断目录结构。

批次落盘并更新 UI 后，Shell 根据任务 ID 构造 Agent prompt，并把发布请求交给 Codex Runtime。Turn 串行
执行，避免多个批次同时争用一个 thread。若 Runtime 尚未接入、Turn 启动失败或 Agent 进程中断，分发状态
会记录错误，但本地任务仍保持 `Pending`，可在恢复连接后重试。MCP Server 继续从同一队列读取任务，不依赖
某个瞬时 HTTP MCP 会话一直在线。

### Codex 人工接力

自动通道异常时有两个复制入口：

- Agent 连接面板的“复制 Codex 连接指令”，用于核对项目工作目录、Endpoint、`gasset_forge` 注册状态
  和 `server_info` 返回的数据根。
- 素材生成页“Codex 人工接力”区域的“复制 Codex 连接指令”和“复制当前批次任务指令”；批次提示包含
  batch ID、有序单图 task ID，以及 `claim → 真实 ImageGen → submit_generation_output → fail` 的最小
  执行链。只有 claim 返回没有 `delivery.workspace` 的历史任务，才会提示旧 staging/process/complete 流程。

复制内容只引用 Bearer Token 的环境变量名，不包含 Token 值。提示中的 TOML 用于核对当前 Host 配置或
下一次启动；普通聊天消息不能在运行中的 Codex Host 里动态加载尚未注册的 MCP，因此必须实际确认工具列表
中存在 `gasset_forge`，并成功调用只读 `server_info` 后才能声称线路已恢复。

#### 已启动 Host 的一次性本地任务桥接

如果当前 Codex Host 没有加载 `gasset_forge`，或没有继承桌面端启动时生成的临时 Bearer Token，不能
仅靠把 TOML 粘贴到聊天里修复当前 Host。此时可使用正式发行中的 `GAssetForge.McpServer.exe` 作为
一次性本地任务桥接。它直接打开与桌面端相同的数据根和任务存储，每次执行一个工具、把 JSON 结果写到
stdout 后退出；不需要先启动 HTTP Server，也不需要 Bearer Token、`Mcp-Session-Id` 或 MCP 动态注册。

PowerShell 示例：

```powershell
$toolArguments = @{
  taskId = "<当前批次中的明确 task ID>"
  agentId = "codex-manual"
} | ConvertTo-Json -Depth 64 -Compress
$argumentsBase64 = [Convert]::ToBase64String(
  [Text.Encoding]::UTF8.GetBytes($toolArguments))

.\GAssetForge.McpServer.exe `
  --invoke-tool claim_generation_task `
  --arguments-base64 $argumentsBase64
```

`--arguments-base64` 必须是 UTF-8 JSON 对象的标准 Base64，解码后上限为 4 MiB；无参数的 `server_info`
可以省略该选项并使用空对象。Base64 只用于避免命令行 JSON 转义问题，不是加密，参数中不得放入 Bearer
Token 或其他不应出现在进程命令行中的秘密。需要隔离诊断时可同时传入
`--data-root "D:\Temp\GAssetForge-Agent-Test"`；正式任务不要误指向测试数据根。

桥接严格只允许以下七个工具：

- `server_info`
- `get_generation_task`
- `claim_generation_task`
- `submit_generation_output`
- `process_generation_output`
- `complete_generation_task`
- `fail_generation_task`

`create_generation_task`、`list_generation_tasks`、`get_current_context` 以及其他未知工具会在调用前被拒绝。
桥接仍复用正式任务状态机、Agent 认领归属、目标路径边界和 PNG/Tilemap 产物校验，但它不建立 Codex thread、
不提供 Turn 调度，也不证明 MCP 已加载。工具白名单也不是任意任务授权：人工接力必须逐个传入复制提示给出的
task ID，不能省略 task ID 去领取其他待办。成功返回退出码 `0`；工具返回结构化错误时为 `2`；参数解析或
启动失败时为 `1`。只有 MCP 调用或本地桥接其中一条真实成功，才可继续处理任务，并应准确记录采用的线路。

人工批次提示只包含当前批次的 `Pending` 任务。`Failed` 是终态，旧 task ID 已不能再次
`claim_generation_task`；把旧失败提示重新粘贴只会再次失败。若当前批次存在失败项，必须点击“创建手动
重试批次”，让应用基于原约束创建一组新的 `Pending` task ID。新批次不会自动触发 Agent Turn，用户可在
检查提示后手动复制到 Codex；旧 `Failed` 记录保留并标记已由重试项接替，便于审计。

发布任务不等于图片已经生成。新任务由 Agent 调用真实图像模型取得本地 PNG `sourcePath`，随后调用
`submit_generation_output`。GAssetForge 才会把原图导入项目 Run、执行确定性后处理、原子写入唯一
`targetPath` 并自动把单图任务登记为 `Completed`。MCP Server 不伪造图片，也不用程序化占位图代替真实模型
输出；如果 Agent 没有图像生成能力，应保留待办或用明确原因标记失败。旧任务仍按原 `stagingPaths`、
`process_generation_output` 和 `complete_generation_task` 合同完成。

### 项目内 Run、部分产物与中断续作

客户端在任务发布前创建固定 Run：

```text
<Assets>/Generated/GAssetForge/Runs/<batchId>/
  00-manifest/run.v1.json
  10-style-reference/
  20-prompts/
  30-model-output/<itemId>/*.model.png
  35-normalized-input/<itemId>/*.normalized.png
  40-background-removed/<itemId>/*.transparent.png
  50-final/
  90-diagnostics/stages.v1.jsonl
```

`run.v1.json` 把表格行、变体、`itemId`、`taskId`、各中间文件和最终 `targetPath` 固定关联。Run 内中间物
默认保留，供检查模型到底返回了什么、软件是否发生缩放、剔色结果是否正确，以及哪一步耗时或失败。
`50-final` 是预建的最终阶段分类 seam；正式资产仍落到用户为该行指定的位置之下：

```text
<所选 Assets 内目录>/Tilemap/<batchId>/Final/*.png
```

若用户选择的就是 Assets 根，软件使用 `Assets/Generated/Tilemap/<batchId>/Final`。权威位置始终来自任务冻结
的 `delivery.assetRoot + delivery.targetPaths`，不会回落到 Codex 默认 `generated_images`，也不会随之后的
Project 切换漂移。

“生成产物”窗口按最终 `targetPaths` 计算图片落盘进度；项目 Run 的模型原图、归一化图和剔色图属于诊断
中间物，不计入正式图片数量。新任务中断后的续作规则是：

1. 使用明确 task ID 调用 `get_generation_task` / `claim_generation_task`；租约回收后重新认领。
2. 若任务已经 `Completed`，或 `submit_generation_output` 判断最终目标已有效，则幂等保留，不重新生成。
3. 若任务仍缺少最终结果，重新调用真实 ImageGen 获得本地 `sourcePath`，再调用
   `submit_generation_output(taskId, agentId, outputIndex=0, sourcePath)`；Agent 不扫描或修补 Run。
4. 只有 `delivery.workspace` 缺失的历史任务，才检查 `stagingPaths` 并用旧
   `process_generation_output` / `complete_generation_task` 续作。
5. 无法继续时调用 `fail_generation_task`，记录 ImageGen 或提交工具返回的准确错误。任何 `Claimed` 任务
   都不能仅因 Turn 结束就被报告为完成。

失败任务已经归档的模型原图和中间结果仍保留在 Run 中，便于人工判断与后续新任务重试；任务状态和最终
`targetPaths` 仍是正式完成判据。

## 受管 HTTP MCP Server

桌面端默认使用：

```text
POST   http://127.0.0.1:43120/mcp
DELETE http://127.0.0.1:43120/mcp
GET    http://127.0.0.1:43120/health
```

HTTP Server 只允许 loopback 绑定和访问。WPF 每次启动生成新的 256 bit Base64Url Bearer Token：

- Token 只注入 MCP Server 与 Codex 子进程环境。
- Codex 配置通过 `bearer_token_env_var` 引用环境变量名，不把值写进命令日志。
- Token 不进入用户设置、项目文件、静态配置片段、`/health` 响应或运行日志。
- `POST /mcp` 与 `DELETE /mcp` 必须携带 `Authorization: Bearer <temporary-token>`。
- `GET /health` 不要求 Token，但仅供 loopback 健康检查，并且不返回认证信息。

HTTP 握手顺序：

1. 客户端向 `POST /mcp` 发送 `initialize`，首次请求不携带会话头。
2. 成功响应返回 `Mcp-Session-Id`。
3. 客户端携带相同会话头发送 `notifications/initialized`。
4. 后续 `tools/list`、`tools/call` 与 `DELETE /mcp` 均使用该会话头和 Bearer Token。

缺少或使用错误 Token 返回 `401 Unauthorized`。来自非 loopback 的请求返回 `403 Forbidden`。当前不提供
`GET /mcp` SSE 流；该方法返回 `405 Method Not Allowed`。HTTP MCP 会话按最近有效请求维持 90 秒活动
TTL，超时只表示协议工具连接失效，不会删除 Codex thread 或本地 `Pending` 任务。

健康响应示例：

```json
{
  "status": "connected",
  "endpoint": "http://127.0.0.1:43120/mcp",
  "activeSessions": 1,
  "totalHandshakes": 1,
  "lastHandshakeUtc": "2026-08-20T08:00:00+00:00",
  "lastClientName": "codex",
  "lastClientVersion": "1.0.0",
  "expectedAgent": "codex"
}
```

`--expected-agent` 只用于比较 `initialize.clientInfo.name` 并记录诊断偏差，不参与认证，也不会代替 Bearer
Token。

## 手动启动与外部 MCP 客户端

受管 Codex Runtime 不需要用户复制持久配置。以下方式只用于开发、协议诊断或其他 MCP 客户端。

开发环境可启动 HTTP 传输：

```powershell
$perspectiveForgeToken =
  [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
$env:GASSETFORGE_MCP_BEARER_TOKEN =
  $perspectiveForgeToken.TrimEnd('=').Replace('+', '-').Replace('/', '_')

dotnet run --project .\src\GAssetForge.McpServer\GAssetForge.McpServer.csproj -- `
  --transport http `
  --host 127.0.0.1 `
  --port 43120 `
  --expected-agent codex `
  --token-env GASSETFORGE_MCP_BEARER_TOKEN
```

正式发行可直接运行：

```powershell
GAssetForge.McpServer.exe `
  --transport http `
  --host 127.0.0.1 `
  --port 43120 `
  --expected-agent codex `
  --token-env GASSETFORGE_MCP_BEARER_TOKEN
```

HTTP 模式必须提供 `--token-env`，对应环境变量必须包含至少 32 个合法 Bearer 字符。测试或隔离环境可追加
`--data-root "D:\Temp\GAssetForge-Agent-Test"`，也可使用环境变量
`GASSETFORGE_DATA_ROOT`。未设置新变量时，独立 Server 仍兼容读取旧
`PERSPECTIVEFORGE_DATA_ROOT`；新旧同时存在时以新变量为准。不要通过代理、端口转发或防火墙规则把该
服务暴露到局域网或公网。

受管配置的新 MCP 名称是 `gasset_forge`。App Server 状态与 elicitation 处理在迁移期也识别旧
`perspective_forge`，但新配置、日志与复制指令只生成新名称。

不传参数时 MCP Server 使用 stdio：

```powershell
GAssetForge.McpServer.exe
```

也可显式传入 `--transport stdio`。stdio 使用 UTF-8、一行一条 JSON-RPC 2.0 消息；协议输出只写 stdout，
日志只写 stderr。它不要求 HTTP Bearer Token 或 `Mcp-Session-Id`，适合已有 MCP 客户端和自动化测试，但
同样不提供 Agent thread 持久化。

## MCP 工具

下面是 MCP Server 本身提供的完整工具集。受管 Codex Runtime 不会开放全部工具；其 `enabled_tools` 仅包含
`server_info`、`get_generation_task`、`claim_generation_task`、`submit_generation_output`、
`process_generation_output`、`complete_generation_task` 和 `fail_generation_task`。
`get_current_context`、`create_generation_task` 与 `list_generation_tasks` 只供外部 MCP 客户端、开发诊断或
未来明确授权的流程使用。

| MCP Tool | 用途 |
| --- | --- |
| `server_info` | 查看版本、数据目录、任务文件和有效 Assets 根目录 |
| `get_current_context` | 读取当前工作流、Tilemap 参数并生成正式约束包 |
| `create_generation_task` | 向本地持久化队列创建 AI 美术任务 |
| `list_generation_tasks` | 按状态或工作流查询任务 |
| `get_generation_task` | 获取指定任务及交付计划 |
| `claim_generation_task` | Agent 领取指定任务或下一个待处理任务 |
| `submit_generation_output` | 新合同：导入真实模型 PNG `sourcePath`，归档、归一化、剔色、校验、原子提交并自动完成单图任务 |
| `process_generation_output` | 旧兼容合同：按零基 `outputIndex` 把 staging 原图本地剔色并写入最终 target |
| `complete_generation_task` | 旧兼容合同：校验完整目标集合后完成多图或无 Run 任务 |
| `fail_generation_task` | 记录失败原因并结束任务 |

新任务的正常顺序：

1. 使用发布 prompt 中的明确 task ID 调用 `claim_generation_task(taskId, agentId)`，读取冻结 prompt、风格
   参考、底色合同、唯一目标路径、`delivery.workspace`、顶层的绝对 `recommendedSourcePath`，以及结构化
   `imageGeneration` CLI 合同。`imageGeneration.output` 明确给出最终 `targetWidth`、`targetHeight`、约分后的
   `targetAspectRatio`、符合 GPT Image 尺寸约束的非方形 `cliSize`，并标记是否精确保留画幅；
   `imageGeneration.style.referenceSourcePaths` 是经过数据根边界、重解析点和冻结 SHA-256 内容校验的风格快照绝对路径；
   快照缺失、被替换或被篡改时，认领会在任务状态写入前失败并保持 `Pending`。推荐源路径由软件从
   冻结的 `delivery.assetRoot + delivery.workspace.modelOutputPaths[0]` 计算并做 Assets 边界校验，供 CLI 兜底
   直接落盘；Agent 不得自行推断或改写。
2. 按 `preferredSkill` 调用真实图像模型，为这个单图任务取得本地 PNG `sourcePath`。CLI fallback 必须传
   `--size <imageGeneration.output.cliSize>`，并继续在提示中约束最终目标宽高与画幅，不能默认改成方形；存在
   风格参考图时使用 `edit` 并为每张路径重复 `--image`，只有无参考图时才使用 `generate`。不得用程序化绘图
   替代，也不得自行创建 Run、搬运、缩放、抠图或修改 Alpha。
3. 调用
   `submit_generation_output(taskId, agentId, outputIndex=0, sourcePath=<模型返回绝对路径>)`。
4. 软件归档源图、归一化冻结画布、补齐第一道纯色底、执行可选背景剔除、验证最终 Tilemap/PNG 合同、原子
   写入 `delivery.targetPaths[0]`，并自动把任务登记为 `Completed`。
5. 只有 ImageGen 或提交工具真实失败时才调用 `fail_generation_task`，并保留准确错误；单项失败不阻塞同批
   其它 task ID。

Agent Host 可以并发调度多个单图任务，但协议不规定固定子 Agent 名称、槽位或页面并行数。新任务必须满足
`output.count=1` 且 `outputIndex=0`；`submit_generation_output` 会拒绝旧多图合同。只有任务没有
`delivery.workspace` 时，Agent 才按历史顺序使用 `stagingPaths → process_generation_output →
complete_generation_task`。

### 背景剔除生成契约

背景剔除是客户端 Harness 的可选确定性工序。新任务把规则冻结在 `output.backgroundRemoval`；Agent 只需让
真实图像模型按 `colorHex` 生成均匀纯色底 PNG，然后把模型返回的绝对 `sourcePath` 交给
`submit_generation_output`。`parameters.delivery.stagingPaths[0]` 现在是软件管理的
`35-normalized-input` 文件，不再是 Agent 必须亲自写入的路径。

典型约束片段：

```json
{
  "output": {
    "width": 1024,
    "height": 1024,
    "count": 1,
    "maxParallelism": 1,
    "format": "png",
    "transparentBackground": true,
    "backgroundRemoval": {
      "schemaVersion": 1,
      "method": "border-connected-chroma-key",
      "preset": "magenta",
      "colorHex": "#FF00FF",
      "transparentThreshold": 30,
      "opaqueThreshold": 120,
      "edgeContract": 1,
      "edgeFeather": 0,
      "despill": true,
      "edgeConnectedOnly": true
    }
  },
  "parameters": {
    "delivery": {
      "assetRoot": "D:\\ExampleProject\\Assets",
      "outputDirectory": "Generated/Tilemap/0123456789abcdef0123456789abcdef/Final",
      "stagingPaths": [
        "Generated/GAssetForge/Runs/0123456789abcdef0123456789abcdef/35-normalized-input/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/ground.normalized.png"
      ],
      "targetPaths": [
        "Generated/Tilemap/0123456789abcdef0123456789abcdef/Final/ground.png"
      ],
      "workspace": {
        "schemaVersion": 1,
        "runId": "0123456789abcdef0123456789abcdef",
        "rootPath": "Generated/GAssetForge/Runs/0123456789abcdef0123456789abcdef",
        "manifestPath": "Generated/GAssetForge/Runs/0123456789abcdef0123456789abcdef/00-manifest/run.v1.json",
        "promptPath": "Generated/GAssetForge/Runs/0123456789abcdef0123456789abcdef/20-prompts/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.txt",
        "diagnosticsPath": "Generated/GAssetForge/Runs/0123456789abcdef0123456789abcdef/90-diagnostics/stages.v1.jsonl",
        "modelOutputPaths": [
          "Generated/GAssetForge/Runs/0123456789abcdef0123456789abcdef/30-model-output/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/ground.model.png"
        ],
        "processedPaths": [
          "Generated/GAssetForge/Runs/0123456789abcdef0123456789abcdef/40-background-removed/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/ground.transparent.png"
        ],
        "preserveIntermediates": true
      }
    }
  }
}
```

`preset` 必须使用规范小写名称；非自定义预设的 `colorHex` 必须与预设一致：

| preset | 中文 | 固定颜色 |
| --- | --- | --- |
| `magenta` | 洋红色（默认推荐） | `#FF00FF` |
| `green` | 绿色 | `#00FF00` |
| `blue` | 蓝色 | `#0000FF` |
| `black` | 黑色 | `#000000` |
| `white` | 白色 | `#FFFFFF` |
| `custom` | 自定义颜色 | 规范大写 `#RRGGBB` |

黑色和白色容易与主体阴影、高光或描边重叠；默认启用 `edgeConnectedOnly`，只删除与画布边缘连通的底色，
降低主体内部同色像素被误删的风险。`transparentThreshold` 与 `opaqueThreshold` 定义 RGB 色距的透明/不透明
过渡，后者必须大于前者；`edgeContract` 为 0–3 px，`edgeFeather` 为 0–2 px，`despill` 用于清理半透明
边缘残留的底色污染。

新单图输出流程：

```text
真实图像模型生成均匀纯色底原图
  → sourcePath
  → submit_generation_output(taskId, agentId, outputIndex=0, sourcePath)
  → 30-model-output：软件原样归档并记录 SHA-256
  → 35-normalized-input：contain-fit 到冻结画布、居中并补齐精确首道底色/边框
  → 40-background-removed：按 output.backgroundRemoval 本地剔色
  → 验证透明 PNG、Tilemap 几何与 Alpha 合同
  → 原子写入 parameters.delivery.targetPaths[0]
  → 自动完成任务登记，最终 target 才计入图片进度
```

`submit_generation_output` 只接受当前认领者提交的新单图任务，并要求 `output.count=1`、`outputIndex=0`、
有效 `delivery.workspace` 和真实 PNG。`sourcePath` 必须来自当前项目、Codex 目录、系统临时目录或 MCP 工作
目录；重解析点、空文件、超过 544 MiB、损坏或非 PNG 文件会被拒绝。项目 Run 中已经归档不同哈希的模型
原图时不会被静默覆盖，以保留审计链。

第一道归一化图会被调整到冻结宽高，模型尺寸不匹配时采用 contain-fit 并居中；启用剔色时使用冻结纯色填充
空白，并强制边框达到精确底色。随后本地处理仍校验底图不透明、边缘底色、透明阈值、去色边、收缩和羽化，
最终透明图才允许原子提交。Agent 不应自行使用 Pillow、ImageMagick、PowerShell 或其它工具重复这些步骤。

兼容合同保持不变：没有 `delivery.workspace` 的旧任务仍可由 Agent 写入旧 `.gassetforge-staging`，调用
`process_generation_output`，最后以完整有序 `targetPaths` 调用 `complete_generation_task`。该流程只用于
历史多图/无 Run 任务，不应套用到新任务。

### 独立背景剔除工具页

桌面端提供 ContentId 为 `background-removal` 的单实例 AvalonDock“背景剔除工具”页，可从“视图 → 背景剔除
工具”恢复，也可像属性、图层和项目面板一样停靠、浮动、调整尺寸或隐藏。素材生成步骤中的背景底色设置与
该工具页共享并持久化，因此生成契约和手动处理采用相同参数。

独立使用时可选择现有图片和透明 PNG 输出位置，并在原图/棋盘格透明结果之间并排预览。页面提供六种底色、
自定义 Hex、透明/不透明阈值、边缘连通模式、去色边、0–3 px 收缩和 0–2 px 羽化；本地处理保持原图尺寸，
输出完成后显示剔除像素与 Alpha 统计。它不调用 Agent，也不会把手动文件自动登记为某个生成任务产物。

## 日志与阶段耗时

生成链路有两套互补日志，不能只看任务的 `updatedAtUtc`：

| 日志 | 位置 | 用途 |
| --- | --- | --- |
| Workbench 会话日志 | `%LocalAppData%\GAssetForge\Desktop\Logs\workbench-*.jsonl` | 记录 UI 调度阶段、Agent Turn、工具/命令开始与完成、错误和会话级 `elapsedMilliseconds` |
| 项目 Run timeline | `<Assets>\Generated\GAssetForge\Runs\<batchId>\90-diagnostics\stages.v1.jsonl` | 记录任务与具体文件工序；随项目资产一起保留，便于跨会话审计 |

Workbench 结构化上下文可包含 `runId`、`batchId`、`taskId`、`outputIndex`、Agent thread/Turn/item、`stage`、
`status`、耗时、期望/观察路径和错误码。Reasoning 流不会逐条持久化；Agent 最终消息、工具、命令、item、
Turn 和错误事件会持久化，并执行敏感值脱敏。

Run timeline 是 append-only、best-effort JSONL。稳定阶段为：

- `workspace.prepare`
- `style.snapshot.project_copy`
- `task.claim`
- `model_output.import`
- `model_output.normalize`
- `output.process_and_commit`
- `task.complete` / `task.fail`

每个可计时阶段写入 `started` 与 `completed`/`failed` 事件，完成事件包含 `startedAtUtc`、`endedAtUtc` 和
`elapsedMilliseconds`。日志写入失败不会反向让已验证的生成任务失败；任务状态与最终文件仍是权威真相源。

指令日志页可执行：

```text
generation timeline
```

该命令显示当前批次、当前调度阶段及耗时、最长步骤、已落盘图片数和已验证图片数。任务租约续期会更新
`updatedAtUtc`，它只是心跳，不代表新图片或新阶段；判断卡点应优先使用 Agent 活动事件、Run timeline 和
最终路径出现时间。

## Tilemap 第一版约束

当前 Tilemap 规则包含：

- 精确输出画布宽高与 PNG 格式。
- 网格边长、A/B 边夹角和 A 边屏幕方向；新契约以屏幕 X 轴向右为 `0°`，正值顺时针。
- 未旋转投影尺寸、旋转后包围盒、两轴向量、两条边的绝对屏幕角度及像素密度。
- 控制角只定义两条相邻边的夹角，不代表整体朝向。`90°/0°` 明确表示画布轴对齐正方形：上下边水平、
  左右边垂直，禁止 Agent 或图像模型再次补偿 `45°` 并改写为 on-point diamond/rhombus。
- 内容必须落在瓦片有效轮廓内。
- 未启用 `output.backgroundRemoval` 的旧/直接透明任务，有效轮廓外必须直接生成真实 Alpha 0，禁止白底、纯色底
  或棋盘格伪透明。
- 启用背景剔除时，Agent 第一工序必须按冻结的 `colorHex` 生成均匀纯色底，画布边缘和有效轮廓外均保持该颜色，
  禁止渐变、纹理、背景投影或环境雾；本地处理后的最终 target 仍要求有效轮廓外真实 Alpha 0。
- 90°/0° 方形允许少量安全边框，但四个角部必须存在瓦片内容；内接的 45° 钻石形会被拒绝。
- 内容提示、美术风格和负向提示可由任务补充。

其他工作流仍使用可扩展的通用约束包，尚未因此获得已完成的专用图像生成实现。

## 持久化与安全边界

生成队列默认保存为：

```text
%LocalAppData%\GAssetForge\Desktop\Generation\generation-tasks.v1.json
```

任务状态为：

```text
Pending → Claimed → Completed
                  ↘ Failed
```

- 任务写入使用跨进程锁和同目录原子替换。
- 同一个任务只能由认领它的 Agent 完成或标记失败。
- 认领默认使用 30 分钟租约；任务操作会原子回收超时的 `Claimed` 任务为 `Pending`。
- 页面创建的任务冻结创建时的 `delivery.assetRoot`；之后切换项目不会改写旧任务的交付位置。
- 新任务是一图一任务，冻结唯一 `targetPaths[0]`、项目内 `workspace` 和 Run manifest；最终路径必须位于
  用户选择的当前项目 Assets 内。
- 项目内 Run 根为 `Generated/GAssetForge/Runs/<batchId>`；manifest、参考图、提示、模型原图、归一化
  图、剔色图和 timeline 均由软件创建并默认保留。
- 新背景剔除任务的 `delivery.stagingPaths[0]` 必须位于 Run 的 `35-normalized-input`；它由软件写入，不能与
  最终目标重名，也不计入正式产物进度。旧任务仍允许 `.gassetforge-staging`。
- 背景剔除任务在创建时即按 `width × height × 4` 校验 513 MiB 单图解码预算，避免创建必然无法后处理的任务。
- Tilemap 的 `parameters.background` 使用 `generationStage` / `finalStage` 分别描述“不透明纯色生成底图”和
  “真实透明最终 PNG”；校验器仍兼容旧任务的 `transparent=true` 结构。
- `submit_generation_output` 只接受认领者、单图 `outputIndex=0`、有效 workspace 和允许来源中的真实 PNG；
  原图导入采用哈希保护，归一化/剔色中间图和最终 target 均使用临时文件后原子移动。
- 新提交成功后自动调用任务完成状态迁移；重复提交已完成任务时幂等返回既有 target，不覆盖不同模型原图。
- `process_generation_output` 与 `complete_generation_task` 继续保留旧任务的多图、staging 和中断兜底能力，
  但不是新任务的正常执行链。
- PNG 校验覆盖签名、IHDR、精确宽高、真实 Alpha 色彩类型、CRC、连续 IDAT、IEND、尾随数据与完整像素解压。
- Tilemap 参数缺失、越界、派生几何不一致、有效轮廓外存在非零 Alpha，或 90°/0° 结果退化为内接 45°
  钻石形，都会拒绝完成。
- PNG 校验限制为 544 MiB 文件、513 MiB 解压像素、4096 个数据块、2048 个 IDAT，以及 16 MiB 非图像数据预算。
- 风格参考图先复制到 `Generation\ReferenceSnapshots\<batch-id>` 并记录 SHA-256，再镜像到项目 Run 的
  `10-style-reference`；每任务最多 128 张、单图最大 512 MB、总量最大 2 GB。
- Run timeline 仅作诊断且 best-effort；队列状态、manifest 冻结映射和最终 target 是权威状态。
- 路径穿越、磁盘根目录、符号链接和目录联接会被拒绝。
- HTTP 请求体上限为 4 MiB，`POST /mcp` 只接受 JSON Content-Type。

## 构建与发行文档来源

```powershell
.\scripts\publish-distribution-windows-x64.ps1
```

发行目录包含 `GAssetForge.exe` 与 `GAssetForge.McpServer.exe`。发布脚本将本文件
`docs\MCP.md` 直接复制为 `dist\GAssetForge-win-x64\MCP-README.md`，因此本文件是发行版 MCP
说明的唯一来源；不要直接手工维护 `dist` 中的副本。
