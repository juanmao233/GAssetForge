namespace GAssetForge.App.Services.AgentRuntime;

// Each thread/start has its own readiness cycle. Notifications may arrive before
// the RPC response provides its thread ID, so bind before interpreting them.
internal sealed class AgentMcpReadinessTracker
{
    internal sealed record Failure(string Code, string Message, string? ThreadId);
    private sealed record Status(string Value, string? Error);
    private sealed class Cycle(bool allowUnscoped)
    {
        public bool AllowUnscoped { get; } = allowUnscoped;
        public bool IsBinding { get; set; } = true;
        public bool IsStopped { get; set; }
        public string? ThreadId { get; set; }
        public string State { get; set; } = "waiting";
        public Dictionary<string, Status> EarlyStatuses { get; } = new(StringComparer.Ordinal);
        public Status? EarlyUnscopedStatus { get; set; }
        public TaskCompletionSource<Failure?> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly object _sync = new();
    private Cycle _current = new(allowUnscoped: true);

    public bool IsReady { get { lock (_sync) { return _current.State == "ready"; } } }
    public string State { get { lock (_sync) { return _current.State; } } }
    public string? ThreadId { get { lock (_sync) { return _current.ThreadId; } } }

    public object Begin(bool allowUnscoped)
    {
        lock (_sync)
        {
            _current.Completion.TrySetResult(new Failure(
                "agent_readiness_replaced", "MCP 就绪等待已被新的会话或线程替换。", _current.ThreadId));
            _current = new Cycle(allowUnscoped);
            return _current;
        }
    }

    public void Bind(object cycle, string? threadId)
    {
        lock (_sync)
        {
            if (!ReferenceEquals(cycle, _current) || _current.IsStopped || !_current.IsBinding)
            {
                return;
            }

            BindCore(threadId);
        }
    }

    public void BindInitialThread(string? threadId)
    {
        lock (_sync)
        {
            if (!string.IsNullOrWhiteSpace(threadId) &&
                _current.IsBinding && _current.AllowUnscoped && !_current.IsStopped)
            {
                BindCore(threadId);
            }
        }
    }

    private void BindCore(string? threadId)
    {
        _current.ThreadId = threadId;
        _current.IsBinding = false;
        Status? status = null;
        if (threadId is not null)
        {
            _current.EarlyStatuses.TryGetValue(threadId, out status);
        }

        status ??= _current.AllowUnscoped ? _current.EarlyUnscopedStatus : null;
        _current.EarlyStatuses.Clear();
        _current.EarlyUnscopedStatus = null;
        if (status is not null)
        {
            Apply(status);
        }
    }

    public bool Observe(string? threadId, string status, string? error)
    {
        lock (_sync)
        {
            if (_current.IsStopped)
            {
                return false;
            }

            var update = new Status(status.ToLowerInvariant(), error);
            if (_current.IsBinding)
            {
                if (threadId is not null)
                {
                    if (_current.EarlyStatuses.Count < 32 || _current.EarlyStatuses.ContainsKey(threadId))
                    {
                        _current.EarlyStatuses[threadId] = update;
                    }
                }
                else if (_current.AllowUnscoped)
                {
                    _current.EarlyUnscopedStatus = update;
                }

                return false;
            }

            if (threadId is null ? !_current.AllowUnscoped :
                !string.Equals(threadId, _current.ThreadId, StringComparison.Ordinal))
            {
                return false;
            }

            Apply(update);
            return true;
        }
    }

    private void Apply(Status status)
    {
        if (status.Value is not ("starting" or "ready" or "failed" or "cancelled" or "canceled"))
        {
            return;
        }

        if (_current.Completion.Task.IsCompleted && _current.State != status.Value)
        {
            _current = new Cycle(_current.AllowUnscoped)
            {
                ThreadId = _current.ThreadId,
                IsBinding = false
            };
        }

        _current.State = status.Value;
        if (status.Value == "ready")
        {
            _current.Completion.TrySetResult(null);
        }
        else if (status.Value == "failed")
        {
            _current.Completion.TrySetResult(new Failure(
                "agent_mcp_failed", status.Error ?? "当前线程的 GAssetForge MCP 初始化失败。", _current.ThreadId));
        }
        else if (status.Value is "cancelled" or "canceled")
        {
            _current.Completion.TrySetResult(new Failure(
                "agent_mcp_cancelled", status.Error ?? "当前线程的 GAssetForge MCP 初始化被取消。", _current.ThreadId));
        }
    }

    public void Stop(string code, string message)
    {
        lock (_sync)
        {
            if (_current.IsStopped)
            {
                return;
            }

            if (_current.Completion.Task.IsCompleted)
            {
                _current = new Cycle(_current.AllowUnscoped) { ThreadId = _current.ThreadId };
            }

            _current.IsStopped = true;
            _current.State = "stopped";
            _current.Completion.TrySetResult(new Failure(code, message, _current.ThreadId));
        }
    }

    public async Task<Failure?> WaitAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Cycle cycle;
            lock (_sync)
            {
                cycle = _current;
            }

            var failure = await cycle.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            lock (_sync)
            {
                if (failure is not null || ReferenceEquals(cycle, _current))
                {
                    return failure;
                }
            }
        }
    }
}
