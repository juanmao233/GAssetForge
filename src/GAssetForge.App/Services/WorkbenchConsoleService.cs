using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GAssetForge.App.Services;

public enum WorkbenchLogLevel
{
    Trace,
    Information,
    Success,
    Warning,
    Error,
    Command
}

public sealed record WorkbenchLogContext
{
    public string? RunId { get; init; }
    public string? BatchId { get; init; }
    public string? TaskId { get; init; }
    public int? OutputIndex { get; init; }
    public string? AgentThreadId { get; init; }
    public string? AgentTurnId { get; init; }
    public string? AgentItemId { get; init; }
    public string? Stage { get; init; }
    public string? Status { get; init; }
    public long? ElapsedMilliseconds { get; init; }
    public string? ExpectedPath { get; init; }
    public string? ObservedPath { get; init; }
    public string? ErrorCode { get; init; }
    public string? FailureStage { get; init; }
    public string? RecoveryHint { get; init; }
    public string? ReadinessState { get; init; }
    public string? ExceptionType { get; init; }
    public string? BlueprintGraphId { get; init; }
    public string? BlueprintNodeId { get; init; }
    public string? BlueprintStepId { get; init; }

    public string Summary => string.Join(
        " · ",
        new[]
        {
            string.IsNullOrWhiteSpace(BatchId) ? null : $"batch={BatchId}",
            string.IsNullOrWhiteSpace(TaskId) ? null : $"task={TaskId}",
            OutputIndex is null ? null : $"output={OutputIndex}",
            string.IsNullOrWhiteSpace(Stage) ? null : $"stage={Stage}",
            string.IsNullOrWhiteSpace(Status) ? null : $"status={Status}",
            ElapsedMilliseconds is null ? null : $"elapsed={ElapsedMilliseconds}ms",
            string.IsNullOrWhiteSpace(AgentThreadId) ? null : $"thread={AgentThreadId}",
            string.IsNullOrWhiteSpace(AgentTurnId) ? null : $"turn={AgentTurnId}",
            string.IsNullOrWhiteSpace(BlueprintGraphId) ? null : $"graph={BlueprintGraphId}",
            string.IsNullOrWhiteSpace(BlueprintNodeId) ? null : $"node={BlueprintNodeId}",
            string.IsNullOrWhiteSpace(BlueprintStepId) ? null : $"step={BlueprintStepId}",
            string.IsNullOrWhiteSpace(ErrorCode) ? null : $"error={ErrorCode}",
            string.IsNullOrWhiteSpace(FailureStage) ? null : $"failureStage={FailureStage}",
            string.IsNullOrWhiteSpace(ReadinessState) ? null : $"readiness={ReadinessState}",
            string.IsNullOrWhiteSpace(ExceptionType) ? null : $"exception={ExceptionType}",
            string.IsNullOrWhiteSpace(RecoveryHint) ? null : $"建议：{RecoveryHint}"
        }.Where(value => value is not null));
}

public sealed record WorkbenchLogEntry(
    long Sequence,
    long Epoch,
    DateTimeOffset Timestamp,
    WorkbenchLogLevel Level,
    string Source,
    string Message,
    string Details = "",
    WorkbenchLogContext? Context = null)
{
    /// <summary>Time received by the log bus; Timestamp is the original source event time.</summary>
    public DateTimeOffset RecordedAt { get; init; } = Timestamp;

    public string TimestampText => Timestamp.ToLocalTime().ToString("HH:mm:ss.fff");

    public string LevelText => Level switch
    {
        WorkbenchLogLevel.Trace => "跟踪",
        WorkbenchLogLevel.Information => "信息",
        WorkbenchLogLevel.Success => "成功",
        WorkbenchLogLevel.Warning => "警告",
        WorkbenchLogLevel.Error => "错误",
        WorkbenchLogLevel.Command => "命令",
        _ => Level.ToString()
    };

    public string LevelGlyph => Level switch
    {
        WorkbenchLogLevel.Success => "●",
        WorkbenchLogLevel.Warning => "▲",
        WorkbenchLogLevel.Error => "●",
        WorkbenchLogLevel.Command => ">",
        WorkbenchLogLevel.Trace => "·",
        _ => "●"
    };

    public string DetailsSummary => string.Join(
        " · ",
        new[]
        {
            Context?.Summary,
            string.IsNullOrWhiteSpace(Details)
                ? null
                : Details.Replace('\r', ' ').Replace('\n', ' ').Trim()
        }.Where(value => !string.IsNullOrWhiteSpace(value)));

    // The log bus has already redacted every value before creating this entry.
    // Keep export at the entry boundary so clipboard and details use the same data.
    public string ExportText => string.Join(
        Environment.NewLine,
        new[]
        {
            $"#{Sequence} [{LevelText}] {Source}",
            $"发生时间：{Timestamp:O}",
            $"记录时间：{RecordedAt:O}",
            Message,
            Context?.Summary,
            Context is null ? null : $"结构化上下文：{JsonSerializer.Serialize(Context)}",
            string.IsNullOrWhiteSpace(Details) ? null : Details
        }.Where(value => !string.IsNullOrWhiteSpace(value)));
}

public sealed class WorkbenchLogEntryEventArgs(WorkbenchLogEntry entry) : EventArgs
{
    public WorkbenchLogEntry Entry { get; } = entry;
}

public sealed class WorkbenchLogBufferClearedEventArgs(
    long sequenceBarrier,
    long epoch) : EventArgs
{
    public long SequenceBarrier { get; } = sequenceBarrier;

    public long Epoch { get; } = epoch;
}

public interface IWorkbenchLogSink
{
    void Write(
        WorkbenchLogLevel level,
        string source,
        string message,
        DateTimeOffset? timestamp = null,
        Exception? exception = null);
}

public interface IWorkbenchLogRedactionRegistry
{
    bool RegisterSensitiveValue(string sensitiveValue);
}

public interface IWorkbenchStructuredLogSink : IWorkbenchLogSink
{
    void WriteStructured(
        WorkbenchLogLevel level,
        string source,
        string message,
        WorkbenchLogContext context,
        DateTimeOffset? timestamp = null,
        Exception? exception = null);
}

/// <summary>
/// Thread-safe, UI-independent logging bus shared by workbench modules.
/// Entries are published synchronously to subscribers and persisted as JSONL
/// in a per-process session file that can be inspected while the app is running.
/// </summary>
public sealed class WorkbenchConsoleService :
    IWorkbenchStructuredLogSink,
    IWorkbenchLogRedactionRegistry,
    IDisposable
{
    private const int DefaultBufferCapacity = 10_000;
    private const string RedactionMarker = "[REDACTED]";
    private const string FileLoggingRecoveryHint =
        "日志仍保存在当前工作台内存中，请及时复制所需日志；检查日志目录权限与磁盘空间后重启软件以恢复文件记录。";
    // Match explicit credential assignments, not ordinary text about authentication.
    private static readonly Regex CredentialAssignmentPattern = new(
        """(?<prefix>\b(?:api[_-]?key|OPENAI_API_KEY|access[_-]?token|refresh[_-]?token|client[_-]?secret)\b["']?\s*[:=]\s*["']?)(?<secret>[^\s"',;&<>]+)""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex AuthorizationPattern = new(
        """(?<prefix>\b(?:authorization|proxy-authorization)\b["']?\s*[:=]\s*["']?(?:Bearer|Basic)\s+)(?<secret>[A-Za-z0-9._~+/=-]+)""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private readonly object _gate = new();
    private readonly Queue<WorkbenchLogEntry> _entries;
    private readonly HashSet<string> _sensitiveValues = new(StringComparer.Ordinal);
    private readonly int _bufferCapacity;
    private string[] _redactionValues = [];
    private StreamWriter? _sessionWriter;
    private long _sequence;
    private long _bufferEpoch;
    private bool _disposed;

    public WorkbenchConsoleService(WorkspacePaths paths, int bufferCapacity = DefaultBufferCapacity)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (bufferCapacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(bufferCapacity), "日志缓冲区容量必须大于零。");
        }

        _bufferCapacity = bufferCapacity;
        _entries = new Queue<WorkbenchLogEntry>(Math.Min(bufferCapacity, 1024));
        LogDirectory = Path.Combine(paths.RootDirectory, "Logs");
        SessionLogPath = CreateSessionLogPath(LogDirectory);

        Exception? initializationFailure = null;
        try
        {
            Directory.CreateDirectory(LogDirectory);
            var stream = new FileStream(
                SessionLogPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete);
            _sessionWriter = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            _sessionWriter.AutoFlush = true;
        }
        catch (Exception exception) when (IsFileLoggingException(exception))
        {
            initializationFailure = exception;
            FileLoggingError = RedactLocked(exception.Message);
            var writer = _sessionWriter;
            _sessionWriter = null;
            DisposeWriterSafely(writer);
        }

        if (IsFileLoggingAvailable)
        {
            var assembly = typeof(WorkbenchConsoleService).Assembly;
            var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ??
                          assembly.GetName().Version?.ToString() ?? "unknown";
            WriteStructured(WorkbenchLogLevel.Information, "Workbench",
                $"指令日志会话已启动：{Path.GetFileName(SessionLogPath)}" +
                $" · version={version} · build={assembly.ManifestModule.ModuleVersionId:D}" +
                $" · process={Environment.ProcessId} · executable={Environment.ProcessPath ?? "unknown"}",
                new WorkbenchLogContext { Stage = "workbench.start", Status = "initialized" });
        }
        else
        {
            WriteStructured(WorkbenchLogLevel.Warning, "Workbench",
                $"会话日志文件不可用，已降级为内存日志：{FileLoggingError}",
                CreateFileLoggingFailureContext(), exception: initializationFailure);
        }
    }

    public event EventHandler<WorkbenchLogEntryEventArgs>? EntryPublished;

    /// <summary>
    /// Raised after the in-memory display buffer is reset. The session file is never deleted.
    /// </summary>
    public event EventHandler<WorkbenchLogBufferClearedEventArgs>? BufferCleared;

    public string LogDirectory { get; }

    public string SessionLogPath { get; }

    public string FileLoggingError { get; private set; } = string.Empty;

    public bool IsFileLoggingAvailable
    {
        get
        {
            lock (_gate)
            {
                return _sessionWriter is not null;
            }
        }
    }

    public IReadOnlyList<WorkbenchLogEntry> GetSnapshot()
    {
        lock (_gate)
        {
            return _entries.ToArray();
        }
    }

    public bool RegisterSensitiveValue(string sensitiveValue)
    {
        if (string.IsNullOrWhiteSpace(sensitiveValue))
        {
            return false;
        }

        lock (_gate)
        {
            if (_disposed || !_sensitiveValues.Add(sensitiveValue))
            {
                return false;
            }

            _redactionValues = _sensitiveValues
                .OrderByDescending(value => value.Length)
                .ToArray();
            return true;
        }
    }

    public void Write(
        WorkbenchLogLevel level,
        string source,
        string message,
        DateTimeOffset? timestamp = null,
        Exception? exception = null) =>
        WriteCore(level, source, message, timestamp, exception, context: null);

    public void WriteStructured(
        WorkbenchLogLevel level,
        string source,
        string message,
        WorkbenchLogContext context,
        DateTimeOffset? timestamp = null,
        Exception? exception = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        WriteCore(level, source, message, timestamp, exception, context);
    }

    private void WriteCore(
        WorkbenchLogLevel level,
        string source,
        string message,
        DateTimeOffset? timestamp,
        Exception? exception,
        WorkbenchLogContext? context)
    {
        source = string.IsNullOrWhiteSpace(source) ? "Workbench" : source.Trim();
        message ??= string.Empty;

        WorkbenchLogEntry entry;
        WorkbenchLogEntry? persistenceWarning = null;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            var recordedAt = DateTimeOffset.Now;
            if (exception is not null && string.IsNullOrWhiteSpace(context?.ExceptionType))
            {
                context = (context ?? new WorkbenchLogContext()) with
                {
                    ExceptionType = exception.GetType().FullName
                };
            }

            entry = new WorkbenchLogEntry(
                Interlocked.Increment(ref _sequence),
                _bufferEpoch,
                timestamp ?? recordedAt,
                level,
                RedactLocked(source),
                RedactLocked(message),
                RedactLocked(exception?.ToString() ?? string.Empty),
                RedactContextLocked(context))
            {
                RecordedAt = recordedAt
            };

            EnqueueLocked(entry);
            if (WriteSessionLineLocked(entry) is { } persistenceFailure)
            {
                var warningTime = DateTimeOffset.Now;
                persistenceWarning = new WorkbenchLogEntry(
                    Interlocked.Increment(ref _sequence), _bufferEpoch, warningTime,
                    WorkbenchLogLevel.Warning, "Workbench",
                    "会话日志文件写入失败，已降级为内存日志；当前任务不会因此中断。",
                    RedactLocked(persistenceFailure.ToString()),
                    RedactContextLocked(CreateFileLoggingFailureContext() with
                    {
                        ExceptionType = persistenceFailure.GetType().FullName
                    }))
                {
                    RecordedAt = warningTime
                };
                EnqueueLocked(persistenceWarning);
            }
        }

        PublishEntry(entry);
        if (persistenceWarning is not null)
        {
            PublishEntry(persistenceWarning);
        }
    }

    private void EnqueueLocked(WorkbenchLogEntry entry)
    {
        _entries.Enqueue(entry);
        while (_entries.Count > _bufferCapacity)
        {
            _entries.Dequeue();
        }
    }

    public void WriteTrace(string source, string message, DateTimeOffset? timestamp = null) =>
        Write(WorkbenchLogLevel.Trace, source, message, timestamp);

    public void WriteInfo(string source, string message, DateTimeOffset? timestamp = null) =>
        Write(WorkbenchLogLevel.Information, source, message, timestamp);

    public void WriteSuccess(string source, string message, DateTimeOffset? timestamp = null) =>
        Write(WorkbenchLogLevel.Success, source, message, timestamp);

    public void WriteWarning(string source, string message, DateTimeOffset? timestamp = null) =>
        Write(WorkbenchLogLevel.Warning, source, message, timestamp);

    public void WriteError(
        string source,
        string message,
        Exception? exception = null,
        DateTimeOffset? timestamp = null) =>
        Write(WorkbenchLogLevel.Error, source, message, timestamp, exception);

    public void ClearInMemory()
    {
        EventHandler<WorkbenchLogBufferClearedEventArgs>? handlers;
        WorkbenchLogBufferClearedEventArgs eventArgs;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _entries.Clear();
            _bufferEpoch++;
            handlers = BufferCleared;
            eventArgs = new WorkbenchLogBufferClearedEventArgs(_sequence, _bufferEpoch);
        }

        InvokeSafely(handlers, this, eventArgs);
    }

    public void Dispose()
    {
        StreamWriter? writer;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            writer = _sessionWriter;
            _sessionWriter = null;
            EntryPublished = null;
            BufferCleared = null;
        }

        DisposeWriterSafely(writer);
    }

    private static string CreateSessionLogPath(string logDirectory)
    {
        var timestamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss-fff");
        var processId = Environment.ProcessId;
        var uniqueSuffix = Guid.NewGuid().ToString("N")[..8];
        return Path.Combine(logDirectory, $"workbench-{timestamp}-p{processId}-{uniqueSuffix}.jsonl");
    }

    private Exception? WriteSessionLineLocked(WorkbenchLogEntry entry)
    {
        if (_sessionWriter is null)
        {
            return null;
        }

        try
        {
            var json = JsonSerializer.Serialize(new
            {
                schemaVersion = 2,
                sequence = entry.Sequence,
                epoch = entry.Epoch,
                timestamp = entry.Timestamp,
                recordedAt = entry.RecordedAt,
                level = entry.Level.ToString(),
                source = entry.Source,
                message = entry.Message,
                details = entry.Details,
                context = entry.Context
            });
            _sessionWriter.WriteLine(json);
            return null;
        }
        catch (Exception exception) when (IsFileLoggingException(exception))
        {
            FileLoggingError = RedactLocked(exception.Message);
            var writer = _sessionWriter;
            _sessionWriter = null;
            // Dispose may flush the same failed stream again. It must not replace the
            // original failure or propagate into generation and conversation code.
            DisposeWriterSafely(writer);
            return exception;
        }
    }

    private WorkbenchLogContext CreateFileLoggingFailureContext() => new()
    {
        Stage = "LogPersistence",
        FailureStage = "LogPersistence",
        Status = "MemoryOnly",
        ErrorCode = "LOG_FILE_UNAVAILABLE",
        RecoveryHint = FileLoggingRecoveryHint,
        ExpectedPath = SessionLogPath
    };

    private static bool IsFileLoggingException(Exception exception) =>
        exception is IOException or ObjectDisposedException or UnauthorizedAccessException or
            NotSupportedException or System.Security.SecurityException;

    private static void DisposeWriterSafely(StreamWriter? writer)
    {
        try
        {
            writer?.Dispose();
        }
        catch (Exception exception) when (IsFileLoggingException(exception))
        {
            // Logging shutdown must never interrupt the application being diagnosed.
        }
    }

    private string RedactLocked(string value)
    {
        foreach (var sensitiveValue in _redactionValues)
        {
            value = value.Replace(sensitiveValue, RedactionMarker, StringComparison.Ordinal);
        }

        return RedactCommonCredentials(value);
    }

    internal static string RedactCommonCredentials(string value)
    {
        value = CredentialAssignmentPattern.Replace(value,
            match => match.Groups["prefix"].Value + RedactionMarker);
        return AuthorizationPattern.Replace(value,
            match => match.Groups["prefix"].Value + RedactionMarker);
    }

    private WorkbenchLogContext? RedactContextLocked(WorkbenchLogContext? context) =>
        context is null
            ? null
            : context with
            {
                RunId = RedactNullableLocked(context.RunId),
                BatchId = RedactNullableLocked(context.BatchId),
                TaskId = RedactNullableLocked(context.TaskId),
                AgentThreadId = RedactNullableLocked(context.AgentThreadId),
                AgentTurnId = RedactNullableLocked(context.AgentTurnId),
                AgentItemId = RedactNullableLocked(context.AgentItemId),
                Stage = RedactNullableLocked(context.Stage),
                Status = RedactNullableLocked(context.Status),
                ExpectedPath = RedactNullableLocked(context.ExpectedPath),
                ObservedPath = RedactNullableLocked(context.ObservedPath),
                ErrorCode = RedactNullableLocked(context.ErrorCode),
                FailureStage = RedactNullableLocked(context.FailureStage),
                RecoveryHint = RedactNullableLocked(context.RecoveryHint),
                ReadinessState = RedactNullableLocked(context.ReadinessState),
                ExceptionType = RedactNullableLocked(context.ExceptionType),
                BlueprintGraphId = RedactNullableLocked(context.BlueprintGraphId),
                BlueprintNodeId = RedactNullableLocked(context.BlueprintNodeId),
                BlueprintStepId = RedactNullableLocked(context.BlueprintStepId)
            };

    private string? RedactNullableLocked(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : RedactLocked(value);

    private void PublishEntry(WorkbenchLogEntry entry)
    {
        var handlers = EntryPublished;
        if (handlers is null)
        {
            return;
        }

        var args = new WorkbenchLogEntryEventArgs(entry);
        foreach (EventHandler<WorkbenchLogEntryEventArgs> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, args);
            }
            catch
            {
                // A UI or integration subscriber must never break the logging bus.
            }
        }
    }

    private static void InvokeSafely<TEventArgs>(
        EventHandler<TEventArgs>? handlers,
        object sender,
        TEventArgs args)
        where TEventArgs : EventArgs
    {
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler<TEventArgs> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(sender, args);
            }
            catch
            {
                // Clearing the display is best effort for every subscriber.
            }
        }
    }
}
