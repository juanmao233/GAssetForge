using GAssetForge.Core;
using GAssetForge.Core.Projects;
using GAssetForge.Core.Workflows;
using System.IO;
using System.Text;
using System.Text.Json;

namespace GAssetForge.App.Services;

public sealed class ProjectPaths
{
    public const string ManifestFileName = ProductIdentity.ProjectManifestFileName;
    public const string LegacyManifestFileName = ProductIdentity.LegacyProjectManifestFileName;
    public const string UiProjectFileName = "ui-project.pfui.json";

    public ProjectPaths(string projectRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        RootDirectory = Normalize(projectRoot);
        AssetDirectory = Path.Combine(RootDirectory, "Assets");
        GeneratedAssetDirectory = Path.Combine(AssetDirectory, "Generated");
        PackagesDirectory = Path.Combine(RootDirectory, "Packages");
        ProjectSettingsDirectory = Path.Combine(RootDirectory, "ProjectSettings");
        var canonicalManifestPath = Path.Combine(ProjectSettingsDirectory, ManifestFileName);
        var legacyManifestPath = Path.Combine(ProjectSettingsDirectory, LegacyManifestFileName);
        var useLegacyManifest =
            !File.Exists(canonicalManifestPath) &&
            !File.Exists($"{canonicalManifestPath}.bak") &&
            (File.Exists(legacyManifestPath) || File.Exists($"{legacyManifestPath}.bak"));
        ManifestPath = useLegacyManifest ? legacyManifestPath : canonicalManifestPath;
        ManifestBackupPath = $"{ManifestPath}.bak";
        UiProjectPath = Path.Combine(ProjectSettingsDirectory, UiProjectFileName);
        UiProjectBackupPath = $"{UiProjectPath}.bak";
    }

    public string RootDirectory { get; }

    public string AssetDirectory { get; }

    public string GeneratedAssetDirectory { get; }

    public string PackagesDirectory { get; }

    public string ProjectSettingsDirectory { get; }

    public string ManifestPath { get; }

    public string ManifestBackupPath { get; }

    public string UiProjectPath { get; }

    public string UiProjectBackupPath { get; }

    private static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}

public sealed class ProjectTilemapSettings
{
    public double GridUnitSize { get; set; } = 64d;

    public int ArtImageWidth { get; set; } = 512;

    public int ArtImageHeight { get; set; } = 256;

    public double DiamondAngle { get; set; } = 53.13010235415598d;

    public double RotationAngle { get; set; }
}

public sealed class ProjectManifest
{
    public int SchemaVersion { get; set; } = 1;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = string.Empty;

    public string WorkflowKey { get; set; } = WorkflowCatalog.UiDesign.Key;

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }

    public ProjectTilemapSettings Tilemap { get; set; } = new();
}

public sealed record ProjectWorkspace(ProjectManifest Manifest, ProjectPaths Paths);

public sealed class ProjectWorkspaceService
{
    public const int CurrentManifestSchemaVersion = 1;
    private const int MaximumProjectNameLength = 80;
    private const long MaximumManifestBytes = 1024L * 1024L;

    private static readonly HashSet<string> ReservedWindowsNames = BuildReservedWindowsNames();
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly TimeProvider _timeProvider;

    public ProjectWorkspaceService(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public ProjectWorkspace Create(
        string parentDirectory,
        string projectName,
        string workflowKey,
        ProjectTilemapSettings? tilemap = null)
    {
        var normalizedParent = ValidateParentDirectory(parentDirectory);
        var validatedName = ValidateProjectName(projectName);
        var validatedWorkflow = ValidateWorkflowKey(workflowKey);
        var validatedTilemap = CloneAndValidateTilemap(tilemap ?? new ProjectTilemapSettings());
        var targetRoot = Path.GetFullPath(Path.Combine(normalizedParent, validatedName));
        EnsureDirectChild(normalizedParent, targetRoot);

        if (Directory.Exists(targetRoot) || File.Exists(targetRoot))
        {
            throw new IOException($"项目位置已存在：{targetRoot}");
        }

        var stagingRoot = Path.Combine(
            normalizedParent,
            $".{validatedName}.{Guid.NewGuid():N}.creating");
        EnsureDirectChild(normalizedParent, stagingRoot);
        var stagingCreated = false;
        try
        {
            Directory.CreateDirectory(stagingRoot);
            stagingCreated = true;
            EnsureNotReparsePoint(stagingRoot, "项目暂存目录");

            var stagingPaths = new ProjectPaths(stagingRoot);
            Directory.CreateDirectory(stagingPaths.GeneratedAssetDirectory);
            Directory.CreateDirectory(stagingPaths.PackagesDirectory);
            Directory.CreateDirectory(stagingPaths.ProjectSettingsDirectory);

            var now = _timeProvider.GetUtcNow();
            var manifest = new ProjectManifest
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = validatedName,
                WorkflowKey = validatedWorkflow,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                Tilemap = validatedTilemap
            };
            ValidateManifest(manifest);

            WriteNewFileDurable(
                stagingPaths.ManifestPath,
                JsonSerializer.Serialize(manifest, Options));

            var uiProject = DefaultUiProjectFactory.Create();
            uiProject.Id = manifest.Id;
            uiProject.Name = manifest.Name;
            ProjectValidator.Validate(uiProject);
            WriteNewFileDurable(stagingPaths.UiProjectPath, ProjectJson.Serialize(uiProject));

            if (!TryLoad(stagingRoot, out var stagedManifest, out _, out var reason))
            {
                throw new InvalidDataException($"项目暂存内容校验失败：{reason}");
            }

            Directory.Move(stagingRoot, targetRoot);
            stagingCreated = false;
            return new ProjectWorkspace(stagedManifest, new ProjectPaths(targetRoot));
        }
        finally
        {
            if (stagingCreated &&
                Directory.Exists(stagingRoot) &&
                IsDirectChild(normalizedParent, stagingRoot))
            {
                try
                {
                    Directory.Delete(stagingRoot, recursive: true);
                }
                catch
                {
                    // Preserve the original creation exception. The staging
                    // name is unique and is never treated as a live project.
                }
            }
        }
    }

    public ProjectWorkspace CreateProject(
        string parentDirectory,
        string projectName,
        string workflowKey,
        ProjectTilemapSettings? tilemap = null) =>
        Create(parentDirectory, projectName, workflowKey, tilemap);

    public bool TryLoad(
        string projectRoot,
        out ProjectManifest manifest,
        out ProjectPaths paths) =>
        TryLoad(projectRoot, out manifest, out paths, out _);

    public bool TryLoad(
        string projectRoot,
        out ProjectManifest manifest,
        out ProjectPaths paths,
        out string reason)
    {
        manifest = null!;
        paths = null!;
        reason = string.Empty;
        try
        {
            var candidatePaths = new ProjectPaths(projectRoot);
            ValidateProjectRoot(candidatePaths);
            if (!TryReadManifest(candidatePaths.ManifestPath, out var candidateManifest) &&
                !TryReadManifest(candidatePaths.ManifestBackupPath, out candidateManifest))
            {
                reason = $"项目清单及其备份均缺失或无效：{candidatePaths.ManifestPath}";
                return false;
            }

            if (!TryReadUiProject(candidatePaths.UiProjectPath, candidateManifest!, out _) &&
                !TryReadUiProject(candidatePaths.UiProjectBackupPath, candidateManifest!, out _))
            {
                reason = $"UI 项目文件及其备份均缺失或无效：{candidatePaths.UiProjectPath}";
                return false;
            }

            manifest = candidateManifest!;
            paths = candidatePaths;
            return true;
        }
        catch (Exception exception) when (IsExpectedPathOrDataException(exception))
        {
            reason = exception.Message;
            manifest = null!;
            paths = null!;
            return false;
        }
    }

    public ProjectWorkspace UpdateWorkflow(string projectRoot, string workflowKey)
    {
        var workspace = LoadRequired(projectRoot);
        workspace.Manifest.WorkflowKey = ValidateWorkflowKey(workflowKey);
        workspace.Manifest.UpdatedAtUtc = _timeProvider.GetUtcNow();
        SaveManifest(workspace.Paths, workspace.Manifest);
        return workspace;
    }

    public ProjectWorkspace Load(string projectRoot) => LoadRequired(projectRoot);

    public ProjectWorkspace UpdateTilemap(string projectRoot, ProjectTilemapSettings tilemap)
    {
        ArgumentNullException.ThrowIfNull(tilemap);
        var workspace = LoadRequired(projectRoot);
        workspace.Manifest.Tilemap = CloneAndValidateTilemap(tilemap);
        workspace.Manifest.UpdatedAtUtc = _timeProvider.GetUtcNow();
        SaveManifest(workspace.Paths, workspace.Manifest);
        return workspace;
    }

    public ProjectWorkspace UpdateTilemapSettings(string projectRoot, ProjectTilemapSettings tilemap) =>
        UpdateTilemap(projectRoot, tilemap);

    public ProjectWorkspace UpdateWorkflowAndTilemap(
        string projectRoot,
        string workflowKey,
        ProjectTilemapSettings tilemap)
    {
        ArgumentNullException.ThrowIfNull(tilemap);
        var workspace = LoadRequired(projectRoot);
        workspace.Manifest.WorkflowKey = ValidateWorkflowKey(workflowKey);
        workspace.Manifest.Tilemap = CloneAndValidateTilemap(tilemap);
        workspace.Manifest.UpdatedAtUtc = _timeProvider.GetUtcNow();
        SaveManifest(workspace.Paths, workspace.Manifest);
        return workspace;
    }

    public static string ValidateProjectName(string projectName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectName);
        var candidate = projectName.Trim();
        if (!string.Equals(projectName, candidate, StringComparison.Ordinal))
        {
            throw new ArgumentException("项目名称不能以空白字符开头或结尾。", nameof(projectName));
        }

        if (candidate.Length > MaximumProjectNameLength)
        {
            throw new ArgumentException($"项目名称不能超过 {MaximumProjectNameLength} 个字符。", nameof(projectName));
        }

        if (candidate is "." or ".." ||
            candidate.EndsWith(' ') ||
            candidate.EndsWith('.') ||
            candidate.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            candidate.Contains(Path.DirectorySeparatorChar) ||
            candidate.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new ArgumentException("项目名称包含 Windows 路径不允许的字符或结尾。", nameof(projectName));
        }

        var deviceName = candidate.Split('.', 2)[0].TrimEnd(' ');
        if (ReservedWindowsNames.Contains(deviceName))
        {
            throw new ArgumentException("项目名称不能使用 Windows 保留设备名。", nameof(projectName));
        }

        return candidate;
    }

    private ProjectWorkspace LoadRequired(string projectRoot)
    {
        if (!TryLoad(projectRoot, out var manifest, out var paths, out var reason))
        {
            throw new InvalidDataException($"无法加载项目：{reason}");
        }

        return new ProjectWorkspace(manifest, paths);
    }

    private static string ValidateParentDirectory(string parentDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parentDirectory);
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parentDirectory));
        if (File.Exists(normalized))
        {
            throw new IOException($"项目父目录指向了文件：{normalized}");
        }

        if (!Directory.Exists(normalized))
        {
            throw new DirectoryNotFoundException($"项目父目录不存在：{normalized}");
        }

        EnsureNotReparsePoint(normalized, "项目父目录");
        return normalized;
    }

    private static void ValidateProjectRoot(ProjectPaths paths)
    {
        if (!Directory.Exists(paths.RootDirectory))
        {
            throw new DirectoryNotFoundException($"项目目录不存在：{paths.RootDirectory}");
        }

        var volumeRoot = Path.GetPathRoot(paths.RootDirectory);
        if (!string.IsNullOrWhiteSpace(volumeRoot) && PathEquals(volumeRoot, paths.RootDirectory))
        {
            throw new InvalidDataException("文件系统根目录不能作为 GAssetForge 项目。 ");
        }

        EnsureNotReparsePoint(paths.RootDirectory, "项目目录");
        EnsureRequiredDirectory(paths.RootDirectory, paths.AssetDirectory, "Assets");
        EnsureRequiredDirectory(paths.RootDirectory, paths.GeneratedAssetDirectory, "Assets/Generated");
        EnsureRequiredDirectory(paths.RootDirectory, paths.PackagesDirectory, "Packages");
        EnsureRequiredDirectory(paths.RootDirectory, paths.ProjectSettingsDirectory, "ProjectSettings");
        EnsureRequiredProjectFilePair(
            paths.RootDirectory,
            paths.ManifestPath,
            paths.ManifestBackupPath,
            "项目清单");
        EnsureRequiredProjectFilePair(
            paths.RootDirectory,
            paths.UiProjectPath,
            paths.UiProjectBackupPath,
            "UI 项目文件");
    }

    private static void EnsureRequiredDirectory(string root, string path, string label)
    {
        if (!IsWithin(root, path) || !Directory.Exists(path))
        {
            throw new InvalidDataException($"项目缺少有效的 {label} 目录。 ");
        }

        EnsureNotReparsePoint(path, label);
    }

    private static void EnsureRequiredFile(string root, string path, string label)
    {
        if (!IsWithin(root, path) || !File.Exists(path))
        {
            throw new InvalidDataException($"项目缺少有效的 {label}。 ");
        }

        EnsureNotReparsePoint(path, label);
    }

    private static void EnsureRequiredProjectFilePair(
        string root,
        string primaryPath,
        string backupPath,
        string label)
    {
        var hasPrimary = File.Exists(primaryPath);
        var hasBackup = File.Exists(backupPath);
        if (!hasPrimary && !hasBackup)
        {
            throw new InvalidDataException($"项目缺少有效的 {label}及其备份。 ");
        }

        if (hasPrimary)
        {
            EnsureRequiredFile(root, primaryPath, label);
        }

        if (hasBackup)
        {
            EnsureRequiredFile(root, backupPath, $"{label}备份");
        }
    }

    private static void ValidateManifest(ProjectManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.SchemaVersion != CurrentManifestSchemaVersion)
        {
            throw new InvalidDataException($"不支持的项目清单版本：{manifest.SchemaVersion}。 ");
        }

        if (string.IsNullOrWhiteSpace(manifest.Id) ||
            manifest.Id.Length != 32 ||
            manifest.Id.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidDataException("项目清单 ID 无效。 ");
        }

        _ = ValidateProjectName(manifest.Name);
        manifest.WorkflowKey = ValidateWorkflowKey(manifest.WorkflowKey);
        manifest.Tilemap = CloneAndValidateTilemap(
            manifest.Tilemap ?? throw new InvalidDataException("项目清单缺少 Tilemap 参数。"));
        if (manifest.CreatedAtUtc == default ||
            manifest.UpdatedAtUtc == default ||
            manifest.UpdatedAtUtc < manifest.CreatedAtUtc)
        {
            throw new InvalidDataException("项目清单时间戳无效。 ");
        }
    }

    private static string ValidateWorkflowKey(string workflowKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowKey);
        var workflow = WorkflowCatalog.All.FirstOrDefault(candidate =>
            string.Equals(candidate.Key, workflowKey.Trim(), StringComparison.OrdinalIgnoreCase));
        return workflow?.Key ?? throw new ArgumentException($"未知的业务工作流：{workflowKey}", nameof(workflowKey));
    }

    private static ProjectTilemapSettings CloneAndValidateTilemap(ProjectTilemapSettings tilemap)
    {
        if (!double.IsFinite(tilemap.GridUnitSize) || tilemap.GridUnitSize is < 8d or > 512d)
        {
            throw new InvalidDataException("Tilemap 网格单位大小必须位于 8 到 512 之间。 ");
        }

        if (tilemap.ArtImageWidth is < 16 or > 8192 || tilemap.ArtImageHeight is < 16 or > 8192)
        {
            throw new InvalidDataException("Tilemap 美术图片尺寸必须位于 16 到 8192 像素之间。 ");
        }

        if (!double.IsFinite(tilemap.DiamondAngle) || tilemap.DiamondAngle is < 15d or > 165d)
        {
            throw new InvalidDataException("Tilemap 控制角必须位于 15° 到 165° 之间。 ");
        }

        if (!double.IsFinite(tilemap.RotationAngle) || tilemap.RotationAngle is < -180d or > 180d)
        {
            throw new InvalidDataException("Tilemap 旋转角必须位于 -180° 到 180° 之间。 ");
        }

        return new ProjectTilemapSettings
        {
            GridUnitSize = tilemap.GridUnitSize,
            ArtImageWidth = tilemap.ArtImageWidth,
            ArtImageHeight = tilemap.ArtImageHeight,
            DiamondAngle = tilemap.DiamondAngle,
            RotationAngle = tilemap.RotationAngle
        };
    }

    private static void SaveManifest(ProjectPaths paths, ProjectManifest manifest)
    {
        ValidateProjectRoot(paths);
        ValidateManifest(manifest);
        WriteAtomic(
            paths.ManifestPath,
            paths.ManifestBackupPath,
            JsonSerializer.Serialize(manifest, Options));
    }

    private static void WriteNewFileDurable(string path, string content)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("无法确定新建项目文件目录。 ");
        Directory.CreateDirectory(directory);
        using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            4096,
            FileOptions.WriteThrough);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
        writer.Write(content);
        writer.Flush();
        stream.Flush(flushToDisk: true);
    }

    private static bool TryReadManifest(string path, out ProjectManifest? manifest)
    {
        manifest = null;
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length is <= 0 or > MaximumManifestBytes)
            {
                return false;
            }

            EnsureNotReparsePoint(path, "项目清单文件");
            manifest = JsonSerializer.Deserialize<ProjectManifest>(File.ReadAllText(path), Options);
            if (manifest is null)
            {
                return false;
            }

            ValidateManifest(manifest);
            return true;
        }
        catch (Exception exception) when (IsExpectedPathOrDataException(exception))
        {
            manifest = null;
            return false;
        }
    }

    private static bool TryReadUiProject(
        string path,
        ProjectManifest manifest,
        out UiProjectModel? project)
    {
        project = null;
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            EnsureNotReparsePoint(path, "UI 项目文件");
            project = ProjectJson.Deserialize(File.ReadAllText(path));
            ProjectValidator.Validate(project);
            if (!string.Equals(project.Id, manifest.Id, StringComparison.Ordinal) ||
                !string.Equals(project.Name, manifest.Name, StringComparison.Ordinal))
            {
                project = null;
                return false;
            }

            return true;
        }
        catch (Exception exception) when (
            IsExpectedPathOrDataException(exception) || exception is NullReferenceException)
        {
            project = null;
            return false;
        }
    }

    private static void WriteAtomic(string path, string backupPath, string content)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("无法确定项目清单目录。 ");
        Directory.CreateDirectory(directory);
        EnsureNotReparsePointIfExists(fullPath, "项目清单文件");
        EnsureNotReparsePointIfExists(Path.GetFullPath(backupPath), "项目清单备份");
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            WriteNewFileDurable(temporaryPath, content);
            if (File.Exists(fullPath))
            {
                File.Replace(temporaryPath, fullPath, Path.GetFullPath(backupPath), ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, fullPath);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static void EnsureDirectChild(string parent, string candidate)
    {
        if (!IsDirectChild(parent, candidate))
        {
            throw new InvalidDataException("项目目录必须是所选位置的直接子目录。 ");
        }
    }

    private static bool IsDirectChild(string parent, string candidate)
    {
        var candidateParent = Directory.GetParent(Path.GetFullPath(candidate))?.FullName;
        return candidateParent is not null && PathEquals(parent, candidateParent);
    }

    private static bool IsWithin(string root, string candidate)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(candidate));
        return !Path.IsPathRooted(relative) &&
               !string.Equals(relative, "..", StringComparison.Ordinal) &&
               !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
               !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static bool PathEquals(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static void EnsureNotReparsePoint(string path, string label)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException($"{label} 不能是符号链接或目录联接。 ");
        }
    }

    private static void EnsureNotReparsePointIfExists(string path, string label)
    {
        if (File.Exists(path) || Directory.Exists(path))
        {
            EnsureNotReparsePoint(path, label);
        }
    }

    private static HashSet<string> BuildReservedWindowsNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL", "CLOCK$"
        };
        for (var index = 1; index <= 9; index++)
        {
            names.Add($"COM{index}");
            names.Add($"LPT{index}");
        }

        return names;
    }

    private static bool IsExpectedPathOrDataException(Exception exception) => exception is
        ArgumentException or
        InvalidDataException or
        IOException or
        JsonException or
        UnauthorizedAccessException or
        NotSupportedException or
        System.Security.SecurityException;
}
