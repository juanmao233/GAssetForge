using System.Text.Json;

namespace GAssetForge.App.Services.AgentRuntime;

[Flags]
public enum AgentTransportCapabilities
{
    None = 0,
    PersistentProcess = 1 << 0,
    AccountRead = 1 << 1,
    ChatGptLogin = 1 << 2,
    Logout = 1 << 3,
    ExplicitThreadStart = 1 << 4,
    ThreadResume = 1 << 5,
    TurnExecution = 1 << 6,
    TurnInterrupt = 1 << 7,
    McpConfiguration = 1 << 8
}

public enum AgentSessionTransportState
{
    Stopped,
    Starting,
    Ready,
    RunningTurn,
    Stopping,
    Faulted
}

public enum AgentLogLevel
{
    Information,
    Warning,
    Error
}

public enum AgentActivityCategory
{
    TurnStarted,
    TurnCompleted,
    AgentMessage,
    Plan,
    Reasoning,
    ToolStarted,
    ToolCompleted,
    CommandStarted,
    CommandCompleted,
    ItemStarted,
    ItemCompleted,
    Warning,
    Error
}

public sealed record AgentSessionStartOptions
{
    public string ExecutablePath { get; init; } = "codex";

    public string? WorkingDirectory { get; init; }

    public Uri? McpEndpoint { get; init; }

    public string? McpBearerTokenEnvironmentVariableName { get; init; }

    public string? McpBearerToken { get; init; }

    public IReadOnlyDictionary<string, string?> EnvironmentVariables { get; init; } =
        new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

    public string? Model { get; init; }

    public string SandboxMode { get; init; } = "workspace-write";

    public string ApprovalPolicy { get; init; } = "never";

    public bool AllowSandboxNetworkAccess { get; init; }

    public string? DeveloperInstructions { get; init; }

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public bool SkipGitRepositoryCheck { get; init; } = true;
}

public sealed record AgentAccountState(
    bool RequiresOpenAiAuth,
    bool IsAuthenticated,
    string? AccountType,
    string? Email,
    string? PlanType);

public sealed record AgentLoginResult(
    string LoginId,
    string LoginType,
    Uri? VerificationUri,
    string? UserCode);

public sealed record AgentThreadResult(string? ThreadId, bool IsDeferred = false);

public sealed record AgentTurnResult(
    string? ThreadId,
    string? TurnId,
    string Status,
    string? ErrorMessage,
    JsonElement? RawTurn = null)
{
    public bool IsSuccess => string.Equals(Status, "completed", StringComparison.OrdinalIgnoreCase);
}

public sealed class AgentRpcException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}

public sealed class AgentMcpStartupException(string message) : Exception(message);

public sealed class AgentTransportStateChangedEventArgs(
    AgentSessionTransportState state,
    string? detail = null) : EventArgs
{
    public AgentSessionTransportState State { get; } = state;

    public string? Detail { get; } = detail;
}

public sealed class AgentTransportLogEventArgs(
    DateTimeOffset timestamp,
    AgentLogLevel level,
    string message) : EventArgs
{
    public DateTimeOffset Timestamp { get; } = timestamp;

    public AgentLogLevel Level { get; } = level;

    public string Message { get; } = message;
}

public sealed class AgentActivityEventArgs(
    DateTimeOffset timestamp,
    AgentActivityCategory category,
    string message,
    bool isWarning,
    string? threadId = null,
    string? turnId = null,
    string? itemId = null,
    string? itemType = null,
    string? taskId = null,
    string? eventName = null,
    bool isTerminal = false) : EventArgs
{
    public DateTimeOffset Timestamp { get; } = timestamp;

    public AgentActivityCategory Category { get; } = category;

    public string Message { get; } = message;

    public bool IsWarning { get; } = isWarning;

    public string? ThreadId { get; } = threadId;

    public string? TurnId { get; } = turnId;

    public string? ItemId { get; } = itemId;

    public string? ItemType { get; } = itemType;

    public string? TaskId { get; } = taskId;

    public string? EventName { get; } = eventName;

    public bool IsTerminal { get; } = isTerminal;
}

public sealed class AgentNotificationEventArgs(string method, JsonElement parameters) : EventArgs
{
    public string Method { get; } = method;

    public JsonElement Parameters { get; } = parameters;
}

public sealed class AgentTurnCompletedEventArgs(AgentTurnResult result) : EventArgs
{
    public AgentTurnResult Result { get; } = result;
}

public sealed class AgentMcpStatusChangedEventArgs(
    string serverName,
    string status,
    string? error,
    string? threadId) : EventArgs
{
    public string ServerName { get; } = serverName;

    public string Status { get; } = status;

    public string? Error { get; } = error;

    public string? ThreadId { get; } = threadId;
}

public interface IAgentSessionTransport : IAsyncDisposable
{
    string DisplayName { get; }

    AgentTransportCapabilities Capabilities { get; }

    AgentSessionTransportState State { get; }

    bool IsRunning { get; }

    string? RuntimeVersion { get; }

    string? CurrentThreadId { get; }

    string? ActiveTurnId { get; }

    event EventHandler<AgentTransportStateChangedEventArgs>? StateChanged;

    event EventHandler<AgentTransportLogEventArgs>? LogReceived;

    event EventHandler<AgentNotificationEventArgs>? NotificationReceived;

    event EventHandler<AgentTurnCompletedEventArgs>? TurnCompleted;

    event EventHandler<AgentMcpStatusChangedEventArgs>? McpStatusChanged;

    Task StartAsync(AgentSessionStartOptions options, CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);

    Task<AgentAccountState> ReadAccountAsync(CancellationToken cancellationToken = default);

    Task<AgentLoginResult> StartChatGptLoginAsync(CancellationToken cancellationToken = default);

    Task LogoutAsync(CancellationToken cancellationToken = default);

    Task<AgentThreadResult> StartThreadAsync(CancellationToken cancellationToken = default);

    Task<AgentThreadResult> ResumeThreadAsync(
        string threadId,
        CancellationToken cancellationToken = default);

    Task<AgentTurnResult> StartTurnAsync(
        string prompt,
        CancellationToken cancellationToken = default);

    Task InterruptTurnAsync(CancellationToken cancellationToken = default);
}
