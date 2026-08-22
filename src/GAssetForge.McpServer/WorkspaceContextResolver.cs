using System.Text.Json;
using GAssetForge.Core.Generation;
using GAssetForge.Core.Workflows;

namespace GAssetForge.McpServer;

internal sealed record TilemapSettings(
    double GridUnitSize,
    int ArtImageWidth,
    int ArtImageHeight,
    double DiamondAngle,
    double RotationAngle);

internal sealed record AssetRootContext(
    string? ConfiguredPath,
    string EffectivePath,
    string CurrentPath,
    bool IsFallback,
    string Message);

internal sealed record ValidatedAssetPath(
    string RelativePath,
    string FullPath);

internal sealed record WorkspaceContext(
    WorkflowDefinition ActiveWorkflow,
    AssetRootContext Assets,
    TilemapSettings Tilemap,
    bool SettingsFileExists,
    string SettingsFilePath);

internal sealed class WorkspaceContextResolver(ServerOptions options)
{
    private const double DefaultGridUnitSize = 64d;
    private const int DefaultArtImageWidth = 512;
    private const int DefaultArtImageHeight = 256;
    private const double DefaultDiamondAngle = 53.13010235415598d;
    private const double DefaultRotationAngle = 0d;

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    public WorkspaceContext Resolve()
    {
        SettingsSnapshot settings;
        var settingsFileExists = File.Exists(options.SettingsFilePath);
        try
        {
            settings = settingsFileExists ? ReadSettings(options.SettingsFilePath) : new SettingsSnapshot();
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[GAssetForge MCP] 无法读取用户设置，使用默认值：{exception.Message}");
            settings = new SettingsSnapshot();
        }

        var assets = ResolveAssetRoot(settings.AssetRootPath, settings.AssetCurrentPath);
        var workflow = WorkflowCatalog.Find(settings.ActiveWorkflowKey);
        var tilemap = new TilemapSettings(
            ClampFinite(settings.TilemapGridUnitSize, DefaultGridUnitSize, 8d, 512d),
            Math.Clamp(settings.TilemapArtImageWidth ?? DefaultArtImageWidth, 16, 8192),
            Math.Clamp(settings.TilemapArtImageHeight ?? DefaultArtImageHeight, 16, 8192),
            ClampFinite(settings.TilemapDiamondAngle, DefaultDiamondAngle, 15d, 165d),
            ClampFinite(settings.TilemapRotationAngle, DefaultRotationAngle, -180d, 180d));

        return new WorkspaceContext(workflow, assets, tilemap, settingsFileExists, options.SettingsFilePath);
    }

    public IReadOnlyList<string> ValidateCompletedAssetPaths(IEnumerable<string> assetPaths)
    {
        var assetRoot = Resolve().Assets.EffectivePath;
        return ValidateCompletedAssetPaths(assetPaths, assetRoot);
    }

    public IReadOnlyList<string> ValidateCompletedAssetPaths(
        IEnumerable<string> assetPaths,
        string assetRootSnapshot)
        => ValidateAssetPaths(assetPaths, assetRootSnapshot, requireExistingFiles: true, "完成产物");

    public IReadOnlyList<string> ValidatePlannedAssetPaths(
        IEnumerable<string> assetPaths,
        string assetRootSnapshot)
        => ValidateAssetPaths(assetPaths, assetRootSnapshot, requireExistingFiles: false, "交付产物");

    public ValidatedAssetPath ResolveDeliveryAssetPath(
        string suppliedPath,
        string assetRootSnapshot,
        bool requireExistingFile,
        string pathRole)
    {
        var assetRoot = NormalizeUsableAssetRoot(assetRootSnapshot);
        if (string.IsNullOrWhiteSpace(suppliedPath))
        {
            throw new InvalidDataException($"{pathRole}路径不能为空。");
        }

        if (Path.IsPathRooted(suppliedPath) && !Path.IsPathFullyQualified(suppliedPath))
        {
            throw new InvalidDataException($"{pathRole}不能使用部分限定路径：{suppliedPath}");
        }

        var candidate = Path.IsPathFullyQualified(suppliedPath)
            ? Path.GetFullPath(suppliedPath)
            : Path.GetFullPath(Path.Combine(assetRoot, suppliedPath));

        if (!IsWithinRoot(assetRoot, candidate))
        {
            throw new InvalidDataException($"{pathRole}必须位于冻结的 Assets 根目录内：{suppliedPath}");
        }

        EnsureNoReparsePointsAllowingMissing(
            assetRoot,
            candidate,
            allowMissingSegments: !requireExistingFile,
            pathRole);
        if (requireExistingFile && !File.Exists(candidate))
        {
            throw new FileNotFoundException($"{pathRole}文件不存在：{suppliedPath}", candidate);
        }

        if (Directory.Exists(candidate))
        {
            throw new InvalidDataException($"{pathRole}必须是文件而不是目录：{suppliedPath}");
        }

        var relativePath = GenerationInputValidator.NormalizeRelativeAssetPath(
            Path.GetRelativePath(assetRoot, candidate).Replace('\\', '/'));
        return new ValidatedAssetPath(relativePath, candidate);
    }

    private IReadOnlyList<string> ValidateAssetPaths(
        IEnumerable<string> assetPaths,
        string assetRootSnapshot,
        bool requireExistingFiles,
        string pathRole)
    {
        ArgumentNullException.ThrowIfNull(assetPaths);
        var results = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var suppliedPath in assetPaths)
        {
            var resolved = ResolveDeliveryAssetPath(
                suppliedPath,
                assetRootSnapshot,
                requireExistingFiles,
                pathRole);
            if (!seen.Add(resolved.RelativePath))
            {
                throw new InvalidDataException($"{pathRole}路径不能重复：{suppliedPath}");
            }

            results.Add(resolved.RelativePath);
        }

        if (results.Count == 0)
        {
            throw new InvalidDataException($"至少需要一个有效的{pathRole}文件。");
        }

        return results;
    }

    public void ValidateDeliveryAssetRootForCreation(string assetRootSnapshot)
    {
        var normalizedSnapshot = NormalizeUsableAssetRoot(assetRootSnapshot);
        var currentRoot = Resolve().Assets.EffectivePath;
        if (!PathEquals(normalizedSnapshot, currentRoot))
        {
            throw new InvalidDataException(
                "交付计划的 AssetRoot 必须等于创建任务时当前有效的 Assets 根目录。");
        }
    }

    private AssetRootContext ResolveAssetRoot(string? configuredPath, string? configuredCurrentPath)
    {
        var defaultRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.DefaultAssetRoot));
        Directory.CreateDirectory(defaultRoot);

        var effectiveRoot = defaultRoot;
        var isFallback = false;
        var message = "正在使用应用默认 Assets 目录";
        string? normalizedConfiguredPath = null;

        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            normalizedConfiguredPath = TryResolveUsableDirectory(configuredPath, out var failureReason);
            if (normalizedConfiguredPath is null)
            {
                isFallback = true;
                message = $"已回退默认目录：{failureReason}";
            }
            else if (!PathEquals(normalizedConfiguredPath, defaultRoot))
            {
                effectiveRoot = normalizedConfiguredPath;
                message = "已加载外部资产目录";
            }
            else
            {
                normalizedConfiguredPath = null;
            }
        }

        var currentPath = effectiveRoot;
        if (!string.IsNullOrWhiteSpace(configuredCurrentPath))
        {
            try
            {
                var normalizedCurrentPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(configuredCurrentPath));
                if (Directory.Exists(normalizedCurrentPath) && IsWithinRoot(effectiveRoot, normalizedCurrentPath))
                {
                    EnsureNoReparsePoints(effectiveRoot, normalizedCurrentPath);
                    currentPath = normalizedCurrentPath;
                }
            }
            catch (Exception exception) when (exception is
                ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"[GAssetForge MCP] 忽略无效的当前资产目录：{exception.Message}");
            }
        }

        return new AssetRootContext(normalizedConfiguredPath, effectiveRoot, currentPath, isFallback, message);
    }

    private static SettingsSnapshot ReadSettings(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        using var document = JsonDocument.Parse(stream, DocumentOptions);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("用户设置根节点必须是对象。");
        }

        var root = document.RootElement;
        return new SettingsSnapshot
        {
            ActiveWorkflowKey = ReadString(root, "activeWorkflowKey"),
            AssetRootPath = ReadString(root, "assetRootPath"),
            AssetCurrentPath = ReadString(root, "assetCurrentPath"),
            TilemapGridUnitSize = ReadDouble(root, "tilemapGridUnitSize"),
            TilemapArtImageWidth = ReadInt32(root, "tilemapArtImageWidth"),
            TilemapArtImageHeight = ReadInt32(root, "tilemapArtImageHeight"),
            TilemapDiamondAngle = ReadDouble(root, "tilemapDiamondAngle"),
            TilemapRotationAngle = ReadDouble(root, "tilemapRotationAngle")
        };
    }

    private static string? TryResolveUsableDirectory(string path, out string reason)
    {
        try
        {
            var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            var volumeRoot = Path.GetPathRoot(normalized);
            if (!string.IsNullOrEmpty(volumeRoot) && PathEquals(normalized, volumeRoot))
            {
                reason = "不能直接把磁盘根目录作为资产目录";
                return null;
            }

            if (!Directory.Exists(normalized))
            {
                reason = File.Exists(normalized) ? "配置路径是文件" : "配置目录不存在或不可用";
                return null;
            }

            if ((File.GetAttributes(normalized) & FileAttributes.ReparsePoint) != 0)
            {
                reason = "配置目录不能是符号链接或目录联接";
                return null;
            }

            reason = string.Empty;
            return normalized;
        }
        catch (Exception exception) when (exception is
            ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException)
        {
            reason = exception.Message;
            return null;
        }
    }

    private static string NormalizeUsableAssetRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            throw new InvalidDataException("交付计划的 AssetRoot 必须是绝对路径。");
        }

        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var volumeRoot = Path.GetPathRoot(normalized);
        if (!string.IsNullOrWhiteSpace(volumeRoot) && PathEquals(normalized, volumeRoot))
        {
            throw new InvalidDataException("不能把文件系统根目录作为交付 AssetRoot。");
        }

        if (!Directory.Exists(normalized))
        {
            throw new DirectoryNotFoundException($"交付 AssetRoot 不存在：{normalized}");
        }

        if ((File.GetAttributes(normalized) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("交付 AssetRoot 不能是符号链接或目录联接。");
        }

        return normalized;
    }

    private static void EnsureNoReparsePoints(string rootPath, string candidatePath)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        var candidate = Path.GetFullPath(candidatePath);
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("Assets 根目录不能是符号链接或目录联接。");
        }

        var relative = Path.GetRelativePath(root, candidate);
        var current = root;
        foreach (var segment in relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException($"完成产物路径不能经过符号链接或目录联接：{candidatePath}");
            }
        }
    }

    private static void EnsureNoReparsePointsAllowingMissing(
        string rootPath,
        string candidatePath,
        bool allowMissingSegments,
        string pathRole)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        var candidate = Path.GetFullPath(candidatePath);
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("Assets 根目录不能是符号链接或目录联接。");
        }

        var relative = Path.GetRelativePath(root, candidate);
        var segments = relative.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        for (var index = 0; index < segments.Length; index++)
        {
            current = Path.Combine(current, segments[index]);
            var isFile = File.Exists(current);
            var isDirectory = Directory.Exists(current);
            if (!isFile && !isDirectory)
            {
                if (allowMissingSegments)
                {
                    return;
                }

                throw new FileNotFoundException($"{pathRole}路径不存在：{candidatePath}", candidatePath);
            }

            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException($"{pathRole}路径不能经过符号链接或目录联接：{candidatePath}");
            }

            if (isFile && index < segments.Length - 1)
            {
                throw new InvalidDataException($"{pathRole}路径的父级不能是文件：{candidatePath}");
            }
        }
    }

    private static bool IsWithinRoot(string rootPath, string candidatePath)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(rootPath), Path.GetFullPath(candidatePath));
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

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static double? ReadDouble(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)
            ? number
            : null;

    private static int? ReadInt32(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : null;

    private static double ClampFinite(double? value, double fallback, double minimum, double maximum) =>
        value is { } number && double.IsFinite(number) ? Math.Clamp(number, minimum, maximum) : fallback;

    private sealed class SettingsSnapshot
    {
        public string? ActiveWorkflowKey { get; init; }
        public string? AssetRootPath { get; init; }
        public string? AssetCurrentPath { get; init; }
        public double? TilemapGridUnitSize { get; init; }
        public int? TilemapArtImageWidth { get; init; }
        public int? TilemapArtImageHeight { get; init; }
        public double? TilemapDiamondAngle { get; init; }
        public double? TilemapRotationAngle { get; init; }
    }
}
