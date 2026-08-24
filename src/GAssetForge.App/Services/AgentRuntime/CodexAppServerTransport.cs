using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using GAssetForge.Core;

namespace GAssetForge.App.Services.AgentRuntime;

public sealed class CodexAppServerTransport : IAgentSessionTransport
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);
    private static readonly JsonElement EmptyObject = JsonSerializer.SerializeToElement(new { });
    private static readonly Regex GenerationTaskIdLinePattern = new(
        @"(?m)^-\s+(?<taskId>[0-9a-fA-F]{32})\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly HashSet<string> ApprovedGAssetForgeTools = new(
        StringComparer.Ordinal)
    {
        "claim_generation_task",
        "submit_generation_output",
        "process_generation_output",
        "complete_generation_task",
        "fail_generation_task"
    };

    private readonly IAgentProcessFactory _processFactory;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim _turnGate = new(1, 1);
    private readonly object _stateLock = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<AgentTurnResult>> _turnWaiters =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, AgentTurnResult> _earlyTurnCompletions =
        new(StringComparer.Ordinal);
    private IAgentChildProcess? _process;
    private JsonLineRpcConnection? _rpc;
    private CancellationTokenSource? _lifetimeCancellation;
    private AgentSessionStartOptions? _options;
    private AgentSessionTransportState _state = AgentSessionTransportState.Stopped;
    private string? _currentThreadId;
    private string? _activeTurnId;
    private string? _runtimeVersion;
    private Exception? _protocolFailure;
    private HashSet<string> _activeGenerationTaskIds = new(StringComparer.Ordinal);
    private bool _stopping;
    private bool _disposed;

    public CodexAppServerTransport() : this(SystemAgentProcessFactory.Instance)
    {
    }

    internal CodexAppServerTransport(IAgentProcessFactory processFactory)
    {
        _processFactory = processFactory ?? throw new ArgumentNullException(nameof(processFactory));
    }

    public string DisplayName => "Codex App Server";

    public AgentTransportCapabilities Capabilities =>
        AgentTransportCapabilities.PersistentProcess |
        AgentTransportCapabilities.AccountRead |
        AgentTransportCapabilities.ChatGptLogin |
        AgentTransportCapabilities.Logout |
        AgentTransportCapabilities.ExplicitThreadStart |
        AgentTransportCapabilities.ThreadResume |
        AgentTransportCapabilities.TurnExecution |
        AgentTransportCapabilities.TurnInterrupt |
        AgentTransportCapabilities.McpConfiguration;

    public AgentSessionTransportState State
    {
        get
        {
            lock (_stateLock)
            {
                return _state;
            }
        }
    }

    public bool IsRunning
    {
        get
        {
            lock (_stateLock)
            {
                try
                {
                    return _process is { HasExited: false };
                }
                catch
                {
                    return false;
                }
            }
        }
    }

    public string? RuntimeVersion
    {
        get
        {
            lock (_stateLock)
            {
                return _runtimeVersion;
            }
        }
    }

    public string? CurrentThreadId
    {
        get
        {
            lock (_stateLock)
            {
                return _currentThreadId;
            }
        }
    }

    public string? ActiveTurnId
    {
        get
        {
            lock (_stateLock)
            {
                return _activeTurnId;
            }
        }
    }

    public event EventHandler<AgentTransportStateChangedEventArgs>? StateChanged;

    public event EventHandler<AgentTransportLogEventArgs>? LogReceived;

    public event EventHandler<AgentNotificationEventArgs>? NotificationReceived;

    public event EventHandler<AgentTurnCompletedEventArgs>? TurnCompleted;

    public event EventHandler<AgentMcpStatusChangedEventArgs>? McpStatusChanged;

    public async Task StartAsync(
        AgentSessionStartOptions options,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(options);
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (IsRunning)
            {
                throw new InvalidOperationException("Codex app-server 已经在运行。");
            }

            SetState(AgentSessionTransportState.Starting, "正在启动 Codex app-server");
            _stopping = false;
            _options = options;
            var startInfo = CodexProcessLaunch.CreateBaseStartInfo(options);
            startInfo.ArgumentList.Add("app-server");
            startInfo.ArgumentList.Add("--stdio");
            CodexProcessLaunch.AddMcpConfiguration(startInfo, options);

            IAgentChildProcess? process = null;
            JsonLineRpcConnection? rpc = null;
            CancellationTokenSource? lifetimeCancellation = null;
            try
            {
                process = _processFactory.Start(startInfo);
                lifetimeCancellation = new CancellationTokenSource();
                rpc = new JsonLineRpcConnection(
                    process.StandardOutput,
                    process.StandardInput,
                    options.RequestTimeout);
                rpc.NotificationReceived += OnNotificationReceived;
                rpc.ProtocolWarning += message => WriteLog(AgentLogLevel.Warning, message);
                rpc.ServerRequestHandler = HandleServerRequestAsync;

                lock (_stateLock)
                {
                    _process = process;
                    _rpc = rpc;
                    _lifetimeCancellation = lifetimeCancellation;
                    _currentThreadId = null;
                    _activeTurnId = null;
                    _protocolFailure = null;
                }

                _ = MonitorProtocolAsync(rpc, lifetimeCancellation.Token);
                _ = MonitorStandardErrorAsync(process, lifetimeCancellation.Token);
                _ = MonitorExitAsync(process, rpc, lifetimeCancellation.Token);

                WriteLog(AgentLogLevel.Information, $"Codex app-server 已启动（PID {process.Id}）。");
                var initializeResult = await rpc.SendRequestAsync(
                        "initialize",
                        new
                        {
                            clientInfo = new
                            {
                                name = "GAssetForge",
                                title = "GAssetForge",
                                version = ProductIdentity.GetAssemblyInformationalVersion(
                                    typeof(CodexAppServerTransport).Assembly)
                            },
                            capabilities = new
                            {
                                experimentalApi = false,
                                requestAttestation = false
                            }
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
                ValidateRuntimeVersion(initializeResult);
                await rpc.SendNotificationAsync("initialized", null, cancellationToken).ConfigureAwait(false);
                SetState(AgentSessionTransportState.Ready, "Codex app-server 初始化完成");
                WriteLog(AgentLogLevel.Information, "Codex app-server 初始化完成。");
            }
            catch
            {
                if (rpc is not null)
                {
                    await rpc.DisposeAsync().ConfigureAwait(false);
                }

                lifetimeCancellation?.Cancel();
                lifetimeCancellation?.Dispose();
                if (process is not null)
                {
                    TryKill(process);
                    await process.DisposeAsync().ConfigureAwait(false);
                }

                ClearRuntimeReferences();
                SetState(AgentSessionTransportState.Faulted, "Codex app-server 启动失败");
                throw;
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_process is null)
            {
                SetState(AgentSessionTransportState.Stopped);
                return;
            }

            _stopping = true;
            SetState(AgentSessionTransportState.Stopping, "正在停止 Codex app-server");
            var process = _process;
            var rpc = _rpc;
            var lifetimeCancellation = _lifetimeCancellation;
            lifetimeCancellation?.Cancel();
            rpc?.Fail(new OperationCanceledException("Codex app-server 已停止。"));
            if (rpc is not null)
            {
                await rpc.DisposeAsync().ConfigureAwait(false);
            }

            TryKill(process);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(StopTimeout);
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                WriteLog(AgentLogLevel.Warning, "等待 Codex app-server 退出超时，已终止进程树。");
            }
            finally
            {
                await process.DisposeAsync().ConfigureAwait(false);
                lifetimeCancellation?.Dispose();
                ClearRuntimeReferences();
                FailTurnWaiters(new OperationCanceledException("Codex app-server 已停止。"));
            }

            SetState(AgentSessionTransportState.Stopped, "Codex app-server 已停止");
            WriteLog(AgentLogLevel.Information, "Codex app-server 已停止。");
        }
        finally
        {
            _stopping = false;
            _lifecycleGate.Release();
        }
    }

    public async Task<AgentAccountState> ReadAccountAsync(CancellationToken cancellationToken = default)
    {
        var result = await RequireRpc().SendRequestAsync(
                "account/read",
                EmptyObject,
                cancellationToken)
            .ConfigureAwait(false);
        var requiresOpenAiAuth = result.TryGetProperty("requiresOpenaiAuth", out var requiresElement) &&
                                 requiresElement.ValueKind == JsonValueKind.True;
        if (!result.TryGetProperty("account", out var account) || account.ValueKind == JsonValueKind.Null)
        {
            return new AgentAccountState(requiresOpenAiAuth, false, null, null, null);
        }

        return new AgentAccountState(
            requiresOpenAiAuth,
            true,
            ReadString(account, "type"),
            ReadString(account, "email"),
            ReadString(account, "planType"));
    }

    public async Task<AgentLoginResult> StartChatGptLoginAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await RequireRpc().SendRequestAsync(
                "account/login/start",
                new { type = "chatgptDeviceCode" },
                cancellationToken)
            .ConfigureAwait(false);
        var loginId = ReadRequiredString(result, "loginId", "Codex 登录响应缺少 loginId。");
        var loginType = ReadRequiredString(result, "type", "Codex 登录响应缺少 type。");
        var verificationUrl = ReadString(result, "verificationUrl") ?? ReadString(result, "authUrl");
        return new AgentLoginResult(
            loginId,
            loginType,
            Uri.TryCreate(verificationUrl, UriKind.Absolute, out var parsedUri) ? parsedUri : null,
            ReadString(result, "userCode"));
    }

    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        await RequireRpc().SendRequestAsync("account/logout", null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AgentThreadResult> StartThreadAsync(CancellationToken cancellationToken = default)
    {
        var options = RequireOptions();
        var result = await RequireRpc().SendRequestAsync(
                "thread/start",
                CreateThreadParameters(options, null),
                cancellationToken)
            .ConfigureAwait(false);
        var threadId = ExtractThreadId(result);
        lock (_stateLock)
        {
            _currentThreadId = threadId;
        }

        WriteLog(AgentLogLevel.Information, $"已创建 Codex 线程 {threadId}。");
        return new AgentThreadResult(threadId);
    }

    public async Task<AgentThreadResult> ResumeThreadAsync(
        string threadId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        var options = RequireOptions();
        var result = await RequireRpc().SendRequestAsync(
                "thread/resume",
                CreateThreadParameters(options, threadId),
                cancellationToken)
            .ConfigureAwait(false);
        var resumedThreadId = ExtractThreadId(result);
        lock (_stateLock)
        {
            _currentThreadId = resumedThreadId;
        }

        WriteLog(AgentLogLevel.Information, $"已恢复 Codex 线程 {resumedThreadId}。");
        return new AgentThreadResult(resumedThreadId);
    }

    public async Task<AgentTurnResult> StartTurnAsync(
        string prompt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        await _turnGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await StartTurnCoreAsync(prompt, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _turnGate.Release();
        }
    }

    private async Task<AgentTurnResult> StartTurnCoreAsync(
        string prompt,
        CancellationToken cancellationToken)
    {
        var threadId = CurrentThreadId;
        if (string.IsNullOrWhiteSpace(threadId))
        {
            threadId = (await StartThreadAsync(cancellationToken).ConfigureAwait(false)).ThreadId;
        }

        if (string.IsNullOrWhiteSpace(threadId))
        {
            throw new InvalidOperationException("Codex 线程尚未就绪。");
        }

        SetState(AgentSessionTransportState.RunningTurn, "Codex 正在执行素材生成任务");
        lock (_stateLock)
        {
            _activeGenerationTaskIds = ExtractGenerationTaskIds(prompt);
        }

        try
        {
            var result = await RequireRpc().SendRequestAsync(
                    "turn/start",
                    new
                    {
                        threadId,
                        input = new[]
                        {
                            new { type = "text", text = prompt }
                        }
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            if (!result.TryGetProperty("turn", out var turn))
            {
                throw new InvalidDataException("Codex turn/start 响应缺少 turn。");
            }

            var turnId = ReadRequiredString(turn, "id", "Codex turn/start 响应缺少 turn.id。");
            var waiter = _turnWaiters.GetOrAdd(
                turnId,
                _ => new TaskCompletionSource<AgentTurnResult>(
                    TaskCreationOptions.RunContinuationsAsynchronously));
            Exception? protocolFailure;
            lock (_stateLock)
            {
                _activeTurnId = turnId;
                protocolFailure = _protocolFailure;
            }

            if (protocolFailure is not null)
            {
                waiter.TrySetException(protocolFailure);
            }

            if (_earlyTurnCompletions.TryRemove(turnId, out var earlyCompletion))
            {
                waiter.TrySetResult(earlyCompletion);
            }

            WriteLog(AgentLogLevel.Information, $"Codex Turn {turnId} 已发布。");
            return await waiter.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await TryInterruptAfterCancellationAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            lock (_stateLock)
            {
                _activeTurnId = null;
                _activeGenerationTaskIds = new HashSet<string>(StringComparer.Ordinal);
            }

            if (IsRunning && !_stopping)
            {
                TryTransitionState(
                    AgentSessionTransportState.RunningTurn,
                    AgentSessionTransportState.Ready);
            }
        }
    }

    public async Task InterruptTurnAsync(CancellationToken cancellationToken = default)
    {
        var threadId = CurrentThreadId;
        var turnId = ActiveTurnId;
        if (string.IsNullOrWhiteSpace(threadId) || string.IsNullOrWhiteSpace(turnId))
        {
            return;
        }

        await RequireRpc().SendRequestAsync(
                "turn/interrupt",
                new { threadId, turnId },
                cancellationToken)
            .ConfigureAwait(false);
        WriteLog(AgentLogLevel.Warning, $"已请求中断 Codex Turn {turnId}。");
    }

    private async Task TryInterruptAfterCancellationAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await InterruptTurnAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            WriteLog(AgentLogLevel.Warning, $"取消后中断 Codex Turn 失败：{exception.Message}");
        }
    }

    private void OnNotificationReceived(string method, JsonElement parameters)
    {
        NotificationReceived?.Invoke(this, new AgentNotificationEventArgs(method, parameters));
        switch (method)
        {
            case "thread/started":
                if (parameters.TryGetProperty("thread", out var thread) &&
                    !string.IsNullOrWhiteSpace(ReadString(thread, "id")))
                {
                    lock (_stateLock)
                    {
                        _currentThreadId = ReadString(thread, "id");
                    }
                }

                break;
            case "turn/completed":
                HandleTurnCompleted(parameters);
                break;
            case "mcpServer/startupStatus/updated":
                HandleMcpStatus(parameters);
                break;
            case "account/login/completed":
                var succeeded = parameters.TryGetProperty("success", out var success) &&
                                success.ValueKind == JsonValueKind.True;
                WriteLog(
                    succeeded ? AgentLogLevel.Information : AgentLogLevel.Error,
                    succeeded
                        ? "Codex 账号登录完成。"
                        : $"Codex 账号登录失败：{ReadString(parameters, "error") ?? "未知错误"}");
                break;
            case "account/updated":
                WriteLog(AgentLogLevel.Information, "Codex 账号状态已更新。");
                break;
        }
    }

    private Task<object?> HandleServerRequestAsync(
        string method,
        JsonElement parameters,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(method, "mcpServer/elicitation/request", StringComparison.Ordinal))
        {
            throw new NotSupportedException($"GAssetForge 尚未处理 app-server 请求：{method}");
        }

        var decision = EvaluateGAssetForgeElicitation(parameters);
        WriteLog(
            decision.Accepted ? AgentLogLevel.Information : AgentLogLevel.Warning,
            decision.Accepted
                ? $"已允许当前批次的 GAssetForge MCP 调用：{decision.ToolName} · task {ShortId(decision.TaskId)}。"
                : $"已拒绝不符合当前批次授权范围的 MCP 调用：{decision.Reason}");

        object response = new Dictionary<string, object?>
        {
            ["action"] = decision.Accepted ? "accept" : "decline",
            ["content"] = decision.Accepted ? new Dictionary<string, object?>() : null,
            ["_meta"] = null
        };
        return Task.FromResult<object?>(response);
    }

    private McpElicitationDecision EvaluateGAssetForgeElicitation(JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object)
        {
            return McpElicitationDecision.Decline("请求参数不是对象");
        }

        var threadId = ReadString(parameters, "threadId") ?? ReadString(parameters, "thread_id");
        var turnId = ReadString(parameters, "turnId") ?? ReadString(parameters, "turn_id");
        var serverName = ReadString(parameters, "serverName") ?? ReadString(parameters, "server_name");
        var request = parameters.TryGetProperty("request", out var nestedRequest) &&
                      nestedRequest.ValueKind == JsonValueKind.Object
            ? nestedRequest
            : parameters;
        var mode = ReadString(request, "mode");
        var metadata = request.TryGetProperty("_meta", out var nestedMetadata)
            ? nestedMetadata
            : parameters.TryGetProperty("_meta", out var topLevelMetadata)
                ? topLevelMetadata
                : default;

        string? activeThreadId;
        string? activeTurnId;
        HashSet<string> activeTaskIds;
        AgentSessionTransportState state;
        lock (_stateLock)
        {
            activeThreadId = _currentThreadId;
            activeTurnId = _activeTurnId;
            activeTaskIds = new HashSet<string>(_activeGenerationTaskIds, StringComparer.Ordinal);
            state = _state;
        }

        if (state != AgentSessionTransportState.RunningTurn || string.IsNullOrWhiteSpace(activeTurnId))
        {
            return McpElicitationDecision.Decline("当前没有运行中的 Agent Turn");
        }

        if (!string.Equals(threadId, activeThreadId, StringComparison.Ordinal))
        {
            return McpElicitationDecision.Decline("threadId 与当前会话不一致");
        }

        if (!string.Equals(turnId, activeTurnId, StringComparison.Ordinal))
        {
            return McpElicitationDecision.Decline("turnId 与当前 Turn 不一致");
        }

        if (!ProductIdentity.IsKnownMcpServerName(serverName))
        {
            return McpElicitationDecision.Decline($"serverName 不是 {ProductIdentity.McpServerName}");
        }

        if (!string.Equals(mode, "form", StringComparison.Ordinal) &&
            !string.Equals(mode, "openai/form", StringComparison.Ordinal))
        {
            return McpElicitationDecision.Decline("仅允许 MCP 工具审批表单");
        }

        if (metadata.ValueKind != JsonValueKind.Object)
        {
            return McpElicitationDecision.Decline("缺少 MCP 工具审批元数据");
        }

        var approvalKind = ReadString(metadata, "codex_approval_kind") ??
                           ReadString(metadata, "codexApprovalKind");
        if (!string.Equals(approvalKind, "mcp_tool_call", StringComparison.Ordinal))
        {
            return McpElicitationDecision.Decline("审批类型不是 mcp_tool_call");
        }

        var toolName = ReadString(metadata, "tool_name") ?? ReadString(metadata, "toolName");
        if (string.IsNullOrWhiteSpace(toolName) || !ApprovedGAssetForgeTools.Contains(toolName))
        {
            return McpElicitationDecision.Decline("工具不在生成协议白名单", toolName);
        }

        var toolParameters = metadata.TryGetProperty("tool_params", out var snakeCaseParameters)
            ? snakeCaseParameters
            : metadata.TryGetProperty("toolParams", out var camelCaseParameters)
                ? camelCaseParameters
                : default;
        if (toolParameters.ValueKind != JsonValueKind.Object)
        {
            return McpElicitationDecision.Decline("缺少工具参数", toolName);
        }

        var taskId = ReadString(toolParameters, "taskId") ?? ReadString(toolParameters, "task_id");
        if (string.IsNullOrWhiteSpace(taskId) || !activeTaskIds.Contains(taskId))
        {
            return McpElicitationDecision.Decline("taskId 不属于当前批次", toolName, taskId);
        }

        var agentId = ReadString(toolParameters, "agentId") ?? ReadString(toolParameters, "agent_id");
        if (string.IsNullOrWhiteSpace(agentId) ||
            !agentId.StartsWith("codex", StringComparison.OrdinalIgnoreCase))
        {
            return McpElicitationDecision.Decline("agentId 不是 Codex 执行器", toolName, taskId);
        }

        return McpElicitationDecision.Accept(toolName, taskId);
    }

    private static HashSet<string> ExtractGenerationTaskIds(string prompt)
    {
        var taskIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in GenerationTaskIdLinePattern.Matches(prompt))
        {
            var taskId = match.Groups["taskId"].Value;
            if (!string.IsNullOrWhiteSpace(taskId))
            {
                taskIds.Add(taskId);
            }
        }

        return taskIds;
    }

    private static string ShortId(string? value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        return normalized.Length <= 12 ? normalized : normalized[..12];
    }

    private sealed record McpElicitationDecision(
        bool Accepted,
        string Reason,
        string? ToolName,
        string? TaskId)
    {
        internal static McpElicitationDecision Accept(string toolName, string taskId) =>
            new(true, string.Empty, toolName, taskId);

        internal static McpElicitationDecision Decline(
            string reason,
            string? toolName = null,
            string? taskId = null) =>
            new(false, reason, toolName, taskId);
    }

    private void HandleTurnCompleted(JsonElement parameters)
    {
        if (!parameters.TryGetProperty("turn", out var turn))
        {
            WriteLog(AgentLogLevel.Warning, "turn/completed 通知缺少 turn。");
            return;
        }

        var turnId = ReadString(turn, "id");
        if (string.IsNullOrWhiteSpace(turnId))
        {
            WriteLog(AgentLogLevel.Warning, "turn/completed 通知缺少 turn.id。");
            return;
        }

        var threadId = ReadString(parameters, "threadId") ?? CurrentThreadId;
        var status = ReadString(turn, "status") ?? "failed";
        string? error = null;
        if (turn.TryGetProperty("error", out var errorElement) && errorElement.ValueKind == JsonValueKind.Object)
        {
            error = ReadString(errorElement, "message");
        }

        var completion = new AgentTurnResult(threadId, turnId, status, error, turn.Clone());
        if (_turnWaiters.TryRemove(turnId, out var waiter))
        {
            waiter.TrySetResult(completion);
        }
        else
        {
            _earlyTurnCompletions[turnId] = completion;
        }

        lock (_stateLock)
        {
            if (string.Equals(_activeTurnId, turnId, StringComparison.Ordinal))
            {
                _activeTurnId = null;
            }

            if (!string.IsNullOrWhiteSpace(threadId))
            {
                _currentThreadId = threadId;
            }
        }

        TurnCompleted?.Invoke(this, new AgentTurnCompletedEventArgs(completion));
        WriteLog(
            completion.IsSuccess ? AgentLogLevel.Information : AgentLogLevel.Error,
            completion.IsSuccess
                ? $"Codex Turn {turnId} 已完成。"
                : $"Codex Turn {turnId} 结束：{status}；{error ?? "无错误详情"}");
    }

    private void HandleMcpStatus(JsonElement parameters)
    {
        var name = ReadString(parameters, "name") ?? "unknown";
        var status = ReadString(parameters, "status") ?? "unknown";
        var error = ReadString(parameters, "error");
        var threadId = ReadString(parameters, "threadId");
        McpStatusChanged?.Invoke(
            this,
            new AgentMcpStatusChangedEventArgs(name, status, error, threadId));
        WriteLog(
            string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase)
                ? AgentLogLevel.Error
                : AgentLogLevel.Information,
            $"Codex MCP {name}：{status}{(string.IsNullOrWhiteSpace(error) ? string.Empty : $"（{error}）")}");
    }

    private async Task MonitorProtocolAsync(JsonLineRpcConnection rpc, CancellationToken cancellationToken)
    {
        try
        {
            await rpc.RunReadLoopAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (!_stopping)
        {
            lock (_stateLock)
            {
                _protocolFailure = exception;
            }

            FailTurnWaiters(exception);
            WriteLog(AgentLogLevel.Error, $"Codex app-server 协议流已终止：{exception.Message}");
            SetState(AgentSessionTransportState.Faulted, exception.Message);
        }
    }

    private async Task MonitorStandardErrorAsync(
        IAgentChildProcess process,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await process.StandardError.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    return;
                }

                if (!string.IsNullOrWhiteSpace(line))
                {
                    WriteLog(AgentLogLevel.Warning, $"codex: {line}");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (!_stopping)
        {
            WriteLog(AgentLogLevel.Warning, $"读取 Codex stderr 失败：{exception.Message}");
        }
    }

    private async Task MonitorExitAsync(
        IAgentChildProcess process,
        JsonLineRpcConnection rpc,
        CancellationToken cancellationToken)
    {
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            if (_stopping)
            {
                return;
            }

            var exitCode = process.ExitCode;
            var exception = new InvalidOperationException($"Codex app-server 意外退出，退出码 {exitCode}。");
            rpc.Fail(exception);
            FailTurnWaiters(exception);
            SetState(AgentSessionTransportState.Faulted, exception.Message);
            WriteLog(AgentLogLevel.Error, exception.Message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void FailTurnWaiters(Exception exception)
    {
        foreach (var item in _turnWaiters.ToArray())
        {
            if (_turnWaiters.TryRemove(item.Key, out var waiter))
            {
                waiter.TrySetException(exception);
            }
        }

        _earlyTurnCompletions.Clear();
    }

    private JsonLineRpcConnection RequireRpc()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_stateLock)
        {
            return _rpc ?? throw new InvalidOperationException("Codex app-server 尚未启动。");
        }
    }

    private AgentSessionStartOptions RequireOptions() =>
        _options ?? throw new InvalidOperationException("Codex app-server 尚未配置。");

    private static object CreateThreadParameters(AgentSessionStartOptions options, string? threadId)
    {
        var parameters = new Dictionary<string, object?>
        {
            ["cwd"] = string.IsNullOrWhiteSpace(options.WorkingDirectory)
                ? Environment.CurrentDirectory
                : Path.GetFullPath(options.WorkingDirectory),
            ["sandbox"] = options.SandboxMode,
            ["approvalPolicy"] = options.ApprovalPolicy
        };
        if (!string.IsNullOrWhiteSpace(threadId))
        {
            parameters["threadId"] = threadId;
        }

        if (!string.IsNullOrWhiteSpace(options.Model))
        {
            parameters["model"] = options.Model;
        }

        if (!string.IsNullOrWhiteSpace(options.DeveloperInstructions))
        {
            parameters["developerInstructions"] = options.DeveloperInstructions;
        }

        return parameters;
    }

    private static string ExtractThreadId(JsonElement result)
    {
        if (!result.TryGetProperty("thread", out var thread))
        {
            throw new InvalidDataException("Codex thread 响应缺少 thread。");
        }

        return ReadRequiredString(thread, "id", "Codex thread 响应缺少 thread.id。");
    }

    private void ValidateRuntimeVersion(JsonElement initializeResult)
    {
        var userAgent = ReadString(initializeResult, "userAgent");
        if (string.IsNullOrWhiteSpace(userAgent))
        {
            WriteLog(AgentLogLevel.Warning, "Codex initialize 未提供 userAgent，无法验证协议版本。");
            return;
        }

        var match = Regex.Match(
            userAgent,
            @"(?<!\d)(?<major>\d+)\.(?<minor>\d+)\.(?<patch>\d+)(?!\d)",
            RegexOptions.CultureInvariant);
        if (!match.Success ||
            !int.TryParse(match.Groups["major"].Value, out var major) ||
            !int.TryParse(match.Groups["minor"].Value, out var minor) ||
            !int.TryParse(match.Groups["patch"].Value, out var patch))
        {
            WriteLog(
                AgentLogLevel.Warning,
                $"无法从 Codex userAgent 解析版本，将按宽松兼容模式运行：{userAgent}");
            return;
        }

        var version = new Version(major, minor, patch);
        lock (_stateLock)
        {
            _runtimeVersion = version.ToString();
        }

        if (major == 0 && minor < 139)
        {
            throw new NotSupportedException(
                $"Codex {version} 低于 GAssetForge 已验证的 app-server 基线 0.139.0。");
        }

        if (major != 0 || minor != 139)
        {
            WriteLog(
                AgentLogLevel.Warning,
                $"Codex {version} 高于当前已验证基线 0.139.x；将宽松接收新字段并继续运行。");
        }
        else
        {
            WriteLog(AgentLogLevel.Information, $"Codex app-server {version} 协议版本兼容。");
        }
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return property.ValueKind == JsonValueKind.String ? property.GetString() : property.ToString();
    }

    private static string ReadRequiredString(JsonElement element, string propertyName, string message) =>
        ReadString(element, propertyName) is { Length: > 0 } value
            ? value
            : throw new InvalidDataException(message);

    private void SetState(AgentSessionTransportState state, string? detail = null)
    {
        lock (_stateLock)
        {
            _state = state;
        }

        StateChanged?.Invoke(this, new AgentTransportStateChangedEventArgs(state, detail));
    }

    private bool TryTransitionState(
        AgentSessionTransportState expectedState,
        AgentSessionTransportState state,
        string? detail = null)
    {
        lock (_stateLock)
        {
            if (_state != expectedState)
            {
                return false;
            }

            _state = state;
        }

        StateChanged?.Invoke(this, new AgentTransportStateChangedEventArgs(state, detail));
        return true;
    }

    private void WriteLog(AgentLogLevel level, string message)
    {
        var safeMessage = CodexProcessLaunch.Redact(message, _options);
        LogReceived?.Invoke(
            this,
            new AgentTransportLogEventArgs(DateTimeOffset.Now, level, safeMessage));
    }

    private static void TryKill(IAgentChildProcess process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }

    private void ClearRuntimeReferences()
    {
        lock (_stateLock)
        {
            _process = null;
            _rpc = null;
            _lifetimeCancellation = null;
            _currentThreadId = null;
            _activeTurnId = null;
            _runtimeVersion = null;
            _protocolFailure = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await StopAsync().ConfigureAwait(false);
        _disposed = true;
        _lifecycleGate.Dispose();
        _turnGate.Dispose();
    }
}
