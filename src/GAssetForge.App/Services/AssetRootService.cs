using System.IO;

namespace GAssetForge.App.Services;

public sealed record AssetRootResolution(
    string? ConfiguredPath,
    string EffectivePath,
    bool IsUsingFallback,
    string Message);

public sealed class AssetRootService(WorkspacePaths paths)
{
    public string DefaultRootPath => paths.AssetDirectory;

    public AssetRootResolution Resolve(string? configuredPath)
    {
        var defaultPath = EnsureDefaultRoot();
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return new AssetRootResolution(null, defaultPath, false, "正在使用应用默认 Assets 目录");
        }

        if (!TryNormalizeDirectory(configuredPath, out var externalPath, out var reason))
        {
            return new AssetRootResolution(configuredPath, defaultPath, true, $"已回退默认目录：{reason}");
        }

        if (PathEquals(externalPath, defaultPath))
        {
            return new AssetRootResolution(null, defaultPath, false, "正在使用应用默认 Assets 目录");
        }

        return new AssetRootResolution(externalPath, externalPath, false, "已加载外部资产目录");
    }

    public bool TryNormalizeDirectory(string path, out string normalizedPath, out string reason)
    {
        normalizedPath = string.Empty;
        reason = string.Empty;
        try
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                reason = "路径为空";
                return false;
            }

            normalizedPath = Normalize(path);
            var volumeRoot = Path.GetPathRoot(normalizedPath);
            if (!string.IsNullOrEmpty(volumeRoot) && PathEquals(normalizedPath, volumeRoot))
            {
                reason = "不能直接把磁盘根目录作为资产目录";
                return false;
            }

            if (!Directory.Exists(normalizedPath))
            {
                reason = File.Exists(normalizedPath) ? "选择的路径是文件" : "目录不存在或当前不可用";
                return false;
            }

            if ((File.GetAttributes(normalizedPath) & FileAttributes.ReparsePoint) != 0)
            {
                reason = "暂不支持把符号链接或目录联接作为资产目录";
                return false;
            }

            using var enumerator = Directory.EnumerateFileSystemEntries(normalizedPath).GetEnumerator();
            _ = enumerator.MoveNext();
            return true;
        }
        catch (Exception exception) when (exception is
            ArgumentException or
            NotSupportedException or
            PathTooLongException or
            UnauthorizedAccessException or
            IOException)
        {
            reason = exception.Message;
            normalizedPath = string.Empty;
            return false;
        }
    }

    public static bool IsWithinRoot(string rootPath, string candidatePath)
    {
        try
        {
            var root = Normalize(rootPath);
            var candidate = Normalize(candidatePath);
            var relative = Path.GetRelativePath(root, candidate);
            return string.Equals(relative, ".", StringComparison.Ordinal) ||
                   (!Path.IsPathRooted(relative) &&
                    !string.Equals(relative, "..", StringComparison.Ordinal) &&
                    !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                    !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal));
        }
        catch
        {
            return false;
        }
    }

    public static bool PathEquals(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        try
        {
            return string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is
            ArgumentException or
            NotSupportedException or
            PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>
    /// Rejects any existing symbolic link, junction or reparse point between a trusted root and
    /// a candidate. The candidate may be a file, directory, or a not-yet-created descendant.
    /// </summary>
    public static void EnsureExistingPathSegmentsAreNotReparsePoints(
        string trustedRoot,
        string candidatePath)
    {
        var root = Normalize(trustedRoot);
        var candidate = Path.GetFullPath(candidatePath);
        if (!Directory.Exists(root) || !IsWithinRoot(root, candidate))
        {
            throw new InvalidDataException("路径不在现有受信根目录内。");
        }

        EnsureNotReparsePoint(root);
        var segments = Path.GetRelativePath(root, candidate).Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        for (var index = 0; index < segments.Length; index++)
        {
            current = Path.Combine(current, segments[index]);
            var isDirectory = Directory.Exists(current);
            var isFile = File.Exists(current);
            if (!isDirectory && !isFile)
            {
                break;
            }

            EnsureNotReparsePoint(current);
            if (isFile && index < segments.Length - 1)
            {
                throw new IOException($"路径中间段被文件占用：{current}");
            }
        }
    }

    /// <summary>
    /// Creates a directory below a trusted root one segment at a time while rejecting files and
    /// reparse points. This prevents lexical in-root paths from escaping through a junction.
    /// </summary>
    public static void EnsureManagedDirectory(string trustedRoot, string targetDirectory)
    {
        var root = Normalize(trustedRoot);
        var target = Normalize(targetDirectory);
        if (!Directory.Exists(root) || !IsWithinRoot(root, target))
        {
            throw new InvalidDataException("受管目录试图离开现有受信根目录。");
        }

        EnsureNotReparsePoint(root);
        if (PathEquals(root, target))
        {
            return;
        }

        var current = root;
        foreach (var segment in Path.GetRelativePath(root, target).Split(
                     new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (File.Exists(current))
            {
                throw new IOException($"受管目录路径被文件占用：{current}");
            }

            if (!Directory.Exists(current))
            {
                Directory.CreateDirectory(current);
            }

            EnsureNotReparsePoint(current);
        }
    }

    private string EnsureDefaultRoot()
    {
        try
        {
            Directory.CreateDirectory(paths.AssetDirectory);
            Directory.CreateDirectory(paths.AiAssetDirectory);
            Directory.CreateDirectory(paths.FinalAssetDirectory);
            Directory.CreateDirectory(paths.UserAssetDirectory);
            return Normalize(paths.AssetDirectory);
        }
        catch (Exception exception) when (exception is
            UnauthorizedAccessException or
            IOException or
            ArgumentException or
            NotSupportedException)
        {
            throw new InvalidOperationException($"无法创建默认资产目录：{paths.AssetDirectory}", exception);
        }
    }

    private static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static void EnsureNotReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException($"受管路径不能经过符号链接或目录联接：{path}");
        }
    }
}
