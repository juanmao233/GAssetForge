using GAssetForge.App.Services.AgentRuntime;
using GAssetForge.App.ViewModels;
using System.ComponentModel;
using System.Windows.Threading;

namespace GAssetForge.App.Services;

internal interface IPageUiAgentRuntimeConnection
{
    event EventHandler<AgentActivityEventArgs>? AgentActivityReceived;
    event PropertyChangedEventHandler? PropertyChanged;

    string AgentDisplayName { get; }
    bool IsConnected { get; }
    bool IsTurnRunning { get; }
    string StatusText { get; }
    string TurnStatusText { get; }
    string RuntimeModeText { get; }
    string McpToolStatusText { get; }

    Task<AgentTurnResult> RunTurnAsync(string prompt, CancellationToken cancellationToken);
}

/// <summary>
/// Reuses the Shell-owned Agent connection for Page UI conversations without taking ownership of
/// the Codex Runtime or MCP Server. Agent text is buffered by TurnId and is only exposed after the
/// completed result identifies the exact Turn that belongs to this request.
/// </summary>
public sealed class ProductionPageUiAgentConversationGateway :
    IPageUiAgentConversationGateway,
    IDisposable
{
    private const int MaximumBufferedAgentUpdates = 512;

    private readonly IPageUiAgentRuntimeConnection _connection;
    private readonly Dispatcher _dispatcher;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private int _isRunning;
    private int _disposed;

    public ProductionPageUiAgentConversationGateway(AgentConnectionViewModel connection)
        : this(new AgentConnectionAdapter(connection))
    {
    }

    internal ProductionPageUiAgentConversationGateway(IPageUiAgentRuntimeConnection connection)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _dispatcher = Dispatcher.CurrentDispatcher;
        _connection.PropertyChanged += OnConnectionPropertyChanged;
    }

    public event EventHandler? StateChanged;

    public PageUiAgentConversationState GetState()
    {
        var displayName = string.IsNullOrWhiteSpace(_connection.AgentDisplayName)
            ? "Agent"
            : _connection.AgentDisplayName.Trim();
        var isConnected = Volatile.Read(ref _disposed) == 0 && _connection.IsConnected;
        var isBusy = Volatile.Read(ref _isRunning) != 0 || _connection.IsTurnRunning;
        if (!isConnected)
        {
            return new PageUiAgentConversationState(
                false,
                isBusy,
                displayName,
                "Agent 尚未接入",
                _connection.StatusText);
        }

        if (isBusy)
        {
            return new PageUiAgentConversationState(
                true,
                true,
                displayName,
                $"{displayName} 正在执行",
                _connection.TurnStatusText);
        }

        return new PageUiAgentConversationState(
            true,
            false,
            displayName,
            $"{displayName} 已接入可用",
            $"{_connection.RuntimeModeText} · {_connection.McpToolStatusText}");
    }

    public async Task<PageUiAgentConversationResult> RunTurnAsync(
        string prompt,
        Action<string> onAgentTextChanged,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        ArgumentNullException.ThrowIfNull(onAgentTextChanged);

        if (Interlocked.CompareExchange(ref _isRunning, 1, 0) != 0)
        {
            return Failure("页面UI制作已有一轮 Agent 对话正在执行，请等待当前回复完成。");
        }

        RaiseStateChanged();
        var capturedUpdates = new List<CapturedAgentUpdate>();
        var captureLock = new object();
        void CaptureAgentMessage(object? sender, AgentActivityEventArgs eventArgs)
        {
            if (eventArgs.Category != AgentActivityCategory.AgentMessage ||
                string.IsNullOrWhiteSpace(eventArgs.TurnId) ||
                string.IsNullOrWhiteSpace(eventArgs.Message))
            {
                return;
            }

            lock (captureLock)
            {
                capturedUpdates.Add(new CapturedAgentUpdate(
                    eventArgs.ThreadId,
                    eventArgs.TurnId,
                    eventArgs.ItemId,
                    eventArgs.Message.Trim()));
                if (capturedUpdates.Count > MaximumBufferedAgentUpdates)
                {
                    capturedUpdates.RemoveRange(
                        0,
                        capturedUpdates.Count - MaximumBufferedAgentUpdates);
                }
            }
        }

        _connection.AgentActivityReceived += CaptureAgentMessage;
        try
        {
            if (!_connection.IsConnected)
            {
                return Failure(
                    "Agent 尚未达到可执行状态。请先在 MCP Agent 接入页启动会话并完成认证与 MCP 初始化。");
            }

            if (_connection.IsTurnRunning)
            {
                return Failure("Agent 正在执行其他任务，请等待当前 Turn 完成后再发送页面 UI 指令。");
            }

            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _lifetimeCancellation.Token);
            var result = await _connection.RunTurnAsync(prompt, linkedCancellation.Token)
                .ConfigureAwait(false);

            // AgentConnection marshals activities to the WPF Dispatcher. Waiting for a lower
            // priority sentinel ensures all AgentMessage notifications queued before
            // turn/completed have reached the capture buffer before it is filtered by TurnId.
            await DrainAgentActivityQueueAsync(linkedCancellation.Token).ConfigureAwait(false);

            CapturedAgentUpdate[] currentTurnUpdates;
            lock (captureLock)
            {
                currentTurnUpdates = string.IsNullOrWhiteSpace(result.TurnId)
                    ? []
                    : capturedUpdates
                        .Where(update =>
                            string.Equals(update.TurnId, result.TurnId, StringComparison.Ordinal) &&
                            (string.IsNullOrWhiteSpace(result.ThreadId) ||
                             string.IsNullOrWhiteSpace(update.ThreadId) ||
                             string.Equals(update.ThreadId, result.ThreadId, StringComparison.Ordinal)))
                        .ToArray();
            }

            var lastReportedText = string.Empty;
            foreach (var update in currentTurnUpdates)
            {
                if (string.Equals(lastReportedText, update.Text, StringComparison.Ordinal))
                {
                    continue;
                }

                lastReportedText = update.Text;
                TryReportAgentText(onAgentTextChanged, update.Text);
            }

            var agentText = currentTurnUpdates.LastOrDefault()?.Text ?? string.Empty;
            if (!result.IsSuccess)
            {
                return new PageUiAgentConversationResult(
                    false,
                    agentText,
                    string.IsNullOrWhiteSpace(result.ErrorMessage)
                        ? $"Agent Turn 未完成：{result.Status}"
                        : result.ErrorMessage.Trim());
            }

            return new PageUiAgentConversationResult(true, agentText, null);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested &&
                                                 !cancellationToken.IsCancellationRequested)
        {
            return Failure("页面UI制作正在关闭，当前 Agent 对话已取消。");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Failure(exception.Message);
        }
        finally
        {
            _connection.AgentActivityReceived -= CaptureAgentMessage;
            Interlocked.Exchange(ref _isRunning, 0);
            RaiseStateChanged();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _connection.PropertyChanged -= OnConnectionPropertyChanged;
        _lifetimeCancellation.Cancel();
        _lifetimeCancellation.Dispose();
    }

    private async Task DrainAgentActivityQueueAsync(CancellationToken cancellationToken)
    {
        if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
        {
            return;
        }

        if (_dispatcher.CheckAccess())
        {
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            return;
        }

        await _dispatcher.InvokeAsync(
                static () => { },
                DispatcherPriority.ContextIdle,
                cancellationToken)
            .Task
            .ConfigureAwait(false);
    }

    private void OnConnectionPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName is nameof(AgentConnectionViewModel.IsConnected) or
            nameof(AgentConnectionViewModel.IsTurnRunning) or
            nameof(AgentConnectionViewModel.StatusText) or
            nameof(AgentConnectionViewModel.TurnStatusText) or
            nameof(AgentConnectionViewModel.RuntimeModeText) or
            nameof(AgentConnectionViewModel.McpToolStatusText) or
            nameof(AgentConnectionViewModel.SelectedAgent))
        {
            RaiseStateChanged();
        }
    }

    private void RaiseStateChanged()
    {
        if (Volatile.Read(ref _disposed) == 0)
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private static void TryReportAgentText(Action<string> callback, string text)
    {
        try
        {
            callback(text);
        }
        catch
        {
            // UI projection failures must not change the outcome of an already completed Turn.
        }
    }

    private static PageUiAgentConversationResult Failure(string message) =>
        new(false, string.Empty, string.IsNullOrWhiteSpace(message) ? "Agent 对话执行失败。" : message.Trim());

    private sealed record CapturedAgentUpdate(
        string? ThreadId,
        string TurnId,
        string? ItemId,
        string Text);

    private sealed class AgentConnectionAdapter : IPageUiAgentRuntimeConnection
    {
        private readonly AgentConnectionViewModel _connection;

        public AgentConnectionAdapter(AgentConnectionViewModel connection)
        {
            _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        }

        public event EventHandler<AgentActivityEventArgs>? AgentActivityReceived
        {
            add => _connection.AgentActivityReceived += value;
            remove => _connection.AgentActivityReceived -= value;
        }

        public event PropertyChangedEventHandler? PropertyChanged
        {
            add => _connection.PropertyChanged += value;
            remove => _connection.PropertyChanged -= value;
        }

        public string AgentDisplayName => _connection.SelectedAgent.DisplayName;
        public bool IsConnected => _connection.IsConnected;
        public bool IsTurnRunning => _connection.IsTurnRunning;
        public string StatusText => _connection.StatusText;
        public string TurnStatusText => _connection.TurnStatusText;
        public string RuntimeModeText => _connection.RuntimeModeText;
        public string McpToolStatusText => _connection.McpToolStatusText;

        public Task<AgentTurnResult> RunTurnAsync(string prompt, CancellationToken cancellationToken) =>
            _connection.RunTurnAsync(prompt, cancellationToken);
    }
}
