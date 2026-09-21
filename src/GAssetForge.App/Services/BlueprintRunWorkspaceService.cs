using GAssetForge.Core.Blueprints;
using System.IO;
using System.Text;
using System.Text.Json;

namespace GAssetForge.App.Services;

public sealed class BlueprintRunWorkspaceService : IBlueprintExecutionDispatcher
{
    private static readonly JsonSerializerOptions TimelineJsonOptions = new(BlueprintJson.Options)
    {
        WriteIndented = false
    };

    private readonly WorkspacePaths _paths;
    private readonly ProjectWorkspaceService _projectWorkspaceService;
    private readonly TimeProvider _timeProvider;

    public BlueprintRunWorkspaceService(WorkspacePaths paths, TimeProvider? timeProvider = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _projectWorkspaceService = new ProjectWorkspaceService();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task<BlueprintDispatchReceipt> DispatchAsync(
        BlueprintExecutionPlan plan,
        string? projectRoot,
        CancellationToken cancellationToken = default)
    {
        BlueprintExecutionPlanValidator.Validate(plan);
        cancellationToken.ThrowIfCancellationRequested();

        var now = _timeProvider.GetUtcNow();
        var runId = $"bp-{now:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}";
        var target = ResolveRunTarget(projectRoot);
        var root = target.RunRoot;
        EnsureManagedDirectory(target.TrustedRoot, root);
        var runDirectory = Path.GetFullPath(Path.Combine(root, runId));
        if (!IsWithin(root, runDirectory))
        {
            throw new InvalidDataException("蓝图运行目录试图离开受管目录。");
        }

        EnsureManagedDirectory(target.TrustedRoot, runDirectory);
        try
        {
            WriteNewFile(
                Path.Combine(runDirectory, "execution-plan.v1.json"),
                BlueprintJson.SerializePlan(plan));
            WriteNewJsonLinesFile(
                Path.Combine(runDirectory, "timeline.v1.jsonl"),
                new
                {
                    schemaVersion = 1,
                    timestampUtc = now,
                    runId,
                    stage = "plan.persisted",
                    status = "prepared",
                    message = "执行计划与运行说明正在准备；planned 提交标记尚未写入。"
                });
            WriteNewFile(
                Path.Combine(runDirectory, "README.md"),
                BuildReadme(plan, runId));
            WriteNewFile(
                Path.Combine(runDirectory, "run-state.v1.json"),
                JsonSerializer.Serialize(new
                {
                    schemaVersion = 1,
                    runId,
                    plan.GraphId,
                    plan.WorkflowKey,
                    plan.StartNodeId,
                    status = "planned",
                    executionMode = "protocol-preview",
                    createdAtUtc = now,
                    stepCount = plan.Steps.Count
                }, BlueprintJson.Options));
        }
        catch
        {
            // Keep the partially written run directory as an audit trail.
            throw;
        }

        return Task.FromResult(new BlueprintDispatchReceipt(
            runId,
            runDirectory,
            "planned"));
    }

    private static string BuildReadme(BlueprintExecutionPlan plan, string runId)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# GAssetForge Blueprint Run");
        builder.AppendLine();
        builder.AppendLine($"- Run: `{runId}`");
        builder.AppendLine($"- Graph: `{plan.GraphId}`");
        builder.AppendLine($"- Workflow: `{plan.WorkflowKey}`");
        builder.AppendLine($"- Start: `{plan.StartNodeId}`");
        builder.AppendLine($"- Steps: `{plan.Steps.Count}`");
        builder.AppendLine();
        builder.AppendLine("当前批次已完成图校验、顺序编译和协议冻结。首版不会在没有明确 Adapter 的情况下误触发本地图片处理或 Agent；后续执行器应按 execution-plan.v1.json 的 executor 与 contractId 分派步骤。");
        return builder.ToString();
    }

    private static void WriteNewFile(string path, string content)
    {
        using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read,
            4096,
            FileOptions.WriteThrough);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(content);
        writer.Flush();
        stream.Flush(flushToDisk: true);
    }

    private static void WriteNewJsonLinesFile(string path, params object[] entries)
    {
        using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read,
            4096,
            FileOptions.WriteThrough);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        foreach (var entry in entries)
        {
            writer.WriteLine(JsonSerializer.Serialize(entry, TimelineJsonOptions));
        }

        writer.Flush();
        stream.Flush(flushToDisk: true);
    }

    private RunStorageTarget ResolveRunTarget(string? projectRoot)
    {
        if (string.IsNullOrWhiteSpace(projectRoot))
        {
            return new RunStorageTarget(
                Path.GetFullPath(_paths.WorkspaceDirectory),
                Path.Combine(_paths.WorkspaceDirectory, "BlueprintRuns"));
        }

        if (!_projectWorkspaceService.TryLoad(
                projectRoot,
                out _,
                out var projectPaths,
                out var reason))
        {
            throw new InvalidDataException($"蓝图运行只能发布到有效的 GAssetForge 项目：{reason}");
        }

        // Legacy projects may predate the canonical AI root. Create only the empty managed
        // container after the project has passed validation; historical Generated data stays put.
        EnsureManagedDirectory(projectPaths.AssetDirectory, projectPaths.AiAssetDirectory);
        return new RunStorageTarget(
            projectPaths.AiAssetDirectory,
            projectPaths.BlueprintRunDirectory);
    }

    private static void EnsureManagedDirectory(string trustedRoot, string path)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(trustedRoot));
        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"蓝图运行受信根目录不存在：{root}");
        }

        if (!IsWithin(root, target))
        {
            throw new InvalidDataException("蓝图运行目录试图离开受信根目录。");
        }

        EnsureNotReparsePoint(root);
        var relative = Path.GetRelativePath(root, target);
        if (string.Equals(relative, ".", StringComparison.Ordinal))
        {
            return;
        }

        var current = root;
        foreach (var segment in relative.Split(
                     new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (File.Exists(current))
            {
                throw new IOException($"蓝图运行目录路径被文件占用：{current}");
            }

            if (!Directory.Exists(current))
            {
                Directory.CreateDirectory(current);
            }

            EnsureNotReparsePoint(current);
        }
    }

    private static void EnsureNotReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException($"蓝图运行目录不能是符号链接或重解析点：{path}");
        }
    }

    private static bool IsWithin(string root, string candidate)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var fullCandidate = Path.GetFullPath(candidate);
        return string.Equals(fullRoot, Path.TrimEndingDirectorySeparator(fullCandidate), comparison) ||
               fullCandidate.StartsWith(fullRoot + Path.DirectorySeparatorChar, comparison);
    }

    private sealed record RunStorageTarget(string TrustedRoot, string RunRoot);
}
