using System.IO;
using SixLabors.ImageSharp;

namespace GAssetForge.App.Services;

public interface IImageProcessingPreviewCache
{
    string RootDirectory { get; }

    string ReservePreviewPath(string toolId);

    bool IsManagedPreviewPath(string path);

    bool PinPreview(string previewPath);

    bool UnpinPreview(string previewPath);

    bool ReleasePreview(string previewPath);

    string PublishPreview(string previewPath, string outputPath, bool overwrite = false);

    ImageProcessingPreviewCleanupResult CleanupExpiredAndExcessPreviews();
}

public sealed record ImageProcessingPreviewCleanupResult(
    int ScannedFileCount,
    int RemovedFileCount,
    long RemovedByteCount,
    int FailedRemovalCount);

/// <summary>
/// Owns short-lived PNG files used by image-processing tool previews. Preview generation writes
/// directly to a path returned by <see cref="ReservePreviewPath"/>; publishing copies that complete
/// PNG to the requested destination through a same-directory temporary file and atomic rename.
/// </summary>
public sealed class ImageProcessingPreviewCache : IImageProcessingPreviewCache
{
    public const string DefaultDirectoryName = "ImageProcessingPreviews";
    public static readonly TimeSpan DefaultMaximumAge = TimeSpan.FromDays(2);
    public const int DefaultMaximumFileCount = 128;
    public const long DefaultMaximumTotalBytes = 512L * 1024L * 1024L;

    private const int CopyBufferSize = 128 * 1024;
    private static readonly byte[] PngSignature =
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A
    ];

    private readonly object _gate = new();
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _maximumAge;
    private readonly int _maximumFileCount;
    private readonly long _maximumTotalBytes;
    private readonly HashSet<string> _pinnedPreviewPaths = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly StringComparison _pathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    public ImageProcessingPreviewCache(
        WorkspacePaths paths,
        TimeProvider? timeProvider = null,
        TimeSpan? maximumAge = null,
        int maximumFileCount = DefaultMaximumFileCount,
        long maximumTotalBytes = DefaultMaximumTotalBytes)
        : this(
            (paths ?? throw new ArgumentNullException(nameof(paths))).ImageProcessingPreviewDirectory,
            timeProvider,
            maximumAge,
            maximumFileCount,
            maximumTotalBytes)
    {
    }

    public ImageProcessingPreviewCache(
        string rootDirectory,
        TimeProvider? timeProvider = null,
        TimeSpan? maximumAge = null,
        int maximumFileCount = DefaultMaximumFileCount,
        long maximumTotalBytes = DefaultMaximumTotalBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        var resolvedMaximumAge = maximumAge ?? DefaultMaximumAge;
        if (resolvedMaximumAge <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumAge), "预览缓存保留时间必须大于零。");
        }

        if (maximumFileCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumFileCount), "预览缓存文件数量上限必须大于零。");
        }

        if (maximumTotalBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumTotalBytes), "预览缓存容量上限必须大于零。");
        }

        RootDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory.Trim()));
        var fileSystemRoot = Path.GetPathRoot(RootDirectory);
        if (!string.IsNullOrWhiteSpace(fileSystemRoot) &&
            string.Equals(
                RootDirectory,
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(fileSystemRoot)),
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
        {
            throw new ArgumentException("文件系统根目录不能作为预览缓存目录。", nameof(rootDirectory));
        }

        _timeProvider = timeProvider ?? TimeProvider.System;
        _maximumAge = resolvedMaximumAge;
        _maximumFileCount = maximumFileCount;
        _maximumTotalBytes = maximumTotalBytes;
        EnsureCacheRootIsSafe();
    }

    public string RootDirectory { get; }

    public string ReservePreviewPath(string toolId)
    {
        var normalizedToolId = NormalizeToolId(toolId);
        lock (_gate)
        {
            EnsureCacheRootIsSafe();
            _ = CleanupCore();

            var toolDirectory = NormalizeManagedPath(Path.Combine(RootDirectory, normalizedToolId));
            if (File.Exists(toolDirectory))
            {
                throw new IOException($"预览缓存工具目录被同名文件占用：{toolDirectory}");
            }

            Directory.CreateDirectory(toolDirectory);
            EnsureDirectoryIsNotReparsePoint(toolDirectory, "预览缓存工具目录");

            while (true)
            {
                var candidate = NormalizeManagedPngPath(
                    Path.Combine(toolDirectory, $"{Guid.NewGuid():N}.png"));
                if (!File.Exists(candidate) && !Directory.Exists(candidate))
                {
                    return candidate;
                }
            }
        }
    }

    public bool IsManagedPreviewPath(string path)
    {
        try
        {
            var normalizedPath = NormalizeManagedPngPath(path);
            return !Directory.Exists(normalizedPath) &&
                   (!File.Exists(normalizedPath) || !IsReparsePoint(normalizedPath));
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public bool PinPreview(string previewPath)
    {
        lock (_gate)
        {
            EnsureCacheRootIsSafe();
            var normalizedPreview = NormalizeManagedPngPath(previewPath);
            if (Directory.Exists(normalizedPreview))
            {
                throw new IOException($"预览缓存路径是目录而不是 PNG 文件：{normalizedPreview}");
            }

            if (File.Exists(normalizedPreview))
            {
                EnsureFileIsNotReparsePoint(normalizedPreview, "预览缓存文件");
            }

            // A reserved path may be pinned before the renderer creates it. This closes the
            // shared-cache cleanup race while another tool is producing a large preview.
            return _pinnedPreviewPaths.Add(normalizedPreview);
        }
    }

    public bool UnpinPreview(string previewPath)
    {
        lock (_gate)
        {
            EnsureCacheRootIsSafe();
            var normalizedPreview = NormalizeManagedPngPath(previewPath);
            return _pinnedPreviewPaths.Remove(normalizedPreview);
        }
    }

    public bool ReleasePreview(string previewPath)
    {
        lock (_gate)
        {
            EnsureCacheRootIsSafe();
            var normalizedPreview = NormalizeManagedPngPath(previewPath);
            _pinnedPreviewPaths.Remove(normalizedPreview);
            if (Directory.Exists(normalizedPreview))
            {
                throw new IOException($"预览缓存路径是目录而不是 PNG 文件：{normalizedPreview}");
            }

            if (!File.Exists(normalizedPreview))
            {
                return false;
            }

            EnsureFileIsNotReparsePoint(normalizedPreview, "预览缓存文件");
            File.Delete(normalizedPreview);
            TryDeleteEmptyManagedParent(normalizedPreview);
            return true;
        }
    }

    public string PublishPreview(string previewPath, string outputPath, bool overwrite = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        lock (_gate)
        {
            EnsureCacheRootIsSafe();
            var normalizedPreview = NormalizeManagedPngPath(previewPath);
            var normalizedOutput = Path.GetFullPath(outputPath.Trim());
            if (!string.Equals(Path.GetExtension(normalizedOutput), ".png", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("预览图片的最终输出必须是 PNG 文件。", nameof(outputPath));
            }

            if (PathsEqual(normalizedPreview, normalizedOutput))
            {
                throw new ArgumentException("预览缓存路径与最终输出路径不能相同。", nameof(outputPath));
            }

            if (!File.Exists(normalizedPreview))
            {
                throw new FileNotFoundException("待发布的预览缓存 PNG 不存在。", normalizedPreview);
            }

            if (Directory.Exists(normalizedOutput))
            {
                throw new IOException($"最终输出路径是目录而不是 PNG 文件：{normalizedOutput}");
            }

            EnsureFileIsNotReparsePoint(normalizedPreview, "预览缓存文件");
            if (File.Exists(normalizedOutput) && !overwrite)
            {
                throw new IOException($"最终输出文件已经存在：{normalizedOutput}");
            }

            var outputDirectory = Path.GetDirectoryName(normalizedOutput)
                ?? throw new InvalidOperationException("无法解析预览图片最终输出目录。");
            Directory.CreateDirectory(outputDirectory);
            var temporaryPath = Path.Combine(
                outputDirectory,
                $".{Path.GetFileName(normalizedOutput)}.{Guid.NewGuid():N}.tmp");
            try
            {
                CopyVerifiedPng(normalizedPreview, temporaryPath);
                File.Move(temporaryPath, normalizedOutput, overwrite);
                return normalizedOutput;
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }
    }

    public ImageProcessingPreviewCleanupResult CleanupExpiredAndExcessPreviews()
    {
        lock (_gate)
        {
            EnsureCacheRootIsSafe();
            return CleanupCore();
        }
    }

    private ImageProcessingPreviewCleanupResult CleanupCore()
    {
        var files = EnumerateOwnedPreviewFiles();
        var now = _timeProvider.GetUtcNow();
        var removedCount = 0;
        long removedBytes = 0;
        var failedCount = 0;
        var retained = new List<PreviewCacheFile>(files.Count);

        foreach (var file in files)
        {
            if (IsPinned(file.Path) || now - file.LastWriteTimeUtc <= _maximumAge)
            {
                retained.Add(file);
                continue;
            }

            if (TryDeleteOwnedPreview(file))
            {
                removedCount++;
                removedBytes += file.Length;
            }
            else
            {
                failedCount++;
                retained.Add(file);
            }
        }

        var retainedCount = retained.Count;
        var retainedBytes = retained.Sum(file => file.Length);
        foreach (var file in retained.OrderBy(file => file.LastWriteTimeUtc))
        {
            if (retainedCount <= _maximumFileCount && retainedBytes <= _maximumTotalBytes)
            {
                break;
            }

            if (IsPinned(file.Path))
            {
                continue;
            }

            if (TryDeleteOwnedPreview(file))
            {
                removedCount++;
                removedBytes += file.Length;
                retainedCount--;
                retainedBytes -= file.Length;
            }
            else
            {
                failedCount++;
            }
        }

        DeleteEmptyToolDirectoriesBestEffort();
        return new ImageProcessingPreviewCleanupResult(
            files.Count,
            removedCount,
            removedBytes,
            failedCount);
    }

    private List<PreviewCacheFile> EnumerateOwnedPreviewFiles()
    {
        var files = new List<PreviewCacheFile>();
        foreach (var directory in Directory.EnumerateDirectories(
                     RootDirectory,
                     "*",
                     SearchOption.TopDirectoryOnly))
        {
            var normalizedDirectory = NormalizeManagedPath(directory);
            if (IsReparsePoint(normalizedDirectory))
            {
                continue;
            }

            foreach (var path in Directory.EnumerateFiles(
                         normalizedDirectory,
                         "*.png",
                         SearchOption.TopDirectoryOnly))
            {
                var normalizedPath = NormalizeManagedPngPath(path);
                if (IsReparsePoint(normalizedPath))
                {
                    continue;
                }

                try
                {
                    var info = new FileInfo(normalizedPath);
                    files.Add(new PreviewCacheFile(
                        normalizedPath,
                        info.Length,
                        new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero)));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // A renderer may still be completing the file. The next cleanup pass can retry it.
                }
            }
        }

        return files;
    }

    private bool TryDeleteOwnedPreview(PreviewCacheFile file)
    {
        try
        {
            var normalizedPath = NormalizeManagedPngPath(file.Path);
            if (_pinnedPreviewPaths.Contains(normalizedPath))
            {
                return false;
            }

            if (!File.Exists(normalizedPath) || IsReparsePoint(normalizedPath))
            {
                return !File.Exists(normalizedPath);
            }

            File.Delete(normalizedPath);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void CopyVerifiedPng(string sourcePath, string temporaryPath)
    {
        using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            CopyBufferSize,
            FileOptions.SequentialScan);
        Span<byte> signature = stackalloc byte[PngSignature.Length];
        if (source.Read(signature) != signature.Length || !signature.SequenceEqual(PngSignature))
        {
            throw new InvalidDataException("预览缓存文件不是有效的 PNG 数据。");
        }

        ValidateCompletePng(source);
        source.Position = 0;
        using var destination = new FileStream(
            temporaryPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            CopyBufferSize,
            FileOptions.WriteThrough);
        source.CopyTo(destination, CopyBufferSize);
        destination.Flush(flushToDisk: true);
    }

    private static void ValidateCompletePng(Stream source)
    {
        try
        {
            source.Position = 0;
            var imageInfo = Image.Identify(source)
                ?? throw new InvalidDataException("预览缓存 PNG 无法读取图像信息。");
            if (imageInfo.Width <= 0 || imageInfo.Height <= 0 ||
                !string.Equals(
                    imageInfo.Metadata.DecodedImageFormat?.Name,
                    "PNG",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("预览缓存文件不是结构有效的 PNG 图片。");
            }

            // Identify validates metadata and dimensions; fully decoding the image additionally
            // verifies chunk structure, compressed pixel data and scanline contents before publish.
            source.Position = 0;
            using var decodedImage = Image.Load(source);
            if (decodedImage.Width != imageInfo.Width || decodedImage.Height != imageInfo.Height ||
                !string.Equals(
                    decodedImage.Metadata.DecodedImageFormat?.Name,
                    "PNG",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("预览缓存 PNG 的解码结果与图像头信息不一致。");
            }
        }
        catch (Exception exception) when (exception is
            ImageFormatException or
            ArgumentException or
            InvalidOperationException or
            NotSupportedException or
            EndOfStreamException or
            OverflowException)
        {
            throw new InvalidDataException($"预览缓存 PNG 结构损坏：{exception.Message}", exception);
        }
    }

    private bool IsPinned(string previewPath) =>
        _pinnedPreviewPaths.Contains(Path.GetFullPath(previewPath));

    private string NormalizeManagedPngPath(string path)
    {
        var normalizedPath = NormalizeManagedPath(path);
        if (!string.Equals(Path.GetExtension(normalizedPath), ".png", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("预览缓存路径必须使用 .png 扩展名。", nameof(path));
        }

        return normalizedPath;
    }

    private string NormalizeManagedPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var normalizedPath = Path.GetFullPath(path.Trim());
        if (PathsEqual(normalizedPath, RootDirectory) || !IsWithinRoot(normalizedPath))
        {
            throw new ArgumentException("只能管理预览缓存根目录内的路径。", nameof(path));
        }

        EnsureExistingManagedAncestorsAreSafe(normalizedPath);
        return normalizedPath;
    }

    private void EnsureExistingManagedAncestorsAreSafe(string path)
    {
        EnsureDirectoryIsNotReparsePoint(RootDirectory, "预览缓存根目录");
        var parent = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(parent) || PathsEqual(parent, RootDirectory))
        {
            return;
        }

        var relativeParent = Path.GetRelativePath(RootDirectory, parent);
        var current = RootDirectory;
        foreach (var segment in relativeParent.Split(
                     new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (File.Exists(current))
            {
                throw new IOException($"预览缓存目录路径被文件占用：{current}");
            }

            if (Directory.Exists(current))
            {
                EnsureDirectoryIsNotReparsePoint(current, "预览缓存子目录");
            }
        }
    }

    private void EnsureCacheRootIsSafe()
    {
        if (File.Exists(RootDirectory))
        {
            throw new IOException($"预览缓存根目录被同名文件占用：{RootDirectory}");
        }

        Directory.CreateDirectory(RootDirectory);
        EnsureDirectoryIsNotReparsePoint(RootDirectory, "预览缓存根目录");
    }

    private static void EnsureDirectoryIsNotReparsePoint(string path, string description)
    {
        if (IsReparsePoint(path))
        {
            throw new IOException($"{description}不能是符号链接或重解析点：{path}");
        }
    }

    private static void EnsureFileIsNotReparsePoint(string path, string description)
    {
        if (IsReparsePoint(path))
        {
            throw new IOException($"{description}不能是符号链接或重解析点：{path}");
        }
    }

    private static bool IsReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private bool IsWithinRoot(string path) =>
        path.StartsWith(RootDirectory + Path.DirectorySeparatorChar, _pathComparison);

    private bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            _pathComparison);

    private static string NormalizeToolId(string toolId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolId);
        var normalized = toolId.Trim().ToLowerInvariant();
        if (normalized.Length > 64 || normalized is "." or "..")
        {
            throw new ArgumentException("图像处理工具 ID 长度必须位于 1 到 64 个字符之间。", nameof(toolId));
        }

        if (normalized.Any(character =>
                !char.IsAsciiLetterOrDigit(character) &&
                character is not '-' and not '_' and not '.'))
        {
            throw new ArgumentException(
                "图像处理工具 ID 只能包含 ASCII 字母、数字、连字符、下划线和点。",
                nameof(toolId));
        }

        return normalized;
    }

    private void TryDeleteEmptyManagedParent(string previewPath)
    {
        var directory = Path.GetDirectoryName(previewPath);
        if (string.IsNullOrWhiteSpace(directory) || PathsEqual(directory, RootDirectory))
        {
            return;
        }

        try
        {
            var normalizedDirectory = NormalizeManagedPath(directory);
            if (!IsReparsePoint(normalizedDirectory) &&
                !Directory.EnumerateFileSystemEntries(normalizedDirectory).Any())
            {
                Directory.Delete(normalizedDirectory);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Empty-directory cleanup is opportunistic and must not invalidate a successful release.
        }
    }

    private void DeleteEmptyToolDirectoriesBestEffort()
    {
        foreach (var directory in Directory.EnumerateDirectories(
                     RootDirectory,
                     "*",
                     SearchOption.TopDirectoryOnly))
        {
            try
            {
                var normalizedDirectory = NormalizeManagedPath(directory);
                if (!IsReparsePoint(normalizedDirectory) &&
                    !Directory.EnumerateFileSystemEntries(normalizedDirectory).Any())
                {
                    Directory.Delete(normalizedDirectory);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Another preview operation may have populated the directory between both checks.
            }
        }
    }

    private sealed record PreviewCacheFile(
        string Path,
        long Length,
        DateTimeOffset LastWriteTimeUtc);
}
