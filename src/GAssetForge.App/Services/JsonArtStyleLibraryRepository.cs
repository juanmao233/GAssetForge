using GAssetForge.Core.ArtStyles;
using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace GAssetForge.App.Services;

public sealed record ArtStyleAssetImportResult(
    IReadOnlyList<ArtStyleAssetModel> ImportedAssets,
    int DuplicateCount);

public sealed class JsonArtStyleLibraryRepository
{
    public const string LibraryFileName = "art-style-library.v1.json";

    private const string BackupFileName = "art-style-library.v1.json.bak";
    private const long MaximumLibraryFileBytes = 16L * 1024L * 1024L;
    private static readonly ConcurrentDictionary<string, object> ProcessLocks =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private static readonly IReadOnlyDictionary<string, string> MediaTypes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".png"] = "image/png",
            [".jpg"] = "image/jpeg",
            [".jpeg"] = "image/jpeg",
            [".webp"] = "image/webp",
            [".bmp"] = "image/bmp",
            [".gif"] = "image/gif",
            [".tif"] = "image/tiff",
            [".tiff"] = "image/tiff"
        };

    private readonly object _syncRoot;
    private readonly TimeProvider _timeProvider;

    public JsonArtStyleLibraryRepository(WorkspacePaths paths, TimeProvider? timeProvider = null)
        : this(ResolveWorkspaceStorageRoot(paths), timeProvider)
    {
    }

    public JsonArtStyleLibraryRepository(string storageRoot, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageRoot);
        StorageRoot = Path.GetFullPath(storageRoot);
        RejectFileSystemRoot(StorageRoot);

        if (File.Exists(StorageRoot))
        {
            throw new ArgumentException("美术风格素材库存储目录指向了文件。", nameof(storageRoot));
        }

        LibraryFilePath = Path.Combine(StorageRoot, LibraryFileName);
        BackupFilePath = Path.Combine(StorageRoot, BackupFileName);
        AssetRoot = Path.Combine(StorageRoot, "assets");
        TrashRoot = Path.Combine(StorageRoot, ".trash");
        _syncRoot = ProcessLocks.GetOrAdd(LibraryFilePath, static _ => new object());
        _timeProvider = timeProvider ?? TimeProvider.System;

        EnsureManagedDirectory(StorageRoot);
        EnsureManagedDirectory(AssetRoot);
    }

    public string StorageRoot { get; }

    public string LibraryFilePath { get; }

    public string BackupFilePath { get; }

    public string AssetRoot { get; }

    public string TrashRoot { get; }

    public string? LastRecoverySource { get; private set; }

    public ArtStyleLibraryModel LoadOrCreate()
    {
        lock (_syncRoot)
        {
            LastRecoverySource = null;
            if (TryRead(LibraryFilePath, out var primary))
            {
                return primary!;
            }

            if (TryRead(BackupFilePath, out var backup))
            {
                LastRecoverySource = "backup";
                return backup!;
            }

            var library = ArtStyleLibraryFactory.CreateDefault(_timeProvider);
            SaveCore(library);
            LastRecoverySource = "default";
            return library;
        }
    }

    public void Save(ArtStyleLibraryModel library)
    {
        lock (_syncRoot)
        {
            SaveCore(library);
            LastRecoverySource = null;
        }
    }

    public ArtStyleAssetImportResult ImportAssets(
        ArtStyleLibraryModel library,
        string groupId,
        IEnumerable<string> sourcePaths)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        ArgumentNullException.ThrowIfNull(sourcePaths);

        lock (_syncRoot)
        {
            ArtStyleLibraryValidator.Validate(library);
            var group = library.Groups.FirstOrDefault(candidate => candidate.Id == groupId)
                ?? throw new ArgumentException("要导入素材的风格组不存在。", nameof(groupId));

            var sources = ValidateImportSources(sourcePaths);
            if (sources.Count == 0)
            {
                return new ArtStyleAssetImportResult([], 0);
            }

            if (group.Assets.Count + sources.Count > ArtStyleLibraryValidator.MaximumAssetsPerGroup)
            {
                throw new InvalidOperationException("导入后将超过单个风格组允许的素材数量上限。");
            }

            var groupDirectory = GetGroupDirectory(group.Id);
            EnsureManagedDirectory(groupDirectory);
            var imported = new List<ArtStyleAssetModel>();
            var createdPaths = new List<string>();
            var duplicateCount = 0;
            var previousSelectedGroupId = library.SelectedGroupId;
            var previousSelectedAssetId = library.SelectedAssetId;
            var previousUpdatedAt = group.UpdatedAtUtc;

            try
            {
                foreach (var source in sources)
                {
                    var hash = ComputeSha256(source.FullPath);
                    if (group.Assets.Concat(imported).Any(asset =>
                            string.Equals(asset.Sha256, hash, StringComparison.OrdinalIgnoreCase)))
                    {
                        duplicateCount++;
                        continue;
                    }

                    var assetId = Guid.NewGuid().ToString("N");
                    var fileName = $"{assetId}{source.Extension}";
                    var targetPath = GetSafePath(groupDirectory, fileName);
                    var temporaryPath = GetSafePath(groupDirectory, $".{assetId}.{Guid.NewGuid():N}.tmp");
                    try
                    {
                        CopyFileDurable(source.FullPath, temporaryPath);
                        File.Move(temporaryPath, targetPath);
                    }
                    finally
                    {
                        TryDeleteFile(temporaryPath);
                    }

                    createdPaths.Add(targetPath);
                    var asset = new ArtStyleAssetModel
                    {
                        Id = assetId,
                        Name = CreateUniqueDisplayName(
                            Path.GetFileNameWithoutExtension(source.OriginalFileName),
                            group.Assets.Concat(imported).Select(candidate => candidate.Name)),
                        OriginalFileName = source.OriginalFileName,
                        RelativePath = $"assets/{group.Id}/{fileName}",
                        Extension = source.Extension,
                        MediaType = MediaTypes[source.Extension],
                        Sha256 = hash,
                        Size = source.Size,
                        ImportedAtUtc = _timeProvider.GetUtcNow()
                    };
                    imported.Add(asset);
                }

                if (imported.Count == 0)
                {
                    return new ArtStyleAssetImportResult([], duplicateCount);
                }

                group.Assets.AddRange(imported);
                group.UpdatedAtUtc = _timeProvider.GetUtcNow();
                library.SelectedGroupId = group.Id;
                library.SelectedAssetId = imported[^1].Id;
                SaveCore(library);
                return new ArtStyleAssetImportResult(imported, duplicateCount);
            }
            catch
            {
                foreach (var asset in imported)
                {
                    group.Assets.Remove(asset);
                }

                group.UpdatedAtUtc = previousUpdatedAt;
                library.SelectedGroupId = previousSelectedGroupId;
                library.SelectedAssetId = previousSelectedAssetId;

                foreach (var createdPath in createdPaths)
                {
                    TryDeleteFile(createdPath);
                }

                throw;
            }
        }
    }

    public void RemoveAsset(ArtStyleLibraryModel library, string groupId, string assetId)
    {
        ArgumentNullException.ThrowIfNull(library);
        lock (_syncRoot)
        {
            ArtStyleLibraryValidator.Validate(library);
            var group = library.Groups.FirstOrDefault(candidate => candidate.Id == groupId)
                ?? throw new ArgumentException("风格组不存在。", nameof(groupId));
            var index = group.Assets.FindIndex(candidate => candidate.Id == assetId);
            if (index < 0)
            {
                throw new ArgumentException("要移除的美术素材不存在。", nameof(assetId));
            }

            var asset = group.Assets[index];
            var originalPath = ResolveAssetFullPath(asset);
            var quarantinedPath = QuarantineFile(originalPath);
            var previousSelectedGroupId = library.SelectedGroupId;
            var previousSelectedAssetId = library.SelectedAssetId;
            var previousUpdatedAt = group.UpdatedAtUtc;

            try
            {
                group.Assets.RemoveAt(index);
                group.UpdatedAtUtc = _timeProvider.GetUtcNow();
                library.SelectedGroupId = group.Id;
                library.SelectedAssetId = group.Assets.Count == 0
                    ? null
                    : group.Assets[Math.Min(index, group.Assets.Count - 1)].Id;
                SaveCore(library);
                TryDeleteFile(quarantinedPath);
            }
            catch
            {
                group.Assets.Insert(index, asset);
                group.UpdatedAtUtc = previousUpdatedAt;
                library.SelectedGroupId = previousSelectedGroupId;
                library.SelectedAssetId = previousSelectedAssetId;
                RestoreQuarantinedFile(quarantinedPath, originalPath);
                throw;
            }
        }
    }

    public void DeleteGroup(ArtStyleLibraryModel library, string groupId)
    {
        ArgumentNullException.ThrowIfNull(library);
        lock (_syncRoot)
        {
            ArtStyleLibraryValidator.Validate(library);
            var index = library.Groups.FindIndex(candidate => candidate.Id == groupId);
            if (index < 0)
            {
                throw new ArgumentException("要删除的美术风格组不存在。", nameof(groupId));
            }

            var group = library.Groups[index];
            var originalDirectory = GetGroupDirectory(group.Id);
            var quarantinedDirectory = QuarantineDirectory(originalDirectory);
            var previousSelectedGroupId = library.SelectedGroupId;
            var previousSelectedAssetId = library.SelectedAssetId;

            try
            {
                library.Groups.RemoveAt(index);
                var nextGroup = library.Groups.Count == 0
                    ? null
                    : library.Groups[Math.Min(index, library.Groups.Count - 1)];
                library.SelectedGroupId = nextGroup?.Id;
                library.SelectedAssetId = nextGroup?.Assets.FirstOrDefault()?.Id;
                SaveCore(library);
                TryDeleteDirectory(quarantinedDirectory);
            }
            catch
            {
                library.Groups.Insert(index, group);
                library.SelectedGroupId = previousSelectedGroupId;
                library.SelectedAssetId = previousSelectedAssetId;
                RestoreQuarantinedDirectory(quarantinedDirectory, originalDirectory);
                throw;
            }
        }
    }

    public string ResolveAssetFullPath(ArtStyleAssetModel asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        EnsureManagedDirectory(StorageRoot);
        EnsureManagedDirectory(AssetRoot);
        var relativePath = asset.RelativePath.Replace('/', Path.DirectorySeparatorChar);
        var fullPath = GetSafePath(StorageRoot, relativePath);
        if (!IsWithin(AssetRoot, fullPath))
        {
            throw new InvalidDataException("美术素材路径不属于应用管理的素材目录。");
        }

        var containingDirectory = Path.GetDirectoryName(fullPath);
        if (containingDirectory is not null && Directory.Exists(containingDirectory))
        {
            EnsureNotReparsePoint(containingDirectory);
        }

        return fullPath;
    }

    private List<ImportSource> ValidateImportSources(IEnumerable<string> sourcePaths)
    {
        var result = new List<ImportSource>();
        long totalBytes = 0;
        foreach (var rawPath in sourcePaths
                     .Where(path => !string.IsNullOrWhiteSpace(path))
                     .Distinct(OperatingSystem.IsWindows()
                         ? StringComparer.OrdinalIgnoreCase
                         : StringComparer.Ordinal))
        {
            var fullPath = Path.GetFullPath(rawPath);
            var file = new FileInfo(fullPath);
            if (!file.Exists)
            {
                throw new FileNotFoundException("要导入的美术素材不存在。", fullPath);
            }

            if ((file.Attributes & FileAttributes.Directory) != 0)
            {
                throw new InvalidDataException($"不能将文件夹作为美术素材导入：{file.Name}。");
            }

            var extension = file.Extension.ToLowerInvariant();
            if (!MediaTypes.ContainsKey(extension))
            {
                throw new NotSupportedException($"不支持的图片格式：{file.Extension}。");
            }

            if (file.Length <= 0 || file.Length > ArtStyleLibraryValidator.MaximumAssetFileBytes)
            {
                throw new InvalidDataException(
                    $"图片 {file.Name} 为空或超过 " +
                    $"{ArtStyleLibraryValidator.MaximumAssetFileBytes / 1024 / 1024} MB 上限。");
            }

            ValidateImageSignature(fullPath, extension);
            totalBytes = checked(totalBytes + file.Length);
            if (totalBytes > ArtStyleLibraryValidator.MaximumAssetImportBatchBytes)
            {
                throw new InvalidDataException(
                    $"单次导入图片总量不能超过 " +
                    $"{ArtStyleLibraryValidator.MaximumAssetImportBatchBytes / 1024 / 1024 / 1024} GB。");
            }

            result.Add(new ImportSource(fullPath, file.Name, extension, file.Length));
        }

        return result;
    }

    private bool TryRead(string path, out ArtStyleLibraryModel? library)
    {
        library = null;
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length <= 0 || file.Length > MaximumLibraryFileBytes)
            {
                return false;
            }

            library = ArtStyleJson.Deserialize(File.ReadAllText(path));
            ArtStyleLibraryValidator.Validate(library);
            return true;
        }
        catch
        {
            library = null;
            return false;
        }
    }

    private void SaveCore(ArtStyleLibraryModel library)
    {
        ArtStyleLibraryValidator.Validate(library);
        WriteAtomic(LibraryFilePath, BackupFilePath, ArtStyleJson.Serialize(library));
    }

    private string GetGroupDirectory(string groupId)
    {
        if (groupId.Length != 32 || groupId.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidDataException("风格组 ID 不能用于管理素材目录。");
        }

        EnsureManagedDirectory(StorageRoot);
        EnsureManagedDirectory(AssetRoot);

        return GetSafePath(AssetRoot, groupId);
    }

    private string? QuarantineFile(string originalPath)
    {
        if (!File.Exists(originalPath))
        {
            return null;
        }

        EnsureManagedDirectory(TrashRoot);
        var quarantinedPath = GetSafePath(TrashRoot, $"{Guid.NewGuid():N}.file");
        File.Move(originalPath, quarantinedPath);
        return quarantinedPath;
    }

    private string? QuarantineDirectory(string originalDirectory)
    {
        if (!Directory.Exists(originalDirectory))
        {
            return null;
        }

        EnsureNotReparsePoint(originalDirectory);
        EnsureManagedDirectory(TrashRoot);
        var quarantinedPath = GetSafePath(TrashRoot, $"{Guid.NewGuid():N}.group");
        Directory.Move(originalDirectory, quarantinedPath);
        return quarantinedPath;
    }

    private static void RestoreQuarantinedFile(string? quarantinedPath, string originalPath)
    {
        if (quarantinedPath is not null && File.Exists(quarantinedPath) && !File.Exists(originalPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(originalPath)!);
            File.Move(quarantinedPath, originalPath);
        }
    }

    private static void RestoreQuarantinedDirectory(string? quarantinedPath, string originalDirectory)
    {
        if (quarantinedPath is not null &&
            Directory.Exists(quarantinedPath) &&
            !Directory.Exists(originalDirectory))
        {
            Directory.Move(quarantinedPath, originalDirectory);
        }
    }

    private static string ComputeSha256(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static void CopyFileDurable(string sourcePath, string destinationPath)
    {
        using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            81920,
            FileOptions.WriteThrough);
        source.CopyTo(destination);
        destination.Flush(flushToDisk: true);
    }

    private static void WriteAtomic(string path, string backupPath, string content)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("无法确定美术风格素材库目录。");
        EnsureManagedDirectory(directory);

        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(content);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(fullPath))
            {
                File.Replace(temporaryPath, fullPath, backupPath, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, fullPath);
            }
        }
        finally
        {
            TryDeleteFile(temporaryPath);
        }
    }

    private static string CreateUniqueDisplayName(string requestedName, IEnumerable<string> existingNames)
    {
        var baseName = string.IsNullOrWhiteSpace(requestedName) ? "风格素材" : requestedName.Trim();
        if (baseName.Length > ArtStyleLibraryValidator.MaximumNameLength)
        {
            baseName = baseName[..ArtStyleLibraryValidator.MaximumNameLength];
        }

        var occupied = existingNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!occupied.Contains(baseName))
        {
            return baseName;
        }

        for (var index = 2; ; index++)
        {
            var suffix = $" ({index})";
            var prefixLength = Math.Min(
                baseName.Length,
                ArtStyleLibraryValidator.MaximumNameLength - suffix.Length);
            var candidate = string.Concat(baseName.AsSpan(0, prefixLength), suffix);
            if (!occupied.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    private static void ValidateImageSignature(string path, string extension)
    {
        Span<byte> header = stackalloc byte[12];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var read = stream.Read(header);
        var valid = extension switch
        {
            ".png" => read >= 8 && header[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }),
            ".jpg" or ".jpeg" => read >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF,
            ".gif" => read >= 6 && (header[..6].SequenceEqual("GIF87a"u8) || header[..6].SequenceEqual("GIF89a"u8)),
            ".bmp" => read >= 2 && header[0] == (byte)'B' && header[1] == (byte)'M',
            ".webp" => read >= 12 && header[..4].SequenceEqual("RIFF"u8) && header[8..12].SequenceEqual("WEBP"u8),
            ".tif" or ".tiff" => read >= 4 &&
                (header[..4].SequenceEqual(new byte[] { 0x49, 0x49, 0x2A, 0x00 }) ||
                 header[..4].SequenceEqual(new byte[] { 0x4D, 0x4D, 0x00, 0x2A })),
            _ => false
        };

        if (!valid)
        {
            throw new InvalidDataException($"文件 {Path.GetFileName(path)} 的内容与图片扩展名不匹配。");
        }
    }

    private static string ResolveWorkspaceStorageRoot(WorkspacePaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var canonicalRoot = Path.Combine(paths.UserAssetDirectory, "StyleLibrary");
        var legacyRoot = Path.Combine(paths.AssetDirectory, "StyleLibrary");
        var canonicalIsValid = HasValidLibraryDocument(canonicalRoot);
        var legacyIsValid = HasValidLibraryDocument(legacyRoot);
        if (canonicalIsValid || !legacyIsValid)
        {
            return canonicalRoot;
        }

        if (Directory.Exists(canonicalRoot) || File.Exists(canonicalRoot))
        {
            // Never overwrite a residual canonical directory automatically. Keep the valid legacy
            // library visible and writable until a later explicit repair can reconcile both roots.
            return legacyRoot;
        }

        var stagingRoot = Path.Combine(
            paths.UserAssetDirectory,
            $".StyleLibrary.migration.{Guid.NewGuid():N}");
        try
        {
            EnsureManagedDirectory(paths.UserAssetDirectory);
            EnsureNotReparsePoint(legacyRoot);
            CopyDirectoryVerified(legacyRoot, stagingRoot);
            Directory.Move(stagingRoot, canonicalRoot);
            return canonicalRoot;
        }
        catch (Exception exception) when (exception is
            IOException or
            UnauthorizedAccessException or
            InvalidDataException or
            NotSupportedException or
            ArgumentException or
            System.Text.Json.JsonException or
            System.Security.SecurityException)
        {
            TryDeleteDirectory(stagingRoot);
            return Directory.Exists(canonicalRoot) ? canonicalRoot : legacyRoot;
        }
    }

    private static bool HasValidLibraryDocument(string storageRoot)
    {
        try
        {
            if (!Directory.Exists(storageRoot) ||
                (File.GetAttributes(storageRoot) & FileAttributes.ReparsePoint) != 0)
            {
                return false;
            }

            foreach (var path in new[]
                     {
                         Path.Combine(storageRoot, LibraryFileName),
                         Path.Combine(storageRoot, BackupFileName)
                     })
            {
                var file = new FileInfo(path);
                if (!file.Exists || file.Length is <= 0 or > MaximumLibraryFileBytes ||
                    (file.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                var library = ArtStyleJson.Deserialize(File.ReadAllText(path));
                ArtStyleLibraryValidator.Validate(library);
                return true;
            }
        }
        catch (Exception exception) when (exception is
            IOException or
            UnauthorizedAccessException or
            InvalidDataException or
            NotSupportedException or
            ArgumentException or
            System.Text.Json.JsonException or
            System.Security.SecurityException)
        {
            return false;
        }

        return false;
    }

    private static void CopyDirectoryVerified(string sourceRoot, string destinationRoot)
    {
        var fullSourceRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceRoot));
        var fullDestinationRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationRoot));
        EnsureNotReparsePoint(fullSourceRoot);
        if (Directory.Exists(fullDestinationRoot) || File.Exists(fullDestinationRoot))
        {
            throw new IOException("美术风格素材库迁移暂存目录已存在。");
        }

        Directory.CreateDirectory(fullDestinationRoot);
        var pending = new Stack<(string Source, string Destination)>();
        pending.Push((fullSourceRoot, fullDestinationRoot));
        while (pending.Count > 0)
        {
            var (sourceDirectory, destinationDirectory) = pending.Pop();
            EnsureNotReparsePoint(sourceDirectory);
            foreach (var sourceEntry in Directory.EnumerateFileSystemEntries(sourceDirectory))
            {
                var attributes = File.GetAttributes(sourceEntry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException($"美术风格素材库迁移拒绝符号链接或重解析点：{sourceEntry}");
                }

                var relativePath = Path.GetRelativePath(fullSourceRoot, sourceEntry);
                var destinationEntry = GetSafePath(fullDestinationRoot, relativePath);
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    Directory.CreateDirectory(destinationEntry);
                    pending.Push((sourceEntry, destinationEntry));
                    continue;
                }

                CopyFileDurable(sourceEntry, destinationEntry);
                var sourceInfo = new FileInfo(sourceEntry);
                var destinationInfo = new FileInfo(destinationEntry);
                if (sourceInfo.Length != destinationInfo.Length ||
                    !string.Equals(
                        ComputeSha256(sourceEntry),
                        ComputeSha256(destinationEntry),
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException($"美术风格素材库迁移复制校验失败：{relativePath}");
                }
            }
        }
    }

    private static string GetSafePath(string root, string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException("应用管理路径必须是相对路径。");
        }

        var fullRoot = Path.GetFullPath(root);
        var candidate = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
        if (!IsWithin(fullRoot, candidate))
        {
            throw new InvalidDataException("检测到试图跳出美术风格素材库目录的路径。");
        }

        return candidate;
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

    private static void EnsureManagedDirectory(string path)
    {
        Directory.CreateDirectory(path);
        EnsureNotReparsePoint(path);
    }

    private static void EnsureNotReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException($"应用管理目录不能是符号链接或重解析点：{path}");
        }
    }

    private static void RejectFileSystemRoot(string path)
    {
        var pathRoot = Path.GetPathRoot(path);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (pathRoot is not null &&
            string.Equals(
                Path.TrimEndingDirectorySeparator(path),
                Path.TrimEndingDirectorySeparator(pathRoot),
                comparison))
        {
            throw new ArgumentException("美术风格素材库存储目录不能是文件系统根目录。", nameof(path));
        }
    }

    private static void TryDeleteFile(string? path)
    {
        try
        {
            if (path is not null && File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Orphan cleanup can be retried during a later maintenance pass.
        }
    }

    private static void TryDeleteDirectory(string? path)
    {
        try
        {
            if (path is not null && Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Orphan cleanup can be retried during a later maintenance pass.
        }
    }

    private sealed record ImportSource(
        string FullPath,
        string OriginalFileName,
        string Extension,
        long Size);
}
