using GAssetForge.Core.ArtWorkspaces;
using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using SixLabors.ImageSharp;

namespace GAssetForge.App.Services;

public sealed record ArtWorkspaceLoadResult(
    ArtWorkspaceDocument Document,
    string FilePath,
    string RecoverySource,
    string ErrorMessage);

public sealed record ArtWorkspaceImportedAsset(
    ArtWorkspaceManagedAsset Asset,
    string AbsolutePath,
    bool ReusedExistingAsset);

public sealed class JsonArtWorkspaceRepository
{
    private const long MaximumSourceImageBytes = 512L * 1024L * 1024L;
    private static readonly ConcurrentDictionary<string, object> ProcessLocks =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private static readonly IReadOnlyDictionary<string, string> SupportedImageMediaTypes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".png"] = "image/png",
            [".jpg"] = "image/jpeg",
            [".jpeg"] = "image/jpeg",
            [".bmp"] = "image/bmp",
            [".gif"] = "image/gif",
            [".tif"] = "image/tiff",
            [".tiff"] = "image/tiff",
            [".webp"] = "image/webp"
        };

    private readonly string _workspaceRoot;
    private readonly string _draftDirectory;
    private readonly ProjectWorkspaceService _projectWorkspaceService;
    private readonly TimeProvider _timeProvider;

    public JsonArtWorkspaceRepository(WorkspacePaths paths, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _workspaceRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(paths.WorkspaceDirectory));
        _draftDirectory = Path.Combine(_workspaceRoot, "ArtWorkspaceDrafts");
        _projectWorkspaceService = new ProjectWorkspaceService();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public ArtWorkspaceLoadResult LoadOrCreate(string? projectRoot)
    {
        var target = ResolveTarget(projectRoot);
        var syncRoot = ProcessLocks.GetOrAdd(target.DocumentPath, static _ => new object());
        lock (syncRoot)
        {
            if (TryRead(target, target.DocumentPath, out var primary))
            {
                return new ArtWorkspaceLoadResult(primary!, target.DocumentPath, "primary", string.Empty);
            }

            if (TryRead(target, target.BackupPath, out var backup))
            {
                return new ArtWorkspaceLoadResult(
                    backup!,
                    target.DocumentPath,
                    "backup",
                    "美术工作区主文件无效，当前已从备份加载；下次有效编辑会保留损坏副本后修复主文件。");
            }

            var document = ArtWorkspaceDocumentFactory.Create(_timeProvider);
            if (!File.Exists(target.DocumentPath) && !File.Exists(target.BackupPath))
            {
                SaveCore(target, document);
                return new ArtWorkspaceLoadResult(document, target.DocumentPath, "default", string.Empty);
            }

            return new ArtWorkspaceLoadResult(
                document,
                target.DocumentPath,
                "safe-default",
                "美术工作区主文件与备份均无效；已加载内存安全默认状态且未覆盖原文件。");
        }
    }

    public void Save(string? projectRoot, ArtWorkspaceDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        document.UpdatedAtUtc = _timeProvider.GetUtcNow();
        ArtWorkspaceValidator.Validate(document);
        var target = ResolveTarget(projectRoot);
        ValidateDocumentPaths(target, document, verifyExistingAssetMetadata: false);
        var syncRoot = ProcessLocks.GetOrAdd(target.DocumentPath, static _ => new object());
        lock (syncRoot)
        {
            SaveCore(target, document);
        }
    }

    public ArtWorkspaceImportedAsset ImportAsset(
        string? projectRoot,
        ArtWorkspaceDocument document,
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        cancellationToken.ThrowIfCancellationRequested();
        var source = ValidateSourceImage(sourcePath);
        var target = ResolveTarget(projectRoot);
        EnsureManagedDirectory(target.StorageRoot, target.ReferenceDirectory);
        var sourceHash = ComputeSha256(source.FullName, cancellationToken);
        var existing = document.Assets.FirstOrDefault(asset =>
            string.Equals(asset.Sha256, sourceHash, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            var existingPath = ResolveManagedPath(target, existing.RelativePath, mustExist: false);
            if (File.Exists(existingPath) && new FileInfo(existingPath).Length == existing.SizeBytes)
            {
                var existingPixelSize = ReadPixelSize(existingPath);
                if (existingPixelSize.Width == existing.PixelWidth &&
                    existingPixelSize.Height == existing.PixelHeight &&
                    string.Equals(
                        ComputeSha256(existingPath, cancellationToken),
                        sourceHash,
                        StringComparison.OrdinalIgnoreCase))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return new ArtWorkspaceImportedAsset(existing, existingPath, ReusedExistingAsset: true);
                }
            }
        }

        if (document.Assets.Count >= ArtWorkspaceValidator.MaximumAssets)
        {
            throw new InvalidDataException(
                $"美术工作区最多允许 {ArtWorkspaceValidator.MaximumAssets} 个在用受管素材。");
        }

        var extension = source.Extension.ToLowerInvariant();
        var assetId = Guid.NewGuid().ToString("N");
        var destination = Path.GetFullPath(Path.Combine(target.ReferenceDirectory, $"{assetId}{extension}"));
        if (!IsWithin(target.ReferenceDirectory, destination))
        {
            throw new InvalidDataException("导入素材目标路径试图离开受管参考目录。");
        }

        try
        {
            CopyFileAtomic(source.FullName, destination, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var copiedFile = new FileInfo(destination);
            if (copiedFile.Length <= 0 || copiedFile.Length > MaximumSourceImageBytes)
            {
                throw new InvalidDataException("导入后的受管图片为空或超过 512 MiB 限制。");
            }

            var copiedHash = ComputeSha256(destination, cancellationToken);
            var (pixelWidth, pixelHeight) = ReadPixelSize(destination);
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = Path.GetRelativePath(target.StorageRoot, destination).Replace('\\', '/');
            var asset = new ArtWorkspaceManagedAsset
            {
                Id = assetId,
                RelativePath = relativePath,
                OriginalFileName = source.Name,
                MediaType = SupportedImageMediaTypes[extension],
                Sha256 = copiedHash,
                SizeBytes = copiedFile.Length,
                PixelWidth = pixelWidth,
                PixelHeight = pixelHeight,
                ImportedAtUtc = _timeProvider.GetUtcNow()
            };
            ArtWorkspaceValidator.Validate(new ArtWorkspaceDocument
            {
                CreatedAtUtc = _timeProvider.GetUtcNow(),
                UpdatedAtUtc = _timeProvider.GetUtcNow(),
                Assets = [asset]
            });
            cancellationToken.ThrowIfCancellationRequested();
            return new ArtWorkspaceImportedAsset(asset, destination, ReusedExistingAsset: false);
        }
        catch
        {
            TryDelete(destination);
            throw;
        }
    }

    public string ResolveAssetPath(string? projectRoot, string relativePath)
    {
        var target = ResolveTarget(projectRoot);
        return ResolveManagedPath(target, relativePath, mustExist: false);
    }

    public string ResolveDocumentPath(string? projectRoot) => ResolveTarget(projectRoot).DocumentPath;

    private ArtWorkspaceStorageTarget ResolveTarget(string? projectRoot)
    {
        if (string.IsNullOrWhiteSpace(projectRoot))
        {
            EnsureManagedDirectory(_workspaceRoot, _draftDirectory);
            return new ArtWorkspaceStorageTarget(
                _workspaceRoot,
                _draftDirectory,
                Path.Combine(_draftDirectory, "art-workspace.v1.json"),
                Path.Combine(_draftDirectory, "art-workspace.v1.json.bak"),
                Path.Combine(_draftDirectory, "References"));
        }

        if (!_projectWorkspaceService.TryLoad(
                projectRoot,
                out _,
                out var projectPaths,
                out var reason))
        {
            throw new InvalidDataException($"美术工作区只能绑定到有效的 GAssetForge 项目：{reason}");
        }

        EnsureManagedDirectory(projectPaths.RootDirectory, projectPaths.ArtWorkspaceDirectory);
        return new ArtWorkspaceStorageTarget(
            projectPaths.RootDirectory,
            projectPaths.ArtWorkspaceDirectory,
            projectPaths.ArtWorkspacePath,
            projectPaths.ArtWorkspaceBackupPath,
            projectPaths.ArtWorkspaceReferenceDirectory);
    }

    private bool TryRead(
        ArtWorkspaceStorageTarget target,
        string path,
        out ArtWorkspaceDocument? document,
        bool verifyExistingAssetMetadata = true)
    {
        document = null;
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            EnsureNotReparsePoint(path, "美术工作区文件");
            var file = new FileInfo(path);
            if (file.Length <= 0 ||
                file.Length > ArtWorkspaceValidator.MaximumSerializedDocumentBytes)
            {
                return false;
            }

            document = ArtWorkspaceJson.Deserialize(File.ReadAllText(path));
            ValidateDocumentPaths(target, document, verifyExistingAssetMetadata);
            return true;
        }
        catch (Exception exception) when (exception is
            InvalidDataException or
            IOException or
            UnauthorizedAccessException or
            System.Text.Json.JsonException or
            NotSupportedException or
            ArgumentException or
            ImageFormatException)
        {
            document = null;
            return false;
        }
    }

    private void SaveCore(ArtWorkspaceStorageTarget target, ArtWorkspaceDocument document)
    {
        var content = ArtWorkspaceJson.Serialize(document);
        if (Encoding.UTF8.GetByteCount(content) > ArtWorkspaceValidator.MaximumSerializedDocumentBytes)
        {
            throw new InvalidDataException(
                $"美术工作区文件不能超过 " +
                $"{ArtWorkspaceValidator.MaximumSerializedDocumentBytes / 1024 / 1024} MiB。");
        }

        EnsureManagedDirectory(target.StorageRoot, target.DocumentDirectory);
        EnsureManagedFileSlot(target.DocumentPath, "美术工作区主文件");
        EnsureManagedFileSlot(target.BackupPath, "美术工作区备份文件");
        var temporaryPath = Path.Combine(
            target.DocumentDirectory,
            $".{Path.GetFileName(target.DocumentPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            WriteDurable(temporaryPath, content);
            if (!File.Exists(target.DocumentPath))
            {
                File.Move(temporaryPath, target.DocumentPath);
                return;
            }

            if (TryRead(target, target.DocumentPath, out _, verifyExistingAssetMetadata: false))
            {
                File.Replace(temporaryPath, target.DocumentPath, target.BackupPath, ignoreMetadataErrors: true);
                return;
            }

            var invalidPath = Path.Combine(
                target.DocumentDirectory,
                $"{Path.GetFileName(target.DocumentPath)}.invalid-{_timeProvider.GetUtcNow():yyyyMMdd-HHmmssfff}");
            File.Move(target.DocumentPath, invalidPath);
            File.Move(temporaryPath, target.DocumentPath);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private static FileInfo ValidateSourceImage(string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var path = Path.GetFullPath(sourcePath);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("需要导入的图片不存在。", path);
        }

        EnsureNotReparsePoint(path, "导入图片");
        var file = new FileInfo(path);
        if (file.Length <= 0 || file.Length > MaximumSourceImageBytes)
        {
            throw new InvalidDataException("导入图片为空或超过 512 MiB 限制。");
        }

        if (!SupportedImageMediaTypes.ContainsKey(file.Extension))
        {
            throw new InvalidDataException($"不支持的图片扩展名：{file.Extension}。");
        }

        _ = ReadPixelSize(path);
        return file;
    }

    private static (int Width, int Height) ReadPixelSize(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var imageInfo = Image.Identify(stream)
            ?? throw new InvalidDataException("图片没有可读取的像素帧。");
        if (imageInfo.Width <= 0 || imageInfo.Height <= 0)
        {
            throw new InvalidDataException("图片没有可读取的像素帧。");
        }

        if (imageInfo.Width > 65536 || imageInfo.Height > 65536)
        {
            throw new InvalidDataException("图片任一边的像素尺寸不能超过 65,536。");
        }

        if ((long)imageInfo.Width * imageInfo.Height > ArtWorkspaceValidator.MaximumSourcePixelCount)
        {
            throw new InvalidDataException(
                $"图片像素总数不能超过 {ArtWorkspaceValidator.MaximumSourcePixelCount:N0}。请先裁切或缩小图片。");
        }

        return (imageInfo.Width, imageInfo.Height);
    }

    private static string ComputeSha256(string path, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                break;
            }

            hash.AppendData(buffer, 0, read);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void CopyFileAtomic(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(destinationPath)
            ?? throw new InvalidOperationException("无法确定参考图片目标目录。");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var source = new FileStream(
                       sourcePath,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.Read,
                       1024 * 1024,
                       FileOptions.SequentialScan))
            using (var destination = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       1024 * 1024,
                       FileOptions.WriteThrough))
            {
                var buffer = new byte[1024 * 1024];
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var read = source.Read(buffer, 0, buffer.Length);
                    if (read == 0)
                    {
                        break;
                    }

                    destination.Write(buffer, 0, read);
                }

                destination.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, destinationPath);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private static void ValidateDocumentPaths(
        ArtWorkspaceStorageTarget target,
        ArtWorkspaceDocument document,
        bool verifyExistingAssetMetadata)
    {
        foreach (var asset in document.Assets)
        {
            var path = ResolveManagedPath(target, asset.RelativePath, mustExist: false);
            if (!verifyExistingAssetMetadata || !File.Exists(path))
            {
                continue;
            }

            EnsureNotReparsePoint(path, "美术工作区素材文件");
            var file = new FileInfo(path);
            if (file.Length <= 0 || file.Length > MaximumSourceImageBytes || file.Length != asset.SizeBytes)
            {
                throw new InvalidDataException($"美术工作区素材文件大小与冻结元数据不一致：{asset.RelativePath}");
            }

            var (width, height) = ReadPixelSize(path);
            if (width != asset.PixelWidth || height != asset.PixelHeight)
            {
                throw new InvalidDataException($"美术工作区素材像素尺寸与冻结元数据不一致：{asset.RelativePath}");
            }
        }
    }

    private static string ResolveManagedPath(
        ArtWorkspaceStorageTarget target,
        string relativePath,
        bool mustExist)
    {
        if (Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException("美术工作区素材路径必须是相对路径。");
        }

        var candidate = Path.GetFullPath(Path.Combine(
            target.StorageRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsWithin(target.ReferenceDirectory, candidate))
        {
            throw new InvalidDataException("美术工作区素材路径必须位于受管参考图片目录内。");
        }

        EnsureExistingPathSegmentsAreNotReparsePoints(target.StorageRoot, candidate);
        if (mustExist && !File.Exists(candidate))
        {
            throw new FileNotFoundException("美术工作区素材文件不存在。", candidate);
        }

        return candidate;
    }

    private static void EnsureManagedDirectory(string trustedRoot, string path)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(trustedRoot));
        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!Directory.Exists(root))
        {
            Directory.CreateDirectory(root);
        }

        if (!IsWithin(root, target))
        {
            throw new InvalidDataException("美术工作区目录试图离开受信根目录。");
        }

        EnsureNotReparsePoint(root, "美术工作区受信根目录");
        var relative = Path.GetRelativePath(root, target);
        var current = root;
        foreach (var segment in relative.Split(
                     new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (File.Exists(current))
            {
                throw new IOException($"美术工作区目录路径被文件占用：{current}");
            }

            if (!Directory.Exists(current))
            {
                Directory.CreateDirectory(current);
            }

            EnsureNotReparsePoint(current, "美术工作区受管目录");
        }
    }

    private static void EnsureExistingPathSegmentsAreNotReparsePoints(string root, string candidate)
    {
        EnsureNotReparsePoint(root, "美术工作区受信根目录");
        var relative = Path.GetRelativePath(root, candidate);
        var current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        foreach (var segment in relative.Split(
                     new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!File.Exists(current) && !Directory.Exists(current))
            {
                break;
            }

            EnsureNotReparsePoint(current, "美术工作区受管路径");
        }
    }

    private static void EnsureManagedFileSlot(string path, string label)
    {
        if (Directory.Exists(path))
        {
            throw new IOException($"{label}路径被目录占用：{path}");
        }

        if (File.Exists(path))
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
            // Unique temporary files can be cleaned by later maintenance.
        }
    }

    private sealed record ArtWorkspaceStorageTarget(
        string StorageRoot,
        string DocumentDirectory,
        string DocumentPath,
        string BackupPath,
        string ReferenceDirectory);
}
