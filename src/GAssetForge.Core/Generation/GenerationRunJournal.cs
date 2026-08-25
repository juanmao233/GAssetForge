using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace GAssetForge.Core.Generation;

public sealed record GenerationRunWorkspacePlan
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public string RunId { get; init; } = string.Empty;

    public string RootPath { get; init; } = string.Empty;

    public string ManifestPath { get; init; } = string.Empty;

    public string PromptPath { get; init; } = string.Empty;

    public string DiagnosticsPath { get; init; } = string.Empty;

    /// <summary>
    /// Exact model-return snapshots copied by GAssetForge before any resize or chroma work.
    /// </summary>
    public string[] ModelOutputPaths { get; init; } = [];

    /// <summary>
    /// Software-normalized, transparent PNG intermediates retained before final delivery commit.
    /// </summary>
    public string[] ProcessedPaths { get; init; } = [];

    public bool PreserveIntermediates { get; init; } = true;
}

public sealed record GenerationRunStageEvent
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;

    public string RunId { get; init; } = string.Empty;

    public string? BatchId { get; init; }

    public string? TaskId { get; init; }

    public int? OutputIndex { get; init; }

    public string Stage { get; init; } = string.Empty;

    public string Status { get; init; } = string.Empty;

    public DateTimeOffset? StartedAtUtc { get; init; }

    public DateTimeOffset? EndedAtUtc { get; init; }

    public long? ElapsedMilliseconds { get; init; }

    public string? AgentId { get; init; }

    public string? ExpectedPath { get; init; }

    public string? ObservedPath { get; init; }

    public string? ErrorCode { get; init; }

    public string? Message { get; init; }

    public GenerationGeometryDiagnostics? GeometryDiagnostics { get; init; }
}

public sealed record GenerationGeometryBoundingBox(
    int X,
    int Y,
    int Width,
    int Height);

public sealed record GenerationGeometryCornerOccupancy(
    double TopLeftPercent,
    double TopRightPercent,
    double BottomLeftPercent,
    double BottomRightPercent);

public sealed record GenerationGeometryDiagnostics
{
    public GenerationGeometryBoundingBox ForegroundBoundingBox { get; init; } = new(0, 0, 0, 0);

    public int ActualWidth { get; init; }

    public int ActualHeight { get; init; }

    public double ActualAspectRatio { get; init; }

    public double ExpectedAspectRatio { get; init; }

    public double AllowedDeviationPercent { get; init; }

    public GenerationGeometryCornerOccupancy CornerOccupancy { get; init; } = new(0d, 0d, 0d, 0d);

    public double RotationAngleEstimate { get; init; }

    public double DiamondScore { get; init; }

    public string ValidatedImagePath { get; init; } = string.Empty;
}

/// <summary>
/// Cross-process, append-only diagnostics journal stored in the frozen project-local run workspace.
/// The journal is best effort: generation must not fail merely because diagnostic persistence is
/// temporarily unavailable.
/// </summary>
public static class GenerationRunJournal
{
    private const int MaximumTextLength = 4_096;
    private static readonly object WriteGate = new();
    private static readonly JsonSerializerOptions JournalJsonOptions = new(GenerationJson.Options)
    {
        WriteIndented = false
    };

    public static GenerationRunStageMeasurement Measure(
        GenerationDeliveryPlan? delivery,
        GenerationTask? task,
        string stage,
        string? agentId = null,
        int? outputIndex = null,
        string? expectedPath = null,
        string? observedPath = null) =>
        new GenerationRunStageMeasurement(
            delivery,
            task,
            stage,
            agentId,
            outputIndex,
            expectedPath,
            observedPath);

    public static void Append(
        GenerationDeliveryPlan? delivery,
        GenerationRunStageEvent stageEvent)
    {
        ArgumentNullException.ThrowIfNull(stageEvent);
        if (delivery?.Workspace is not { } workspace ||
            string.IsNullOrWhiteSpace(workspace.DiagnosticsPath))
        {
            return;
        }

        try
        {
            var diagnosticsPath = ResolveWorkspacePath(
                delivery.AssetRoot,
                workspace.RootPath,
                workspace.DiagnosticsPath);
            var directory = Path.GetDirectoryName(diagnosticsPath);
            if (string.IsNullOrWhiteSpace(directory))
            {
                return;
            }

            Directory.CreateDirectory(directory);
            var safeEvent = stageEvent with
            {
                RunId = workspace.RunId,
                Stage = Limit(stageEvent.Stage),
                Status = Limit(stageEvent.Status),
                AgentId = LimitNullable(stageEvent.AgentId),
                ExpectedPath = LimitNullable(stageEvent.ExpectedPath),
                ObservedPath = LimitNullable(stageEvent.ObservedPath),
                ErrorCode = LimitNullable(stageEvent.ErrorCode),
                Message = LimitNullable(stageEvent.Message)
            };
            var line = JsonSerializer.Serialize(safeEvent, JournalJsonOptions);
            lock (WriteGate)
            {
                using var stream = new FileStream(
                    diagnosticsPath,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete);
                using var writer = new StreamWriter(
                    stream,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                writer.WriteLine(line);
            }
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or InvalidDataException or
            ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            // Diagnostics are intentionally non-fatal. Tool and task state remain authoritative.
        }
    }

    private static string ResolveWorkspacePath(
        string assetRoot,
        string workspaceRoot,
        string relativePath)
    {
        var normalizedAssetRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(assetRoot));
        var normalizedWorkspaceRoot = NormalizeRelativePath(workspaceRoot);
        var normalizedRelativePath = NormalizeRelativePath(relativePath);
        if (!normalizedRelativePath.StartsWith(
                $"{normalizedWorkspaceRoot}/",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("运行诊断路径必须位于运行工作区内。");
        }

        var fullPath = Path.GetFullPath(Path.Combine(
            normalizedAssetRoot,
            normalizedRelativePath.Replace('/', Path.DirectorySeparatorChar)));
        var relativeToAssetRoot = Path.GetRelativePath(normalizedAssetRoot, fullPath);
        if (Path.IsPathRooted(relativeToAssetRoot) ||
            relativeToAssetRoot is "." or ".." ||
            relativeToAssetRoot.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new InvalidDataException("运行诊断路径越出了 Assets 根目录。");
        }

        return fullPath;
    }

    private static string NormalizeRelativePath(string path)
    {
        var normalized = path?.Trim().Replace('\\', '/') ?? string.Empty;
        if (normalized.Length == 0 || Path.IsPathRooted(normalized) ||
            normalized.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Any(segment => segment is "." or ".."))
        {
            throw new InvalidDataException("运行工作区路径无效。");
        }

        return normalized.Trim('/');
    }

    private static string Limit(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? "unknown"
            : value.Trim()[..Math.Min(value.Trim().Length, MaximumTextLength)];

    private static string? LimitNullable(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : Limit(value);

    public sealed class GenerationRunStageMeasurement : IDisposable
    {
        private readonly GenerationDeliveryPlan? _delivery;
        private readonly GenerationTask? _task;
        private readonly string _stage;
        private readonly string? _agentId;
        private readonly int? _outputIndex;
        private readonly string? _expectedPath;
        private readonly string? _observedPath;
        private readonly DateTimeOffset _startedAtUtc = DateTimeOffset.UtcNow;
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private int _disposed;

        internal GenerationRunStageMeasurement(
            GenerationDeliveryPlan? delivery,
            GenerationTask? task,
            string stage,
            string? agentId,
            int? outputIndex,
            string? expectedPath,
            string? observedPath)
        {
            _delivery = delivery;
            _task = task;
            _stage = stage;
            _agentId = agentId;
            _outputIndex = outputIndex;
            _expectedPath = expectedPath;
            _observedPath = observedPath;
            Append(
                _delivery,
                new GenerationRunStageEvent
                {
                    BatchId = _task?.BatchId,
                    TaskId = _task?.Id,
                    OutputIndex = _outputIndex,
                    Stage = _stage,
                    Status = "started",
                    StartedAtUtc = _startedAtUtc,
                    AgentId = _agentId,
                    ExpectedPath = _expectedPath,
                    ObservedPath = _observedPath
                });
        }

        public void Complete(string? message = null, string? observedPath = null)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _stopwatch.Stop();
            var endedAtUtc = DateTimeOffset.UtcNow;
            Append(
                _delivery,
                new GenerationRunStageEvent
                {
                    BatchId = _task?.BatchId,
                    TaskId = _task?.Id,
                    OutputIndex = _outputIndex,
                    Stage = _stage,
                    Status = "completed",
                    StartedAtUtc = _startedAtUtc,
                    EndedAtUtc = endedAtUtc,
                    ElapsedMilliseconds = _stopwatch.ElapsedMilliseconds,
                    AgentId = _agentId,
                    ExpectedPath = _expectedPath,
                    ObservedPath = observedPath ?? _observedPath,
                    Message = message
                });
        }

        public void Fail(Exception exception, string? errorCode = null)
        {
            ArgumentNullException.ThrowIfNull(exception);
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _stopwatch.Stop();
            var endedAtUtc = DateTimeOffset.UtcNow;
            Append(
                _delivery,
                new GenerationRunStageEvent
                {
                    BatchId = _task?.BatchId,
                    TaskId = _task?.Id,
                    OutputIndex = _outputIndex,
                    Stage = _stage,
                    Status = "failed",
                    StartedAtUtc = _startedAtUtc,
                    EndedAtUtc = endedAtUtc,
                    ElapsedMilliseconds = _stopwatch.ElapsedMilliseconds,
                    AgentId = _agentId,
                    ExpectedPath = _expectedPath,
                    ObservedPath = _observedPath,
                    ErrorCode = errorCode ?? exception.GetType().Name,
                    Message = exception.Message
                });
        }

        public void Dispose()
        {
            if (Volatile.Read(ref _disposed) == 0)
            {
                Complete();
            }
        }
    }
}
