namespace GAssetForge.App.Services.AgentRuntime;

public sealed class AgentReadinessException : InvalidOperationException
{
    public AgentReadinessException(
        string code,
        string stage,
        string message,
        string? threadId,
        string stateSummary,
        string recoveryHint,
        Exception? innerException = null)
        : base($"[{code}] {message} {recoveryHint}", innerException)
    {
        Code = code;
        Stage = stage;
        ThreadId = threadId;
        StateSummary = stateSummary;
        RecoveryHint = recoveryHint;
        OccurredAt = DateTimeOffset.Now;
    }

    public string Code { get; }
    public string Stage { get; }
    public string? ThreadId { get; }
    public string StateSummary { get; }
    public string RecoveryHint { get; }
    public DateTimeOffset OccurredAt { get; }
}
