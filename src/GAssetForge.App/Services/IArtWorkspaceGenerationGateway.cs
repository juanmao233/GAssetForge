using GAssetForge.Core.Generation;

namespace GAssetForge.App.Services;

public sealed record ArtWorkspaceGenerationOutputFrameSnapshot
{
    public string Name { get; init; } = string.Empty;

    public double X { get; init; }

    public double Y { get; init; }

    public double Width { get; init; }

    public double Height { get; init; }

    public int PixelWidth { get; init; }

    public int PixelHeight { get; init; }

    public string BackgroundHex { get; init; } = "#20242C";

    public string OutputRelativeDirectory { get; init; } = string.Empty;

    public string OutputDirectoryPath { get; init; } = string.Empty;
}

public sealed record ArtWorkspaceGenerationReferenceSnapshot
{
    public string ImageId { get; init; } = string.Empty;

    public string AssetId { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string AbsolutePath { get; init; } = string.Empty;

    public string Sha256 { get; init; } = string.Empty;

    public string MediaType { get; init; } = string.Empty;

    public int PixelWidth { get; init; }

    public int PixelHeight { get; init; }

    public double X { get; init; }

    public double Y { get; init; }

    public double Width { get; init; }

    public double Height { get; init; }

    public double RotationDegrees { get; init; }

    public double Opacity { get; init; } = 1d;

    public int ZIndex { get; init; }
}

public sealed record ArtWorkspaceGenerationRequest
{
    public string RequestId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAtUtc { get; init; }

    public string ProjectRoot { get; init; } = string.Empty;

    public string AssetRoot { get; init; } = string.Empty;

    public string Instruction { get; init; } = string.Empty;

    public ArtWorkspaceGenerationOutputFrameSnapshot OutputFrame { get; init; } = new();

    public ArtWorkspaceGenerationReferenceSnapshot[] VisibleReferences { get; init; } = [];

    public ArtWorkspaceGenerationInstructionSegment[] InstructionSegments { get; init; } = [];

    /// <summary>
    /// Legacy single-image side channel retained for requests saved before inline instruction
    /// tokens were introduced. New requests should use <see cref="InstructionSegments"/> only.
    /// </summary>
    public ArtWorkspaceGenerationInstructionReference? InstructionReference { get; init; }
}

public sealed record ArtWorkspaceGenerationProgress
{
    public string State { get; init; } = "running";

    public string Message { get; init; } = string.Empty;

    public string? AgentDescription { get; init; }

    public double? Percentage { get; init; }
}

public sealed record ArtWorkspaceGenerationResult
{
    public bool IsSuccess { get; init; }

    /// <summary>
    /// The current UI wait ended after dispatch was interrupted, while the durable task and frozen
    /// target remain owned by the gateway for a later resume. This is not a terminal task failure.
    /// </summary>
    public bool IsDeferred { get; init; }

    public string AgentDescription { get; init; } = string.Empty;

    public string[] OutputPaths { get; init; } = [];

    public string? ErrorMessage { get; init; }

    public string? BatchId { get; init; }

    public string? TaskId { get; init; }
}

public sealed class ArtWorkspaceDeferredGenerationCompletedEventArgs(
    ArtWorkspaceGenerationRequest request,
    ArtWorkspaceGenerationResult result) : EventArgs
{
    public ArtWorkspaceGenerationRequest Request { get; } =
        request ?? throw new ArgumentNullException(nameof(request));

    public ArtWorkspaceGenerationResult Result { get; } =
        result ?? throw new ArgumentNullException(nameof(result));
}

public interface IArtWorkspaceGenerationGateway
{
    bool IsConnected { get; }

    bool IsBusy { get; }

    string AgentDisplayName { get; }

    string StatusText { get; }

    event EventHandler? StateChanged;

    /// <summary>
    /// Raised when a durable task whose original UI waiter was released after an interruption later
    /// reaches a real terminal state in the same application process.
    /// </summary>
    event EventHandler<ArtWorkspaceDeferredGenerationCompletedEventArgs>? DeferredGenerationCompleted;

    Task<ArtWorkspaceGenerationResult> GenerateAsync(
        ArtWorkspaceGenerationRequest frozenRequest,
        IProgress<ArtWorkspaceGenerationProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
