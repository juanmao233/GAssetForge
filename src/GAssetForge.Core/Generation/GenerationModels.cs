using System.Text.Json.Nodes;

namespace GAssetForge.Core.Generation;

public enum GenerationTaskStatus
{
    Pending,
    Claimed,
    Completed,
    Failed
}

/// <summary>
/// A provider-neutral, workflow-specific package that an image-generation agent can consume.
/// </summary>
public sealed record GenerationConstraintPackage
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public string WorkflowId { get; init; } = string.Empty;

    public string? PreferredSkill { get; init; }

    public string Prompt { get; init; } = string.Empty;

    public string? NegativePrompt { get; init; }

    public GenerationOutputSpec Output { get; init; } = new();

    public GenerationConstraintRule[] Rules { get; init; } = [];

    /// <summary>
    /// Workflow-owned structured data. New workflows can extend this object without changing the queue schema.
    /// </summary>
    public JsonObject Parameters { get; init; } = [];
}

public sealed record GenerationOutputSpec
{
    public int Width { get; init; } = 1024;

    public int Height { get; init; } = 1024;

    public int Count { get; init; } = 1;

    /// <summary>
    /// Legacy compatibility ceiling for older persisted tasks. New desktop batches publish one
    /// output per task and leave task scheduling to the Agent host instead of exposing a user
    /// controlled image-parallelism knob.
    /// </summary>
    public int MaxParallelism { get; init; } = 1;

    public string Format { get; init; } = "png";

    public bool TransparentBackground { get; init; }

    /// <summary>
    /// Optional first-stage chroma-key contract. When present, the agent writes an opaque source
    /// image to parameters.delivery.stagingPaths and GAssetForge produces the final alpha PNG.
    /// </summary>
    public GenerationBackgroundRemovalSpec? BackgroundRemoval { get; init; }
}

public sealed record GenerationBackgroundRemovalSpec
{
    public const int CurrentSchemaVersion = 1;
    public const string BorderConnectedChromaKeyMethod = "border-connected-chroma-key";

    /// <summary>
    /// ImageSharp expands the staging image to one RGBA byte quartet per pixel before removal.
    /// Task creation and MCP post-processing must enforce this same per-image contract budget.
    /// </summary>
    public const int DecodedRgbaBytesPerPixel = 4;

    public const long MaximumDecodedRgbaBytes = 513L * 1024L * 1024L;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public string Method { get; init; } = BorderConnectedChromaKeyMethod;

    public string Preset { get; init; } = "magenta";

    public string ColorHex { get; init; } = "#FF00FF";

    public double TransparentThreshold { get; init; } = 30d;

    public double OpaqueThreshold { get; init; } = 120d;

    public int EdgeContract { get; init; } = 1;

    public int EdgeFeather { get; init; }

    public bool Despill { get; init; } = true;

    public bool EdgeConnectedOnly { get; init; } = true;
}

public sealed record GenerationConstraintRule
{
    public string Key { get; init; } = string.Empty;

    public string Category { get; init; } = "general";

    public string Instruction { get; init; } = string.Empty;

    public bool Required { get; init; } = true;

    public double? Weight { get; init; }
}

public sealed record CreateGenerationTaskRequest
{
    public string DisplayName { get; init; } = string.Empty;

    public string? RequestedBy { get; init; }

    public GenerationConstraintPackage Constraints { get; init; } = new();
}

public sealed record CreateGenerationBatchItemRequest
{
    public string ItemId { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public GenerationConstraintPackage Constraints { get; init; } = new();
}

public sealed record CreateGenerationBatchRequest
{
    public string BatchId { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public string? RequestedBy { get; init; }

    public CreateGenerationBatchItemRequest[] Items { get; init; } = [];
}

public sealed record GenerationTask
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public string Id { get; init; } = string.Empty;

    public int Revision { get; init; }

    public string DisplayName { get; init; } = string.Empty;

    public string? RequestedBy { get; init; }

    /// <summary>
    /// Optional desktop submission batch identity. Legacy and independently-created tasks leave this null.
    /// </summary>
    public string? BatchId { get; init; }

    /// <summary>
    /// Stable logical row identity inside <see cref="BatchId"/>.
    /// </summary>
    public string? BatchItemId { get; init; }

    /// <summary>
    /// Zero-based display and execution order inside <see cref="BatchId"/>.
    /// </summary>
    public int? BatchItemIndex { get; init; }

    public GenerationTaskStatus Status { get; init; }

    public GenerationConstraintPackage Constraints { get; init; } = new();

    public string? ClaimedBy { get; init; }

    public DateTimeOffset CreatedAtUtc { get; init; }

    public DateTimeOffset UpdatedAtUtc { get; init; }

    public DateTimeOffset? ClaimedAtUtc { get; init; }

    public DateTimeOffset? CompletedAtUtc { get; init; }

    public DateTimeOffset? FailedAtUtc { get; init; }

    public string[] RelativeAssetPaths { get; init; } = [];

    public string? FailureMessage { get; init; }
}
