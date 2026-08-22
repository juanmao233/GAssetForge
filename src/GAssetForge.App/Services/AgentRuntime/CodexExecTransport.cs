using System.Diagnostics;
using System.IO;
using System.Text.Json;
using GAssetForge.Core;

namespace GAssetForge.App.Services.AgentRuntime;

public sealed class CodexExecTransport : IAgentSessionTransport
{
    private readonly IAgentProcessFactory _processFactory;
    private readonly SemaphoreSlim _turnGate = new(1, 1);
    private readonly object _stateLock = new();
    private AgentSessionStartOptions? _options;
    private IAgentChildProcess? _activeProcess;
    private AgentSessionTransportState _state = AgentSessionTransportState.Stopped;
    private string? _currentThreadId;
    private string? _activeTurnId;
    private bool _started;
    private bool _disposed;

    public CodexExecTransport() : this(SystemAgentProcessFactory.Instance)
    {
    }

    internal CodexExecTransport(IAgentProcessFactory processFactory)
    {
        _processFactory = processFactory ?? throw new ArgumentNullException(nameof(processFactory));
    }

    public string DisplayName => "Codex Exec (fallback)";

    public AgentTransportCapabilities Capabilities =>
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
                return _started;
            }
        }
    }

    public string? RuntimeVersion => null;

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

    public Task StartAsync(
        AgentSessionStartOptions options,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        _ = CodexProcessLaunch.CreateBaseStartInfo(options);

        lock (_stateLock)
        {
            if (_activeProcess is not null)
            {
                throw new InvalidOperationException("Codex exec 正在执行 Turn，不能重新配置。");
            }

            _options = options;
            _started = true;
        }

        SetState(AgentSessionTransportState.Ready, "Codex exec 降级通道已配置");
        WriteLog(AgentLogLevel.Warning, "已启用 Codex exec JSONL 降级通道；账号管理与常驻握手不可用。");
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IAgentChildProcess? process;
        lock (_stateLock)
        {
            process = _activeProcess;
            _started = false;
        }

        SetState(AgentSessionTransportState.Stopping);
        if (process is not null)
        {
            TryKill(process);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }
        }

        lock (_stateLock)
        {
            _activeProcess = null;
            _activeTurnId = null;
        }

        SetState(AgentSessionTransportState.Stopped, "Codex exec 已停止");
    }

    public Task<AgentAccountState> ReadAccountAsync(CancellationToken cancellationToken = default) =>
        Task.FromException<AgentAccountState>(Unsupported("读取账号"));

    public Task<AgentLoginResult> StartChatGptLoginAsync(CancellationToken cancellationToken = default) =>
        Task.FromException<AgentLoginResult>(Unsupported("ChatGPT 登录"));

    public Task LogoutAsync(CancellationToken cancellationToken = default) =>
        Task.FromException(Unsupported("账号登出"));

    public Task<AgentThreadResult> StartThreadAsync(CancellationToken cancellationToken = default)
    {
        EnsureStarted();
        cancellationToken.ThrowIfCancellationRequested();
        lock (_stateLock)
        {
            _currentThreadId = null;
        }

        return Task.FromResult(new AgentThreadResult(null, IsDeferred: true));
    }

    public Task<AgentThreadResult> ResumeThreadAsync(
        string threadId,
        CancellationToken cancellationToken = default)
    {
        EnsureStarted();
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_stateLock)
        {
            _currentThreadId = threadId;
        }

        WriteLog(AgentLogLevel.Information, $"下一个 Codex exec Turn 将恢复线程 {threadId}。");
        return Task.FromResult(new AgentThreadResult(threadId));
    }

    public async Task<AgentTurnResult> StartTurnAsync(
        string prompt,
        CancellationToken cancellationToken = default)
    {
        EnsureStarted();
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        await _turnGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureStarted();
            var options = _options!;
            var resumedThreadId = CurrentThreadId;
            var localTurnId = $"exec-{Guid.NewGuid():N}";
            var startInfo = CreateExecStartInfo(options, resumedThreadId);
            var process = _processFactory.Start(startInfo);
            lock (_stateLock)
            {
                _activeProcess = process;
                _activeTurnId = localTurnId;
            }

            SetState(AgentSessionTransportState.RunningTurn, "Codex exec 正在执行素材生成任务");
            if (options.McpEndpoint is not null)
            {
                McpStatusChanged?.Invoke(
                    this,
                    new AgentMcpStatusChangedEventArgs(
                        ProductIdentity.McpServerName,
                        "starting",
                        null,
                        resumedThreadId));
            }

            WriteLog(
                AgentLogLevel.Information,
                string.IsNullOrWhiteSpace(resumedThreadId)
                    ? $"Codex exec 已启动新线程 Turn（PID {process.Id}）。"
                    : $"Codex exec 已恢复线程 {resumedThreadId}（PID {process.Id}）。");

            using var killRegistration = cancellationToken.Register(() => TryKill(process));
            var stderrTask = ReadStandardErrorAsync(process, cancellationToken);
            var stdoutTask = ReadJsonEventsAsync(process, localTurnId, cancellationToken);
            try
            {
                await process.StandardInput.WriteAsync(prompt.AsMemory(), cancellationToken).ConfigureAwait(false);
                await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
                process.StandardInput.Dispose();
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                var parsed = await stdoutTask.ConfigureAwait(false);
                await stderrTask.ConfigureAwait(false);

                var status = parsed.Status ?? (process.ExitCode == 0 ? "completed" : "failed");
                var error = parsed.ErrorMessage;
                if (process.ExitCode != 0 && string.IsNullOrWhiteSpace(error))
                {
                    error = $"codex exec 退出码 {process.ExitCode}。";
                }

                var completion = new AgentTurnResult(
                    parsed.ThreadId ?? resumedThreadId,
                    parsed.TurnId ?? localTurnId,
                    status,
                    error,
                    parsed.RawTurn);
                if (!string.IsNullOrWhiteSpace(completion.ThreadId))
                {
                    lock (_stateLock)
                    {
                        _currentThreadId = completion.ThreadId;
                    }
                }

                TurnCompleted?.Invoke(this, new AgentTurnCompletedEventArgs(completion));
                if (options.McpEndpoint is not null)
                {
                    McpStatusChanged?.Invoke(
                        this,
                        new AgentMcpStatusChangedEventArgs(
                            ProductIdentity.McpServerName,
                            completion.IsSuccess ? "ready" : "failed",
                            completion.ErrorMessage,
                            completion.ThreadId));
                }

                WriteLog(
                    completion.IsSuccess ? AgentLogLevel.Information : AgentLogLevel.Error,
                    completion.IsSuccess
                        ? $"Codex exec Turn {completion.TurnId} 已完成。"
                        : $"Codex exec Turn 失败：{completion.ErrorMessage ?? completion.Status}");
                return completion;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                WriteLog(AgentLogLevel.Warning, "Codex exec Turn 已取消并终止进程树。");
                throw;
            }
            finally
            {
                TryKill(process);
                await process.DisposeAsync().ConfigureAwait(false);
                lock (_stateLock)
                {
                    if (ReferenceEquals(_activeProcess, process))
                    {
                        _activeProcess = null;
                        _activeTurnId = null;
                    }
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            SetState(AgentSessionTransportState.Faulted, exception.Message);
            WriteLog(AgentLogLevel.Error, $"Codex exec 执行失败：{exception.Message}");
            throw;
        }
        finally
        {
            if (IsRunning)
            {
                SetState(AgentSessionTransportState.Ready);
            }

            _turnGate.Release();
        }
    }

    public Task InterruptTurnAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IAgentChildProcess? process;
        lock (_stateLock)
        {
            process = _activeProcess;
        }

        if (process is not null)
        {
            TryKill(process);
            WriteLog(AgentLogLevel.Warning, "Codex exec Turn 已通过终止进程树中断。");
        }

        return Task.CompletedTask;
    }

    private static ProcessStartInfo CreateExecStartInfo(
        AgentSessionStartOptions options,
        string? resumeThreadId)
    {
        var startInfo = CodexProcessLaunch.CreateBaseStartInfo(options);
        startInfo.ArgumentList.Add("exec");
        if (!string.IsNullOrWhiteSpace(resumeThreadId))
        {
            startInfo.ArgumentList.Add("resume");
        }

        startInfo.ArgumentList.Add("--json");
        if (options.SkipGitRepositoryCheck)
        {
            startInfo.ArgumentList.Add("--skip-git-repo-check");
        }

        if (!string.IsNullOrWhiteSpace(options.Model))
        {
            startInfo.ArgumentList.Add("-m");
            startInfo.ArgumentList.Add(options.Model);
        }

        if (string.IsNullOrWhiteSpace(resumeThreadId))
        {
            startInfo.ArgumentList.Add("-s");
            startInfo.ArgumentList.Add(options.SandboxMode);
        }

        CodexProcessLaunch.AddConfig(
            startInfo,
            $"approval_policy={CodexProcessLaunch.TomlString(options.ApprovalPolicy)}");
        CodexProcessLaunch.AddMcpConfiguration(startInfo, options);
        if (!string.IsNullOrWhiteSpace(resumeThreadId))
        {
            startInfo.ArgumentList.Add(resumeThreadId);
        }

        startInfo.ArgumentList.Add("-");
        return startInfo;
    }

    private async Task<ExecJsonResult> ReadJsonEventsAsync(
        IAgentChildProcess process,
        string localTurnId,
        CancellationToken cancellationToken)
    {
        string? threadId = CurrentThreadId;
        string? turnId = localTurnId;
        string? status = null;
        string? errorMessage = null;
        JsonElement? rawTurn = null;
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                var eventType = ReadString(root, "type") ?? string.Empty;
                NotificationReceived?.Invoke(
                    this,
                    new AgentNotificationEventArgs(eventType, root.Clone()));
                switch (eventType)
                {
                    case "thread.started":
                        threadId = ReadString(root, "thread_id") ?? ReadString(root, "threadId") ?? threadId;
                        if (!string.IsNullOrWhiteSpace(threadId))
                        {
                            lock (_stateLock)
                            {
                                _currentThreadId = threadId;
                            }
                        }

                        break;
                    case "turn.started":
                        turnId = ReadString(root, "turn_id") ?? ReadString(root, "turnId") ?? turnId;
                        break;
                    case "turn.completed":
                        status = "completed";
                        rawTurn = root.Clone();
                        break;
                    case "turn.failed":
                        status = "failed";
                        errorMessage = ReadErrorMessage(root);
                        rawTurn = root.Clone();
                        break;
                    case "error":
                        status = "failed";
                        errorMessage = ReadErrorMessage(root);
                        rawTurn = root.Clone();
                        break;
                }
            }
            catch (JsonException exception)
            {
                WriteLog(AgentLogLevel.Warning, $"忽略无效的 codex exec JSONL：{exception.Message}");
            }
        }

        return new ExecJsonResult(threadId, turnId, status, errorMessage, rawTurn);
    }

    private async Task ReadStandardErrorAsync(
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
    }

    private void EnsureStarted()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_stateLock)
        {
            if (!_started || _options is null)
            {
                throw new InvalidOperationException("Codex exec 降级通道尚未配置。");
            }
        }
    }

    private static NotSupportedException Unsupported(string operation) =>
        new($"Codex exec 降级通道不支持{operation}，请改用 Codex app-server。");

    private static string? ReadString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return property.ValueKind == JsonValueKind.String ? property.GetString() : property.ToString();
    }

    private static string? ReadErrorMessage(JsonElement element)
    {
        if (ReadString(element, "message") is { Length: > 0 } message)
        {
            return message;
        }

        if (element.TryGetProperty("error", out var error))
        {
            return error.ValueKind == JsonValueKind.String
                ? error.GetString()
                : ReadString(error, "message") ?? error.ToString();
        }

        return null;
    }

    private void SetState(AgentSessionTransportState state, string? detail = null)
    {
        lock (_stateLock)
        {
            _state = state;
        }

        StateChanged?.Invoke(this, new AgentTransportStateChangedEventArgs(state, detail));
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

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await StopAsync().ConfigureAwait(false);
        _disposed = true;
        _turnGate.Dispose();
    }

    private sealed record ExecJsonResult(
        string? ThreadId,
        string? TurnId,
        string? Status,
        string? ErrorMessage,
        JsonElement? RawTurn);
}
