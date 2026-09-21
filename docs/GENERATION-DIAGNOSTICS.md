# 生成诊断与排错

## 如何提供可查询的错误

美术对话失败信息包含批次、任务编号。打开“指令日志”，按编号或错误码搜索；选中日志行后
按 Ctrl+C，详情列会复制完整诊断。日志目录按钮打开当前会话 JSONL 所在位置。
清除日志显示不会删除已经写入的会话日志，也不会删除生成任务或产物。

用户数据目录使用 `WorkspacePaths` 解析，旧安装可能仍在
`%LOCALAPPDATA%/PerspectiveForge/Desktop/Logs`；以界面显示的日志路径为准。

## 错误类别

| 错误码 | 含义 | 处理方式 |
| --- | --- | --- |
| `agent_session_not_running` | 会话未运行，Turn 尚未提交 | 启动或重新连接 Agent |
| `agent_auth_required` | 账号认证尚未确认 | 完成登录并刷新认证 |
| `mcp_server_unavailable` | 本地 MCP 未运行或不健康 | 检查本地服务并重新连接 |
| `mcp_endpoint_mismatch` | 服务端口与配置不一致 | 按当前配置重新连接 |
| `agent_project_mismatch` | Agent 与当前项目不匹配 | 为当前项目重新连接，不能跨项目提交 |
| `agent_mcp_ready_timeout` | 当前线程 MCP 初始化超过等待上限 | 检查该线程初始化日志，恢复后继续原任务 |
| `agent_mcp_failed` / `agent_mcp_cancelled` | MCP 初始化失败或被取消 | 查看原始 MCP 错误；不要盲目修改创作指令 |
| `agent_thread_start_cancelled` | 创建线程期间取消，远端结果未知 | 显式重新连接；软件不会自动再建线程 |
| `agent_turn_submission_failed` | Turn 提交没有成功确认 | 检查连接与原始错误，保留原任务 |
| `agent_turn_execution_failed` | 已确认的 Turn 执行失败 | 查看执行阶段错误与持久任务状态 |
| `image_generation_failed` 等任务错误码 | Agent 已调用生成链，但任务失败 | 按任务实际阶段排查模型网络、交付或校验 |
| `LOG_FILE_UNAVAILABLE` | 日志文件不可写，已降级为内存日志 | 先复制诊断，再检查磁盘空间／权限；重启恢复文件记录 |

## 日志合同

JSONL `schemaVersion=2` 保留原有字段，新增 `recordedAt`。`timestamp` 是事件发生时间，
`recordedAt` 是日志总线写入时间，`sequence` 是日志接收序号；异步通知不保证这三者的排序一致。
复盘竞态时应同时查看发生时间、线程和阶段，不要仅凭界面行顺序判断先后。

`context` 包含已有的 Run/Batch/Task/Thread/Turn 编号，并增加：

- `ErrorCode`：稳定失败类别，不再把 `WaitingForTaskClaim` 之类进度名当错误码。
- `FailureStage`：实际失败操作；`Stage` 仍表示界面进度状态。
- `ReadinessState`：失败时的运行、认证、MCP 等状态摘要。
- `RecoveryHint`：与错误类别对应的建议。
- `ExceptionType`：异常类型；`details` 保留原始异常及调用栈，经过脱敏。

启动日志还记录实际进程路径、软件版本和程序集构建编号，方便确认启动的是哪个测试包。

诊断在失败发生处记录，不依赖 WPF 界面回调执行时机。注册凭据及常见 Authorization/API key
赋值在日志总线脱敏；复制出口使用同一份已脱敏记录。日志可能仍包含项目名称与本地路径，
公开分享前应自行检查这些非凭据信息。

## 验证范围

回归覆盖线程迟到／早到就绪、旧周期误放行、无关插件、超时、取消、停止、错误阶段、持久
Pending 不改写、界面回调延迟、日志文件故障及真实表格复制。测试不调用付费绘图模型，
也不证明上游图片服务始终可用。最终成功仍以持久任务 Completed 与冻结目标文件为准。
