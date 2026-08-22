using System.Globalization;
using GAssetForge.Core.Generation;
using GAssetForge.Core.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace GAssetForge.McpServer;

internal sealed record GeneratedBackgroundRemovalStatistics(
    string KeyColorHex,
    int Width,
    int Height,
    long RemovedPixelCount,
    long TransparentPixelCount,
    long PartialPixelCount,
    long OpaquePixelCount);

internal sealed record GeneratedOutputProcessingResult(
    int OutputIndex,
    string? RelativeStagingPath,
    string? RelativeProcessedPath,
    string? RelativeTargetPath,
    bool Processed,
    bool AlreadyFinal,
    bool Skipped,
    bool ReplacedExistingTarget,
    bool StagingDeleted,
    string Message,
    GeneratedBackgroundRemovalStatistics? Statistics);

internal sealed class GeneratedAssetPostProcessor(WorkspaceContextResolver contextResolver)
{
    private const long Mebibyte = 1024L * 1024L;
    private const long MaximumSourceFileBytes = 544L * Mebibyte;
    private const double EdgeColorDistanceEpsilon = 0.000001d;
    private const double MaximumEdgeMismatchRatio = 0.125d;
    private const int MinimumEdgeMismatchAllowance = 2;

    private static readonly byte[] PngSignature =
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    // ImageSharp expands sources to RGBA and the largest supported canvases are memory intensive.
    // Keep all MCP callers in this process behind one gate to prevent parallel 8192² allocations.
    private static readonly SemaphoreSlim ProcessingGate = new(1, 1);

    private readonly IBackgroundRemovalService _backgroundRemovalService =
        new ImageSharpBackgroundRemovalService();

    public async Task<GeneratedOutputProcessingResult> ProcessAsync(
        GenerationTask task,
        GenerationDeliveryPlan? delivery,
        string agentId,
        int outputIndex,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        EnsureClaimOwnership(task, agentId);

        var output = task.Constraints.Output
            ?? throw new InvalidDataException("生成任务缺少输出规格。");
        if (outputIndex < 0 || outputIndex >= output.Count)
        {
            throw new InvalidDataException(
                $"outputIndex 必须是 0 到 {output.Count - 1} 之间的 0-based 索引。");
        }

        if (output.BackgroundRemoval is null)
        {
            return ValidateLegacyOutput(task, delivery, outputIndex);
        }

        if (delivery is null)
        {
            throw new InvalidDataException("启用背景剔除的任务必须包含 parameters.delivery。");
        }

        if (delivery.TargetPaths.Length != output.Count ||
            delivery.StagingPaths.Length != output.Count)
        {
            throw new InvalidDataException(
                "背景剔除任务的 delivery.targetPaths 与 delivery.stagingPaths 必须和 output.count 按索引一一对应。");
        }

        await ProcessingGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await ProcessBackgroundRemovalAsync(
                    task,
                    delivery,
                    output.BackgroundRemoval,
                    outputIndex,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            ProcessingGate.Release();
        }
    }

    private GeneratedOutputProcessingResult ValidateLegacyOutput(
        GenerationTask task,
        GenerationDeliveryPlan? delivery,
        int outputIndex)
    {
        if (delivery is null)
        {
            return new GeneratedOutputProcessingResult(
                outputIndex,
                null,
                null,
                null,
                Processed: false,
                AlreadyFinal: false,
                Skipped: true,
                ReplacedExistingTarget: false,
                StagingDeleted: false,
                "该旧任务未启用背景剔除且没有冻结 delivery，无需调用本地输出处理工具。",
                null);
        }

        if (delivery.TargetPaths.Length != task.Constraints.Output.Count)
        {
            throw new InvalidDataException("旧任务的 delivery.targetPaths 数量与 output.count 不一致。");
        }

        var target = contextResolver.ResolveDeliveryAssetPath(
            delivery.TargetPaths[outputIndex],
            delivery.AssetRoot,
            requireExistingFile: true,
            "最终交付");
        GeneratedAssetValidator.Validate(target.FullPath, task.Constraints);
        return new GeneratedOutputProcessingResult(
            outputIndex,
            null,
            null,
            target.RelativePath,
            Processed: false,
            AlreadyFinal: true,
            Skipped: true,
            ReplacedExistingTarget: false,
            StagingDeleted: false,
            "任务未启用背景剔除；对应最终目标已存在并通过约束校验。",
            null);
    }

    private async Task<GeneratedOutputProcessingResult> ProcessBackgroundRemovalAsync(
        GenerationTask task,
        GenerationDeliveryPlan delivery,
        GenerationBackgroundRemovalSpec removalSpec,
        int outputIndex,
        CancellationToken cancellationToken)
    {
        var target = contextResolver.ResolveDeliveryAssetPath(
            delivery.TargetPaths[outputIndex],
            delivery.AssetRoot,
            requireExistingFile: false,
            "最终交付");
        var staging = contextResolver.ResolveDeliveryAssetPath(
            delivery.StagingPaths[outputIndex],
            delivery.AssetRoot,
            requireExistingFile: false,
            "背景剔除 staging");
        if (PathsEqual(staging.FullPath, target.FullPath))
        {
            throw new InvalidDataException("背景剔除 staging 路径不能与最终交付路径相同。");
        }

        if (File.Exists(target.FullPath) && IsValidFinalTarget(target.FullPath, task.Constraints))
        {
            var cleanup = delivery.Workspace?.PreserveIntermediates == true
                ? new StagingCleanupResult(false, null)
                : TryDeleteStagingIfPresent(staging, delivery.AssetRoot);
            return new GeneratedOutputProcessingResult(
                outputIndex,
                staging.RelativePath,
                delivery.Workspace?.ProcessedPaths.ElementAtOrDefault(outputIndex),
                target.RelativePath,
                Processed: false,
                AlreadyFinal: true,
                Skipped: true,
                ReplacedExistingTarget: false,
                cleanup.Deleted,
                AppendCleanupDiagnostic(
                    "最终目标已经通过约束校验，已幂等跳过重复后处理。",
                    cleanup),
                null);
        }

        staging = contextResolver.ResolveDeliveryAssetPath(
            staging.RelativePath,
            delivery.AssetRoot,
            requireExistingFile: true,
            "背景剔除 staging");
        var targetDirectory = Path.GetDirectoryName(target.FullPath)
            ?? throw new InvalidOperationException("无法解析最终交付目录。");
        Directory.CreateDirectory(targetDirectory);
        target = contextResolver.ResolveDeliveryAssetPath(
            target.RelativePath,
            delivery.AssetRoot,
            requireExistingFile: false,
            "最终交付");

        var temporaryPath = Path.Combine(
            targetDirectory,
            $".pfpost-{Guid.NewGuid():N}.tmp.png");
        string? finalTemporaryPath = null;
        string? processedRelativePath = null;
        BackgroundRemovalResult removalResult;
        try
        {
            using (var sourceLease = new FileStream(
                       staging.FullPath,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.Read,
                       bufferSize: 64 * 1024,
                       FileOptions.SequentialScan))
            {
                ValidateSourceBeforeDecode(
                    sourceLease,
                    staging.FullPath,
                    task.Constraints,
                    removalSpec);
                cancellationToken.ThrowIfCancellationRequested();
                var settings = new BackgroundRemovalSettings
                {
                    KeyColorHex = removalSpec.ColorHex,
                    TransparentThreshold = removalSpec.TransparentThreshold,
                    OpaqueThreshold = removalSpec.OpaqueThreshold,
                    EdgeContract = removalSpec.EdgeContract,
                    EdgeFeather = removalSpec.EdgeFeather,
                    Despill = removalSpec.Despill,
                    EdgeConnectedOnly = removalSpec.EdgeConnectedOnly
                };
                removalResult = await Task.Run(
                        () => _backgroundRemovalService.RemoveToPng(
                            staging.FullPath,
                            temporaryPath,
                            settings,
                            overwrite: false),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            GeneratedAssetValidator.Validate(temporaryPath, task.Constraints);
            cancellationToken.ThrowIfCancellationRequested();
            if (delivery.Workspace is { ProcessedPaths.Length: > 0 } workspace)
            {
                if (workspace.ProcessedPaths.Length != task.Constraints.Output.Count)
                {
                    throw new InvalidDataException(
                        "运行工作区 processedPaths 必须与 output.count 按索引一一对应。");
                }

                var processed = contextResolver.ResolveDeliveryAssetPath(
                    workspace.ProcessedPaths[outputIndex],
                    delivery.AssetRoot,
                    requireExistingFile: false,
                    "背景剔除中间产物");
                if (PathsEqual(processed.FullPath, staging.FullPath) ||
                    PathsEqual(processed.FullPath, target.FullPath))
                {
                    throw new InvalidDataException("背景剔除中间产物路径不能与 normalized 或最终目标相同。");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(processed.FullPath)!);
                _ = CommitValidatedTemporary(temporaryPath, processed.FullPath, task.Constraints);
                GeneratedAssetValidator.Validate(processed.FullPath, task.Constraints);
                processedRelativePath = processed.RelativePath;
                finalTemporaryPath = Path.Combine(
                    targetDirectory,
                    $".pfcommit-{Guid.NewGuid():N}.tmp.png");
                File.Copy(processed.FullPath, finalTemporaryPath, overwrite: false);
                GeneratedAssetValidator.Validate(finalTemporaryPath, task.Constraints);
            }

            target = contextResolver.ResolveDeliveryAssetPath(
                target.RelativePath,
                delivery.AssetRoot,
                requireExistingFile: false,
                "最终交付");
            var commit = CommitValidatedTemporary(
                finalTemporaryPath ?? temporaryPath,
                target.FullPath,
                task.Constraints);
            GeneratedAssetValidator.Validate(target.FullPath, task.Constraints);
            var cleanup = delivery.Workspace?.PreserveIntermediates == true
                ? new StagingCleanupResult(false, null)
                : TryDeleteStagingIfPresent(staging, delivery.AssetRoot);
            var successMessage = commit.ExistingValidTargetWon
                ? "并发处理期间已有有效最终目标完成提交，已保留该目标。"
                : "纯色底原图已在本地剔色、通过最终约束校验并原子提交。";
            return new GeneratedOutputProcessingResult(
                outputIndex,
                staging.RelativePath,
                processedRelativePath,
                target.RelativePath,
                Processed: !commit.ExistingValidTargetWon,
                AlreadyFinal: commit.ExistingValidTargetWon,
                Skipped: commit.ExistingValidTargetWon,
                ReplacedExistingTarget: commit.ReplacedExistingTarget,
                cleanup.Deleted,
                AppendCleanupDiagnostic(successMessage, cleanup),
                new GeneratedBackgroundRemovalStatistics(
                    removalResult.KeyColorHex,
                    removalResult.Width,
                    removalResult.Height,
                    removalResult.RemovedPixelCount,
                    removalResult.TransparentPixelCount,
                    removalResult.PartialPixelCount,
                    removalResult.OpaquePixelCount));
        }
        finally
        {
            TryDeleteFile(temporaryPath);
            if (finalTemporaryPath is not null)
            {
                TryDeleteFile(finalTemporaryPath);
            }
        }
    }

    private static void ValidateSourceBeforeDecode(
        FileStream source,
        string fullPath,
        GenerationConstraintPackage constraints,
        GenerationBackgroundRemovalSpec removalSpec)
    {
        if (source.Length <= 0)
        {
            throw new InvalidDataException($"背景剔除 staging 文件为空：{fullPath}");
        }

        if (source.Length > MaximumSourceFileBytes)
        {
            throw new InvalidDataException(
                $"背景剔除 staging 文件超过 {MaximumSourceFileBytes / Mebibyte} MiB 上限：{fullPath}");
        }

        Span<byte> signature = stackalloc byte[PngSignature.Length];
        try
        {
            source.ReadExactly(signature);
        }
        catch (EndOfStreamException exception)
        {
            throw new InvalidDataException($"背景剔除 staging 不是完整 PNG：{fullPath}", exception);
        }

        if (!signature.SequenceEqual(PngSignature))
        {
            throw new InvalidDataException($"背景剔除 staging 必须是真实 PNG：{fullPath}");
        }

        source.Position = 0;
        var imageInfo = Image.Identify(source)
            ?? throw new InvalidDataException($"无法识别背景剔除 staging 图像：{fullPath}");
        if (imageInfo.Width != constraints.Output.Width || imageInfo.Height != constraints.Output.Height)
        {
            throw new InvalidDataException(
                $"背景剔除 staging 尺寸不匹配：任务要求 {constraints.Output.Width}×{constraints.Output.Height}，" +
                $"文件为 {imageInfo.Width}×{imageInfo.Height}（{fullPath}）。");
        }

        long decodedBytes;
        try
        {
            decodedBytes = checked(
                (long)imageInfo.Width *
                imageInfo.Height *
                GenerationBackgroundRemovalSpec.DecodedRgbaBytesPerPixel);
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException($"背景剔除 staging 解码尺寸溢出：{fullPath}", exception);
        }

        if (decodedBytes > GenerationBackgroundRemovalSpec.MaximumDecodedRgbaBytes)
        {
            throw new InvalidDataException(
                $"背景剔除 staging 解码数据超过 " +
                $"{GenerationBackgroundRemovalSpec.MaximumDecodedRgbaBytes / Mebibyte} MiB 上限：{fullPath}");
        }

        source.Position = 0;
        using var image = Image.Load<Rgba32>(source);
        ValidateOpaqueChromaKeyStage(image, removalSpec, fullPath);
        source.Position = 0;
    }

    private static void ValidateOpaqueChromaKeyStage(
        Image<Rgba32> image,
        GenerationBackgroundRemovalSpec removalSpec,
        string fullPath)
    {
        var keyColor = ParseKeyColor(removalSpec.ColorHex);
        long nonOpaquePixelCount = 0;
        var firstNonOpaqueX = -1;
        var firstNonOpaqueY = -1;
        var firstNonOpaqueAlpha = byte.MaxValue;
        var topMatches = 0;
        var bottomMatches = 0;
        var leftMatches = 0;
        var rightMatches = 0;

        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    var pixel = row[x];
                    if (pixel.A != byte.MaxValue)
                    {
                        nonOpaquePixelCount++;
                        if (firstNonOpaqueX < 0)
                        {
                            firstNonOpaqueX = x;
                            firstNonOpaqueY = y;
                            firstNonOpaqueAlpha = pixel.A;
                        }
                    }

                    var isTransparentKeyColor = ColorDistance(pixel, keyColor) <=
                                                removalSpec.TransparentThreshold + EdgeColorDistanceEpsilon;
                    if (y == 0 && isTransparentKeyColor)
                    {
                        topMatches++;
                    }

                    if (y == accessor.Height - 1 && isTransparentKeyColor)
                    {
                        bottomMatches++;
                    }

                    if (x == 0 && isTransparentKeyColor)
                    {
                        leftMatches++;
                    }

                    if (x == row.Length - 1 && isTransparentKeyColor)
                    {
                        rightMatches++;
                    }
                }
            }
        });

        if (nonOpaquePixelCount > 0)
        {
            throw new InvalidDataException(
                $"背景剔除第一阶段 staging 必须完全不透明（所有像素 Alpha=255）；" +
                $"检测到 {nonOpaquePixelCount} 个非 255 Alpha 像素，首个为 " +
                $"({firstNonOpaqueX},{firstNonOpaqueY}) Alpha={firstNonOpaqueAlpha}（{fullPath}）。");
        }

        var cornerFailures = new List<string>(4);
        AddCornerFailureIfNeeded(cornerFailures, image[0, 0], keyColor, removalSpec, "左上");
        AddCornerFailureIfNeeded(cornerFailures, image[image.Width - 1, 0], keyColor, removalSpec, "右上");
        AddCornerFailureIfNeeded(cornerFailures, image[0, image.Height - 1], keyColor, removalSpec, "左下");
        AddCornerFailureIfNeeded(
            cornerFailures,
            image[image.Width - 1, image.Height - 1],
            keyColor,
            removalSpec,
            "右下");
        if (cornerFailures.Count > 0)
        {
            throw new InvalidDataException(
                $"背景剔除 staging 画布角点必须匹配冻结底色 {removalSpec.ColorHex}，" +
                $"且 RGB 距离不得超过 transparentThreshold={FormatThreshold(removalSpec.TransparentThreshold)}；" +
                $"不匹配角点：{string.Join("、", cornerFailures)}（{fullPath}）。");
        }

        ValidateEdgeCoverage("上边缘", image.Width, topMatches, removalSpec, fullPath);
        ValidateEdgeCoverage("下边缘", image.Width, bottomMatches, removalSpec, fullPath);
        ValidateEdgeCoverage("左边缘", image.Height, leftMatches, removalSpec, fullPath);
        ValidateEdgeCoverage("右边缘", image.Height, rightMatches, removalSpec, fullPath);
    }

    private static void AddCornerFailureIfNeeded(
        ICollection<string> failures,
        Rgba32 pixel,
        Rgba32 keyColor,
        GenerationBackgroundRemovalSpec removalSpec,
        string cornerName)
    {
        var distance = ColorDistance(pixel, keyColor);
        if (distance > removalSpec.TransparentThreshold + EdgeColorDistanceEpsilon)
        {
            failures.Add($"{cornerName}(距离 {FormatThreshold(distance)})");
        }
    }

    private static void ValidateEdgeCoverage(
        string edgeName,
        int pixelCount,
        int matchingPixelCount,
        GenerationBackgroundRemovalSpec removalSpec,
        string fullPath)
    {
        var mismatchCount = pixelCount - matchingPixelCount;
        var allowedMismatchCount = Math.Max(
            MinimumEdgeMismatchAllowance,
            (int)Math.Floor(pixelCount * MaximumEdgeMismatchRatio));
        if (mismatchCount <= allowedMismatchCount)
        {
            return;
        }

        throw new InvalidDataException(
            $"背景剔除 staging 画布{edgeName}未形成稳定纯色底：冻结底色 {removalSpec.ColorHex}，" +
            $"transparentThreshold={FormatThreshold(removalSpec.TransparentThreshold)}，" +
            $"匹配 {matchingPixelCount}/{pixelCount}，最多允许 {allowedMismatchCount} 个边缘接触/抗锯齿像素偏离" +
            $"（{fullPath}）。");
    }

    private static Rgba32 ParseKeyColor(string colorHex)
    {
        var normalized = BackgroundKeyColor.NormalizeHex(colorHex).AsSpan(1);
        return new Rgba32(
            byte.Parse(normalized[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(normalized.Slice(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(normalized.Slice(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.MaxValue);
    }

    private static double ColorDistance(Rgba32 pixel, Rgba32 keyColor)
    {
        var red = pixel.R - keyColor.R;
        var green = pixel.G - keyColor.G;
        var blue = pixel.B - keyColor.B;
        return Math.Sqrt(red * red + green * green + blue * blue);
    }

    private static string FormatThreshold(double value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);

    private static CommitResult CommitValidatedTemporary(
        string temporaryPath,
        string targetPath,
        GenerationConstraintPackage constraints)
    {
        if (!File.Exists(targetPath))
        {
            try
            {
                File.Move(temporaryPath, targetPath);
                return new CommitResult(false, false);
            }
            catch (IOException) when (File.Exists(targetPath))
            {
                if (IsValidFinalTarget(targetPath, constraints))
                {
                    return new CommitResult(false, true);
                }

                // Another writer created an invalid target between validation and commit. Continue
                // through the backup-backed replace path below without ever truncating it in place.
            }
        }

        if (IsValidFinalTarget(targetPath, constraints))
        {
            return new CommitResult(false, true);
        }

        var targetDirectory = Path.GetDirectoryName(targetPath)
            ?? throw new InvalidOperationException("无法解析最终交付目录。");
        var backupPath = Path.Combine(
            targetDirectory,
            $".pfbackup-{Guid.NewGuid():N}.png");
        var replacementValidated = false;
        try
        {
            File.Replace(temporaryPath, targetPath, backupPath, ignoreMetadataErrors: true);
            try
            {
                GeneratedAssetValidator.Validate(targetPath, constraints);
                replacementValidated = true;
            }
            catch (Exception validationException)
            {
                try
                {
                    File.Replace(backupPath, targetPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
                }
                catch (Exception restoreException)
                {
                    throw new IOException(
                        $"最终产物替换后的复核失败，且无法从备份恢复；备份保留在 {backupPath}。",
                        new AggregateException(validationException, restoreException));
                }

                throw;
            }

            return new CommitResult(true, false);
        }
        finally
        {
            if (replacementValidated)
            {
                TryDeleteFile(backupPath);
            }
        }
    }

    private StagingCleanupResult TryDeleteStagingIfPresent(
        ValidatedAssetPath staging,
        string assetRoot)
    {
        try
        {
            _ = File.GetAttributes(staging.FullPath);
            var revalidated = contextResolver.ResolveDeliveryAssetPath(
                staging.RelativePath,
                assetRoot,
                requireExistingFile: true,
                "背景剔除 staging");
            File.Delete(revalidated.FullPath);
            return new StagingCleanupResult(true, null);
        }
        catch (FileNotFoundException)
        {
            return new StagingCleanupResult(false, null);
        }
        catch (DirectoryNotFoundException)
        {
            return new StagingCleanupResult(false, null);
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or InvalidDataException or
            NotSupportedException or System.Security.SecurityException)
        {
            var diagnostic =
                $"staging 清理失败，最终 target 已保留，可稍后重试；" +
                $"{exception.GetType().Name}: {exception.Message}";
            Console.Error.WriteLine(
                $"[GAssetForge MCP] {diagnostic}（{staging.RelativePath}）");
            return new StagingCleanupResult(false, diagnostic);
        }
    }

    internal static bool IsValidFinalTarget(
        string targetPath,
        GenerationConstraintPackage constraints)
    {
        if (!File.Exists(targetPath))
        {
            return false;
        }

        try
        {
            GeneratedAssetValidator.Validate(targetPath, constraints);
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static void EnsureClaimOwnership(GenerationTask task, string agentId)
    {
        if (task.Status != GenerationTaskStatus.Claimed)
        {
            throw new InvalidOperationException(
                $"任务 {task.Id} 当前状态为 {task.Status}，只能处理处于 Claimed 的任务。");
        }

        if (!string.Equals(task.ClaimedBy, agentId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"任务 {task.Id} 已由 Agent {task.ClaimedBy} 认领。");
        }
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(
                $"[GAssetForge MCP] 无法清理后处理临时文件 {path}：{exception.Message}");
        }
    }

    private static string AppendCleanupDiagnostic(
        string successMessage,
        StagingCleanupResult cleanup) =>
        cleanup.Diagnostic is null
            ? successMessage
            : $"{successMessage} {cleanup.Diagnostic}";

    private readonly record struct CommitResult(
        bool ReplacedExistingTarget,
        bool ExistingValidTargetWon);

    private readonly record struct StagingCleanupResult(
        bool Deleted,
        string? Diagnostic);
}
