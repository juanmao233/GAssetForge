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

    private string EnsureDefaultRoot()
    {
        try
        {
            Directory.CreateDirectory(paths.AssetDirectory);
            Directory.CreateDirectory(paths.GeneratedAssetDirectory);
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
}
