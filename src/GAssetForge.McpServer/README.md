# GAssetForge MCP Server

这是 GAssetForge 的本地 MCP 入口。它与桌面端共享
`%LocalAppData%\GAssetForge\Desktop`，用于让外部 Agent 读取当前美术工作流约束，并协作处理
持久化 AI 生图任务。它同时支持桌面端管理的 loopback Streamable HTTP 模式，以及已有客户端可继续使用
的 stdio 模式。

新配置使用 `gasset_forge`、`GASSETFORGE_DATA_ROOT` 与
`GASSETFORGE_MCP_BEARER_TOKEN`。迁移期仍可读取旧数据根环境变量；桌面端也能识别旧 MCP 状态别名，
但不会继续生成旧配置。

正式项目可以位于该全局数据根之外。桌面端会把当前项目的工作流、Tilemap 参数和 `<ProjectRoot>\Assets`
镜像到全局设置，供独立 MCP 进程读取；MCP 不直接把项目 manifest 当作跨进程协调文件。生成任务会冻结
创建时的 Assets 根，因此之后新建或恢复其他项目不会改写旧任务的交付位置。

## 桌面端入口

正式发行目录同时包含：

```text
GAssetForge.exe
GAssetForge.McpServer.exe
```

在桌面端选择 `MCP 接入 → 打开 Agent 连接面板`。ContentId 为 `mcp-agent-connection` 的 AvalonDock
单实例面板支持 Codex / OpenCode、端口设置、配置复制、服务器启停、握手状态、最近客户端与实时日志；
它可隐藏、浮动或重新停靠，位置与隐藏状态随工作区布局持久化。默认 Endpoint 为
`http://127.0.0.1:43120/mcp`；Agent 与有效端口保存到
`Settings\user-settings.json`。隐藏面板不会停止服务，应用退出会停止其启动的 Server 子进程。

## 发布

```powershell
dotnet publish GAssetForge.McpServer.csproj -p:PublishProfile=win-x64
```

发布配置会生成 Windows x64、自包含、不裁剪的单文件 `GAssetForge.McpServer.exe`。完整发行建议从
仓库根目录运行 `scripts\publish-distribution-windows-x64.ps1`，一次生成桌面端和 MCP Server。自动化测试可
使用 `--data-root <绝对路径>` 隔离数据。

## HTTP Agent 配置

Codex：

```toml
[mcp_servers.gasset_forge]
url = "http://127.0.0.1:43120/mcp"
tool_timeout_sec = 900
```

OpenCode：

```json
{
  "$schema": "https://opencode.ai/config.json",
  "mcp": {
    "gasset-forge": {
      "type": "remote",
      "url": "http://127.0.0.1:43120/mcp",
      "enabled": true
    }
  }
}
```

桌面连接面板只负责生成和复制 Endpoint 片段，不会自动改写 Agent 配置文件。静态片段不包含临时 Token；
桌面内的 AppServer transport 必须从 `McpServerProcessService` 读取当前 Token，并为 MCP 请求注入
`Authorization: Bearer ...`。

## stdio 兼容配置

将 `command` 替换为发布后 EXE 的真实绝对路径：

```json
{
  "mcpServers": {
    "gasset-forge": {
      "command": "C:\\Path\\To\\GAssetForge.McpServer.exe",
      "args": []
    }
  }
}
```

不传参数时使用 stdio；也可显式传 `--transport stdio`。该传输使用 JSON-RPC 2.0、UTF-8、一行一条
消息。标准输出只包含协议消息，运行日志只写标准错误。

## 本地 HTTP 模式

```powershell
$env:GASSETFORGE_MCP_BEARER_TOKEN = `
  [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))

GAssetForge.McpServer.exe `
  --transport http `
  --host 127.0.0.1 `
  --port 43120 `
  --expected-agent codex `
  --token-env GASSETFORGE_MCP_BEARER_TOKEN `
  --data-root "C:\Absolute\GAssetForgeData"
```

启动前必须把至少 32 个合法 Bearer 字符写入 `--token-env` 指定的进程环境变量。桌面端会自动生成
256 bit Base64Url 临时值并只注入 MCP 子进程；上例展示了直接启动时的临时生成方式。

HTTP 默认端口为 `43120`，`--expected-agent` 支持 `codex` 与 `opencode`，但只用于握手诊断。完整端点：

| 方法与路径 | 用途 |
| --- | --- |
| `POST /mcp` | MCP JSON-RPC 请求与通知 |
| `DELETE /mcp` | 关闭携带 `Mcp-Session-Id` 的会话 |
| `GET /health` | 运行状态、端点、会话数、握手数和最近客户端 |

所有 `POST /mcp` 与 `DELETE /mcp` 请求必须携带
`Authorization: Bearer <temporary-token>`。`initialize` 成功时响应头返回 `Mcp-Session-Id`，后续
`notifications/initialized`、工具请求及 `DELETE /mcp` 还必须携带该 Header。无返回值通知使用 HTTP `202`；普通 JSON-RPC 响应使用
`application/json`。完成 `notifications/initialized` 后，`/health` 状态才从 `waiting` 变为
`connected`。HTTP 会话按最近请求活动时间维护 90 秒 TTL；健康检查或新请求会清理过期会话，最后一个
已握手会话过期后状态会自动回到 `waiting`。`GET /mcp` 不提供 SSE，会返回 `405`。

桌面端每秒读取 `/health`，并把 Server stderr 送入连接面板日志。Agent 类型或端口改变时必须停止并
重新启动服务器，确保 `--expected-agent`、监听端口和展示的配置一致。

## 本机安全边界

- `--host` 只接受 `127.0.0.1`、`::1` 等 loopback 地址。
- HTTP 请求还会检查远端地址，非 loopback 客户端返回 `403`。
- `POST /mcp` 与 `DELETE /mcp` 缺少或使用错误 Bearer Token 时返回 `401`。
- `GET /health` 不要求 Token，但只接受 loopback 请求，且不返回 Token 或其环境变量名。
- `POST /mcp` 只接受 JSON Content-Type，请求体上限为 4 MiB。
- `--expected-agent` 只记录客户端类型偏差诊断，不参与授权或拒绝握手。
- 临时 Token 不写磁盘、不写日志；stdio 传输不要求 Token。
- 服务不应通过反向代理、端口转发或防火墙规则暴露到局域网或公网。
- 所有 HTTP 运行日志只写 stderr，不混入 JSON-RPC 响应。

## 工具

- `server_info`
- `get_current_context`
- `create_generation_task`
- `list_generation_tasks`
- `get_generation_task`
- `claim_generation_task`
- `submit_generation_output`
- `run_image_delivery_fallback`
- `process_generation_output`
- `complete_generation_task`
- `fail_generation_task`

受管 Codex worker 只开放 `server_info`、`get_generation_task`、`claim_generation_task`、
`submit_generation_output`、`run_image_delivery_fallback`、`process_generation_output`、`complete_generation_task` 和 `fail_generation_task`；
任务创建、列表与任意上下文读取不会暴露给这条自动 Turn。一次性 `--invoke-tool` 人工桥接使用相同 worker
白名单。

通用 MCP 客户端调用 `get_current_context` 时，应以新增的 `activeGenerationStep` 选择 Tilemap 或
`icon-design` 约束；`iconDesign` 返回规范化模式、画幅和相机参数，`generationConstraints` /
`generationMetrics` 与该步骤一致。`activeWorkflow` 和 `tilemap` 继续保留用于旧客户端兼容，但 Icon 不是项目
模板，新客户端不能只根据 `activeWorkflow` 判断生成步骤。

桌面“素材生成”页把数量展开为多个 `output.count=1` 的 Pending 任务，并冻结 `parameters.style`、
`parameters.delivery` 与项目内 `delivery.workspace`。Tilemap 任务冻结网格投影；Icon 任务还会冻结制作模式、
精确画幅、相机方位/仰角/滚转、物距、焦距、投影派生数据、生成角度描述和共用背景合同。Agent 对每个 task 只执行
`claim_generation_task → 真实内置 ImageGen → savedPath 有值时 submit_generation_output / savedPath 为空时 run_image_delivery_fallback`；任务间并发由 Agent Host 默认调度，
不再读取页面并行数。模型返回的 `sourcePath` 可以位于 Codex 临时目录，服务会原样归档到当前项目
`Assets/Generated/GAssetForge/Runs/<batchId>/30-model-output`，再写入 `35-normalized-input`、
`40-background-removed` 和冻结的最终 `targetPaths`，并自动完成任务登记。Assets 根作为输出位置时，正式结果分别
位于 `Generated/Tilemap/<batchId>/Final` 或 `Generated/Icon/<batchId>/Final`。

Art Workspace 的单区域 token 使用显式 `region-edit` 合同。`claim_generation_task` 返回唯一 base、mask、
像素范围和保留策略；Agent 直接调用 `run_image_delivery_fallback`，Harness 固定执行
`image_gen.py edit --image <base> --mask <mask>`；默认 `gpt-image-2` 对图片输入固有使用高保真模式，不能额外传入其明确拒绝的 `input-fidelity` 参数。不先调用普通整图 ImageGen，也不传入
历史画布图片。提交服务会归档模型原图、生成 mask 外差异诊断并用 base 强制合成；Final 的保留区必须逐像素
相同。大面积越界重绘返回 `output_region_edit_validation_failed` 的可重试普通结果，不登记 Completed。

若图片已生成、导入和完成中间处理，但 90°/0° Tilemap 前景主体几何未通过，
`submit_generation_output` 不抛 MCP tool error，而是正常返回
`outcome=retryable_validation_failure`。外层错误码固定为 `output_geometry_validation_failed`，子码区分
`tilemap_geometry_aspect_ratio_mismatch`、`tilemap_geometry_rotation_mismatch` 和
`tilemap_geometry_diamond_detected`，并返回包围盒、宽高比、角部占用、旋转估计、diamond score 与校验路径。
Agent 应记录 warning 并继续其它任务，不得再次调用 `fail_generation_task` 或包装成图片交付失败。桌面端默认
在初次生成后最多自动重做 3 次；每次创建新 task、新 batch、新 Run 和新路径，旧中间产物继续保留。
桌面端还会持久化是否请求应用内调度：Agent 重新 Ready 或应用重启后，仅恢复明确请求调度的 Pending 批次，
复用原 task/path 并补齐缺失的 prompt/manifest；同一 batch 不会重复启动 Turn。自动重做准备失败会转为可见的
人工接管状态，而不是无限显示“自动重做中”。

Agent 不得为新任务自行建目录、搬运、缩放、补底、抠图、修改 Alpha 或重复调用 process/complete。只有没有
`delivery.workspace` 的历史任务继续使用旧 `stagingPaths → process_generation_output → complete_generation_task`
兼容流程；最终 target 才计入产物进度。

服务会拒绝不存在、越界、经过符号链接/目录联接、路径不匹配的文件；PNG 还会校验签名、精确尺寸、真实
Alpha、CRC、连续 IDAT，并真实解压每行过滤器与像素数据。Tilemap 合同按 Core 构建器范围 fail-closed，
本地处理后的最终图片在瓦片有效轮廓外必须为 Alpha 0，内部必须含非全透明有效内容；90°/0° 还会分别校验
主体宽高比、边缘旋转和真实的内接菱形轮廓，角部缺失本身不会直接被归类为 45° 钻石形。校验另设 544 MiB 文件、
513 MiB 解压像素、4096 chunks、2048 IDAT 和 16 MiB 非图像数据预算，避免恶意资源消耗。
任务认领默认使用 30 分钟租约。List、Get、Claim、Process、Complete 或 Fail 访问任务时会把超时的
`Claimed` 原子回收为 `Pending` 并推进 revision；旧认领者必须重新认领后才能继续处理或提交终态，避免
Agent 异常退出后任务永久卡死。
桌面按钮只写入共享任务队列，不会反向启动 Agent。通用调用方仍可先调用 `get_current_context`，再自行创建任务。
