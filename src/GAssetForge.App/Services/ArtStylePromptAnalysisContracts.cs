using GAssetForge.App.Services.AgentRuntime;

namespace GAssetForge.App.Services;

public sealed record ArtStylePromptAnalysisReference
{
    public string Id { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string SourcePath { get; init; } = string.Empty;

    public string Sha256 { get; init; } = string.Empty;

    public string MediaType { get; init; } = string.Empty;
}

public sealed record ArtStylePromptAnalysisInput
{
    public string GroupId { get; init; } = string.Empty;

    public string GroupName { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public string ExistingPrompt { get; init; } = string.Empty;

    public string SourceFingerprint { get; init; } = string.Empty;

    public ArtStylePromptAnalysisReference[] References { get; init; } = [];
}

public sealed record ArtStylePromptAnalysisResult
{
    public string UnifiedPrompt { get; init; } = string.Empty;

    public string Summary { get; init; } = string.Empty;

    public string Analyzer { get; init; } = string.Empty;

    public string AnalyzerVersion { get; init; } = string.Empty;

    public string RequestId { get; init; } = string.Empty;

    public string GroupId { get; init; } = string.Empty;

    public string SourceFingerprint { get; init; } = string.Empty;

    public DateTimeOffset AnalyzedAtUtc { get; init; }

    public string RequestFilePath { get; init; } = string.Empty;

    public string ResultFilePath { get; init; } = string.Empty;

    public string DiagnosticsDirectory { get; init; } = string.Empty;
}

public interface IArtStylePromptAnalysisService
{
    Task<ArtStylePromptAnalysisResult> AnalyzeAsync(
        ArtStylePromptAnalysisInput input,
        CancellationToken cancellationToken = default);
}

public sealed record ArtStylePromptAgentTurnResult(
    string Status,
    string? ThreadId = null,
    string? TurnId = null,
    string? ErrorMessage = null)
{
    public bool IsSuccess => string.Equals(Status, "completed", StringComparison.OrdinalIgnoreCase);

    internal static ArtStylePromptAgentTurnResult FromAgentTurn(AgentTurnResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new ArtStylePromptAgentTurnResult(
            result.Status,
            result.ThreadId,
            result.TurnId,
            result.ErrorMessage);
    }
}

public interface IArtStylePromptAgentClient
{
    bool IsConnected { get; }

    string DisplayName { get; }

    string WorkingDirectory { get; }

    Task<ArtStylePromptAgentTurnResult> RunTurnAsync(
        string prompt,
        CancellationToken cancellationToken = default);
}
