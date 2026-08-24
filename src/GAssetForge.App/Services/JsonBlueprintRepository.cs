using GAssetForge.Core.Blueprints;
using GAssetForge.Core.Workflows;
using System.Collections.Concurrent;
using System.IO;
using System.Text;

namespace GAssetForge.App.Services;

public sealed record BlueprintLoadResult(
    BlueprintGraph Graph,
    string FilePath,
    string RecoverySource,
    string ErrorMessage);

public sealed class JsonBlueprintRepository
{
    private const long MaximumGraphBytes = 8L * 1024L * 1024L;
    private static readonly ConcurrentDictionary<string, object> ProcessLocks =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private readonly string _workspaceRoot;
    private readonly string _draftRoot;
    private readonly ProjectWorkspaceService _projectWorkspaceService;
    private readonly TimeProvider _timeProvider;

    public JsonBlueprintRepository(WorkspacePaths paths, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _workspaceRoot = Path.GetFullPath(paths.WorkspaceDirectory);
        _draftRoot = Path.Combine(paths.WorkspaceDirectory, "BlueprintDrafts");
        _projectWorkspaceService = new ProjectWorkspaceService();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public BlueprintLoadResult LoadOrCreate(string? projectRoot, string workflowKey)
    {
        workflowKey = ValidateWorkflowKey(workflowKey);
        var target = ResolveGraphTarget(projectRoot, workflowKey);
        var path = target.Path;
        var backupPath = $"{path}.bak";
        var syncRoot = ProcessLocks.GetOrAdd(path, static _ => new object());
        lock (syncRoot)
        {
            if (TryRead(path, workflowKey, out var primary))
            {
                return new BlueprintLoadResult(primary!, path, "primary", string.Empty);
            }

            if (TryRead(backupPath, workflowKey, out var backup))
            {
                return new BlueprintLoadResult(
                    backup!,
                    path,
                    "backup",
                    "蓝图主文件无效，当前已从备份加载；下次有效编辑会保留损坏副本后修复主文件。");
            }

            var graph = BlueprintGraphFactory.CreateDefault(workflowKey, _timeProvider);
            var hasInvalidFile = File.Exists(path) || File.Exists(backupPath);
            if (!hasInvalidFile)
            {
                SaveCore(target, graph);
                return new BlueprintLoadResult(graph, path, "default", string.Empty);
            }

            return new BlueprintLoadResult(
                graph,
                path,
                "safe-default",
                "蓝图主文件与备份均无效；已加载内存安全默认图且未覆盖原文件。");
        }
    }

    public void Save(string? projectRoot, string workflowKey, BlueprintGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        workflowKey = ValidateWorkflowKey(workflowKey);
        if (!string.Equals(graph.WorkflowKey, workflowKey, StringComparison.Ordinal))
        {
            throw new InvalidDataException("蓝图工作流键与目标存储位置不一致。");
        }

        graph.UpdatedAtUtc = _timeProvider.GetUtcNow();
        BlueprintGraphValidator.Validate(graph);
        var target = ResolveGraphTarget(projectRoot, workflowKey);
        var path = target.Path;
        var syncRoot = ProcessLocks.GetOrAdd(path, static _ => new object());
        lock (syncRoot)
        {
            SaveCore(target, graph);
        }
    }

    public string ResolveGraphPath(string? projectRoot, string workflowKey)
        => ResolveGraphTarget(projectRoot, workflowKey).Path;

    private BlueprintStorageTarget ResolveGraphTarget(string? projectRoot, string workflowKey)
    {
        workflowKey = ValidateWorkflowKey(workflowKey);
        string trustedRoot;
        string directory;
        if (string.IsNullOrWhiteSpace(projectRoot))
        {
            trustedRoot = _workspaceRoot;
            directory = _draftRoot;
        }
        else
        {
            if (!_projectWorkspaceService.TryLoad(
                    projectRoot,
                    out _,
                    out var projectPaths,
                    out var reason))
            {
                throw new InvalidDataException($"蓝图只能绑定到有效的 GAssetForge 项目：{reason}");
            }

            trustedRoot = projectPaths.ProjectSettingsDirectory;
            directory = projectPaths.BlueprintDirectory;
        }

        EnsureManagedDirectory(trustedRoot, directory);
        var fileName = $"{workflowKey}.gafgraph.json";
        var path = Path.GetFullPath(Path.Combine(directory, fileName));
        if (!IsWithin(directory, path))
        {
            throw new InvalidDataException("蓝图文件路径试图离开受管目录。");
        }

        EnsureManagedFileSlot(path, "蓝图主文件");
        EnsureManagedFileSlot($"{path}.bak", "蓝图备份文件");
        return new BlueprintStorageTarget(trustedRoot, directory, path);
    }

    private bool TryRead(string path, string workflowKey, out BlueprintGraph? graph)
    {
        graph = null;
        try
        {
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(path);
            }
            catch (FileNotFoundException)
            {
                return false;
            }
            catch (DirectoryNotFoundException)
            {
                return false;
            }

            if ((attributes & FileAttributes.Directory) != 0)
            {
                throw new IOException($"蓝图文件路径被目录占用：{path}");
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException($"蓝图文件不能是符号链接或重解析点：{path}");
            }

            var file = new FileInfo(path);
            if (file.Length <= 0 || file.Length > MaximumGraphBytes)
            {
                return false;
            }

            graph = BlueprintJson.DeserializeGraph(File.ReadAllText(path));
            BlueprintGraphValidator.Validate(graph);
            if (!string.Equals(graph.WorkflowKey, workflowKey, StringComparison.Ordinal))
            {
                graph = null;
                return false;
            }

            return true;
        }
        catch (Exception exception) when (exception is
            InvalidDataException or
            System.Text.Json.JsonException or
            NotSupportedException)
        {
            graph = null;
            return false;
        }
    }

    private void SaveCore(BlueprintStorageTarget target, BlueprintGraph graph)
    {
        var content = BlueprintJson.SerializeGraph(graph);
        if (Encoding.UTF8.GetByteCount(content) > MaximumGraphBytes)
        {
            throw new InvalidDataException($"蓝图文件不能超过 {MaximumGraphBytes / 1024 / 1024} MiB。");
        }

        var path = target.Path;
        var directory = target.Directory;
        EnsureManagedDirectory(target.TrustedRoot, directory);
        var backupPath = $"{path}.bak";
        EnsureManagedFileSlot(path, "蓝图主文件");
        EnsureManagedFileSlot(backupPath, "蓝图备份文件");
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            WriteDurable(temporaryPath, content);
            if (!File.Exists(path))
            {
                File.Move(temporaryPath, path);
                return;
            }

            if (TryRead(path, graph.WorkflowKey, out _))
            {
                File.Replace(temporaryPath, path, backupPath, ignoreMetadataErrors: true);
                return;
            }

            var invalidPath = Path.Combine(
                directory,
                $"{Path.GetFileName(path)}.invalid-{_timeProvider.GetUtcNow():yyyyMMdd-HHmmssfff}");
            File.Move(path, invalidPath);
            File.Move(temporaryPath, path);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private static void WriteDurable(string path, string content)
    {
        using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            4096,
            FileOptions.WriteThrough);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(content);
        writer.Flush();
        stream.Flush(flushToDisk: true);
    }

    private static string ValidateWorkflowKey(string workflowKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowKey);
        var definition = WorkflowCatalog.All.FirstOrDefault(item =>
            string.Equals(item.Key, workflowKey, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException("未知的业务工作流键。", nameof(workflowKey));
        return definition.Key;
    }

    private static void EnsureManagedDirectory(string trustedRoot, string path)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(trustedRoot));
        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"蓝图受信根目录不存在：{root}");
        }

        if (!IsWithin(root, target))
        {
            throw new InvalidDataException("蓝图目录试图离开受信根目录。");
        }

        EnsureNotReparsePoint(root, "蓝图受信根目录");
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
                throw new IOException($"蓝图目录路径被文件占用：{current}");
            }

            if (!Directory.Exists(current))
            {
                Directory.CreateDirectory(current);
            }

            EnsureNotReparsePoint(current, "蓝图受管目录");
        }
    }

    private static void EnsureManagedFileSlot(string path, string label)
    {
        if (Directory.Exists(path))
        {
            throw new IOException($"{label}路径被目录占用：{path}");
        }

        EnsureNotReparsePointIfExists(path, label);
    }

    private static void EnsureNotReparsePointIfExists(string path, string label)
    {
        if (File.Exists(path) || Directory.Exists(path))
        {
            EnsureNotReparsePoint(path, label);
        }
    }

    private static void EnsureNotReparsePoint(string path, string label)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException($"{label}不能是符号链接或重解析点：{path}");
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

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // A unique temporary file can be cleaned by later maintenance.
        }
    }

    private sealed record BlueprintStorageTarget(
        string TrustedRoot,
        string Directory,
        string Path);
}
