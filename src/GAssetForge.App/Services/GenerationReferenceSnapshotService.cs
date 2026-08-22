using GAssetForge.Core.ArtStyles;
using GAssetForge.Core.Generation;
using System.Buffers;
using System.IO;
using System.Security.Cryptography;

namespace GAssetForge.App.Services;

public sealed record GenerationReferenceSource(
    string Id,
    string Name,
    string FullPath,
    string Sha256,
    string MediaType);

public sealed record GenerationReferenceSnapshot(
    string BatchId,
    string? SnapshotDirectory,
    IReadOnlyList<MaterialGenerationStyleReference> References);

public sealed class GenerationReferenceSnapshotService(WorkspacePaths paths)
{
    public const long MaximumTaskReferenceBytes =
        ArtStyleLibraryValidator.MaximumAssetImportBatchBytes;

    private const int CopyBufferSize = 128 * 1024;

    public async Task<GenerationReferenceSnapshot> CaptureAsync(
        string batchId,
        IEnumerable<GenerationReferenceSource> sources,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(batchId);
        ArgumentNullException.ThrowIfNull(sources);
        if (!Guid.TryParseExact(batchId, "N", out _))
        {
            throw new ArgumentException("生成批次 ID 必须是 32 位 GUID。", nameof(batchId));
        }

        var sourceArray = sources.ToArray();
        if (sourceArray.Length > MaterialGenerationConstraintComposer.MaximumReferenceCount)
        {
            throw new ArgumentException(
                $"单个生成任务最多携带 {MaterialGenerationConstraintComposer.MaximumReferenceCount} 张风格参考图。",
                nameof(sources));
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (sourceArray.Length == 0)
        {
            return new GenerationReferenceSnapshot(batchId, null, []);
        }

        var plannedReferences = await Task.Run(
                () => PreflightSources(sourceArray, cancellationToken),
                cancellationToken)
            .ConfigureAwait(false);

        var snapshotRoot = Path.GetFullPath(paths.GenerationReferenceSnapshotDirectory);
        Directory.CreateDirectory(snapshotRoot);
        var finalDirectory = GetSafeChildPath(snapshotRoot, batchId);
        if (Directory.Exists(finalDirectory) || File.Exists(finalDirectory))
        {
            throw new IOException($"生成参考快照目录已存在：{finalDirectory}");
        }

        var temporaryDirectory = GetSafeChildPath(snapshotRoot, $".{batchId}.{Guid.NewGuid():N}.tmp");
        if (Directory.Exists(temporaryDirectory) || File.Exists(temporaryDirectory))
        {
            throw new IOException($"生成参考快照临时目录已存在：{temporaryDirectory}");
        }

        Directory.CreateDirectory(temporaryDirectory);
        var references = new List<MaterialGenerationStyleReference>(sourceArray.Length);
        try
        {
            foreach (var planned in plannedReferences)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var temporaryTarget = GetSafeChildPath(temporaryDirectory, planned.TargetFileName);
                var copiedHash = await CopyAndHashAsync(
                        planned,
                        temporaryTarget,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!string.Equals(copiedHash, planned.Source.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"风格参考图片内容已变化，请重新导入：{planned.Source.Name}");
                }

                var finalTarget = GetSafeChildPath(finalDirectory, planned.TargetFileName);
                references.Add(new MaterialGenerationStyleReference
                {
                    Id = planned.Source.Id,
                    Name = planned.Source.Name,
                    Sha256 = copiedHash,
                    MediaType = planned.Source.MediaType,
                    DataRootRelativePath = ToDataRootRelativePath(finalTarget)
                });
            }

            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(temporaryDirectory, finalDirectory);
            return new GenerationReferenceSnapshot(batchId, finalDirectory, references);
        }
        catch
        {
            TryDeleteDirectoryIfOwned(temporaryDirectory);
            throw;
        }
    }

    public Task DeleteAsync(
        GenerationReferenceSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return Task.Run(() => Delete(snapshot), cancellationToken);
    }

    public void Delete(GenerationReferenceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!string.IsNullOrWhiteSpace(snapshot.SnapshotDirectory))
        {
            DeleteDirectoryIfOwned(snapshot.SnapshotDirectory);
        }
    }

    private static IReadOnlyList<PlannedReference> PreflightSources(
        IReadOnlyList<GenerationReferenceSource> sources,
        CancellationToken cancellationToken)
    {
        var plannedReferences = new List<PlannedReference>(sources.Count);
        var targetFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long totalBytes = 0;
        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (source is null)
            {
                throw new ArgumentException("风格参考图集合不能包含空项。", nameof(sources));
            }

            if (string.IsNullOrWhiteSpace(source.Name) || source.Name.Length > 260)
            {
                throw new InvalidDataException("风格参考图片名称无效。");
            }

            if (!IsSha256(source.Sha256))
            {
                throw new InvalidDataException($"风格参考图片 SHA-256 无效：{source.Name}");
            }

            if (string.IsNullOrWhiteSpace(source.MediaType) ||
                !source.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"风格参考图片媒体类型无效：{source.Name}");
            }

            var sourcePath = Path.GetFullPath(source.FullPath);
            var sourceInfo = new FileInfo(sourcePath);
            if (!sourceInfo.Exists)
            {
                throw new FileNotFoundException($"风格参考图片不存在：{source.Name}", sourcePath);
            }

            if ((sourceInfo.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException($"风格参考图片不能是符号链接：{source.Name}");
            }

            if (sourceInfo.Length is <= 0 or > ArtStyleLibraryValidator.MaximumAssetFileBytes)
            {
                throw new InvalidDataException(
                    $"风格参考图片大小无效或超过 " +
                    $"{ArtStyleLibraryValidator.MaximumAssetFileBytes / 1024 / 1024} MB：{source.Name}");
            }

            if (totalBytes > MaximumTaskReferenceBytes - sourceInfo.Length)
            {
                throw new InvalidDataException(
                    $"单个生成任务的风格参考图片总量不能超过 " +
                    $"{MaximumTaskReferenceBytes / 1024 / 1024 / 1024} GB。");
            }

            totalBytes += sourceInfo.Length;
            var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
            if (!IsSupportedImageExtension(extension))
            {
                throw new NotSupportedException($"风格参考图片格式不受支持：{sourceInfo.Extension}");
            }

            var targetFileName = $"{NormalizeId(source.Id)}{extension}";
            if (!targetFileNames.Add(targetFileName))
            {
                throw new InvalidDataException($"风格参考素材 ID 重复：{source.Id}");
            }

            plannedReferences.Add(new PlannedReference(
                source,
                sourcePath,
                targetFileName,
                sourceInfo.Length));
        }

        return plannedReferences;
    }

    private static async Task<string> CopyAndHashAsync(
        PlannedReference planned,
        string targetPath,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        try
        {
            await using var sourceStream = new FileStream(
                planned.SourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                CopyBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var targetStream = new FileStream(
                targetPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                CopyBufferSize,
                FileOptions.Asynchronous);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long copiedBytes = 0;
            while (true)
            {
                var bytesRead = await sourceStream.ReadAsync(
                        buffer.AsMemory(0, buffer.Length),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (bytesRead == 0)
                {
                    break;
                }

                if (copiedBytes > planned.Length - bytesRead)
                {
                    throw new InvalidDataException(
                        $"风格参考图片在快照期间发生变化：{planned.Source.Name}");
                }

                copiedBytes += bytesRead;
                hash.AppendData(buffer.AsSpan(0, bytesRead));
                await targetStream.WriteAsync(
                        buffer.AsMemory(0, bytesRead),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (copiedBytes != planned.Length)
            {
                throw new InvalidDataException(
                    $"风格参考图片在快照期间发生变化：{planned.Source.Name}");
            }

            await targetStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }
        catch
        {
            TryDeleteFile(targetPath);
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private string ToDataRootRelativePath(string fullPath)
    {
        var relative = Path.GetRelativePath(paths.RootDirectory, fullPath).Replace('\\', '/');
        if (Path.IsPathRooted(relative) || relative is "." or ".." || relative.StartsWith("../", StringComparison.Ordinal))
        {
            throw new InvalidDataException("风格参考快照路径越出了数据根目录。");
        }

        return relative;
    }

    private void DeleteDirectoryIfOwned(string directoryPath)
    {
        var snapshotRoot = Path.GetFullPath(paths.GenerationReferenceSnapshotDirectory);
        var fullPath = Path.GetFullPath(directoryPath);
        if (!AssetRootService.IsWithinRoot(snapshotRoot, fullPath) ||
            AssetRootService.PathEquals(snapshotRoot, fullPath) ||
            !Directory.Exists(fullPath))
        {
            return;
        }

        var attributes = File.GetAttributes(fullPath);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException($"拒绝删除重解析点快照目录：{fullPath}");
        }

        Directory.Delete(fullPath, recursive: true);
    }

    private void TryDeleteDirectoryIfOwned(string directoryPath)
    {
        try
        {
            DeleteDirectoryIfOwned(directoryPath);
        }
        catch (Exception exception) when (exception is
            IOException or
            UnauthorizedAccessException or
            InvalidDataException or
            System.Security.SecurityException)
        {
            // Preserve the original capture failure while still refusing unsafe cleanup targets.
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is
            IOException or
            UnauthorizedAccessException or
            System.Security.SecurityException)
        {
            // The owning temporary directory is removed by the outer failure cleanup.
        }
    }

    private static string GetSafeChildPath(string root, string fileName)
    {
        var candidate = Path.GetFullPath(Path.Combine(root, fileName));
        if (!AssetRootService.IsWithinRoot(root, candidate) || AssetRootService.PathEquals(root, candidate))
        {
            throw new InvalidDataException("生成参考快照路径越界。");
        }

        return candidate;
    }

    private static string NormalizeId(string value)
    {
        if (!Guid.TryParseExact(value, "N", out var id))
        {
            throw new InvalidDataException($"风格参考素材 ID 无效：{value}");
        }

        return id.ToString("N");
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static bool IsSupportedImageExtension(string extension) => extension is
        ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp" or ".gif" or ".tif" or ".tiff";

    private sealed record PlannedReference(
        GenerationReferenceSource Source,
        string SourcePath,
        string TargetFileName,
        long Length);
}
