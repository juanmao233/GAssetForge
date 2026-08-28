using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using GAssetForge.Core.Generation;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace GAssetForge.McpServer;

internal sealed record GeneratedOutputSubmissionResult(
    int OutputIndex,
    string SourcePath,
    string ModelOutputPath,
    string NormalizedPath,
    string? ProcessedPath,
    string TargetPath,
    string SourceSha256,
    bool SourceResized,
    GeneratedOutputProcessingResult Processing);

internal sealed record ArtWorkspaceRegionEditDiagnostics(
    string BaseImagePath,
    string MaskPath,
    string CandidatePath,
    string CompositePath,
    string DifferenceReportPath,
    string DifferencePreviewPath,
    int EditablePixelCount,
    int PreservedPixelCount,
    int SignificantOutsideChangedPixels,
    double SignificantOutsideChangedPercent,
    double MaximumAllowedPercent,
    int SignificantChannelDelta,
    bool Passed);

internal sealed class ArtWorkspaceRegionEditValidationException(
    string message,
    ArtWorkspaceRegionEditDiagnostics diagnostics)
    : InvalidOperationException(message)
{
    internal const string ErrorCode = "art_workspace_region_edit_outside_mask_changed";

    internal ArtWorkspaceRegionEditDiagnostics Diagnostics { get; } = diagnostics;
}

/// <summary>
/// Imports an image-model return into the frozen project workspace and executes every deterministic
/// image step that previously required Agent-authored PowerShell or ad-hoc file manipulation.
/// </summary>
internal sealed class GeneratedOutputSubmissionService(
    WorkspaceContextResolver contextResolver,
    GeneratedAssetPostProcessor postProcessor)
{
    private const long MaximumSourceFileBytes = 544L * 1024L * 1024L;
    private static readonly byte[] PngSignature =
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public async Task<GeneratedOutputSubmissionResult> SubmitAsync(
        GenerationTask task,
        GenerationDeliveryPlan delivery,
        string agentId,
        int outputIndex,
        string sourcePath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(delivery);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        if (delivery.Workspace is not { } workspace)
        {
            throw new InvalidDataException(
                "该任务没有项目内 generation workspace；旧任务请使用 process_generation_output。 ");
        }

        if (task.Status != GenerationTaskStatus.Claimed ||
            !string.Equals(task.ClaimedBy, agentId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"任务 {task.Id} 必须由当前 agentId 认领后才能提交模型产物。");
        }

        var output = task.Constraints.Output;
        ArtWorkspaceRegionEditContract? regionEditContract = null;
        if (!ArtWorkspaceRegionEditContractComposer.TryRead(
                task.Constraints,
                out regionEditContract) &&
            ArtWorkspaceRegionEditContractComposer.HasDeclaredContract(task.Constraints))
        {
            throw new InvalidDataException("任务声明了损坏或不受支持的美术工作区局部编辑合同。");
        }

        var hasBackgroundRemoval = output.BackgroundRemoval is not null;
        if (outputIndex < 0 || outputIndex >= output.Count ||
            workspace.ModelOutputPaths.Length != output.Count ||
            workspace.ProcessedPaths.Length != output.Count ||
            delivery.TargetPaths.Length != output.Count ||
            (hasBackgroundRemoval
                ? delivery.StagingPaths.Length != output.Count
                : delivery.StagingPaths.Length != 0))
        {
            throw new InvalidDataException("运行工作区输出路径与 output.count 不一致。");
        }

        var normalizedSourcePath = ValidateSourcePath(sourcePath, delivery.AssetRoot);
        var modelOutput = contextResolver.ResolveDeliveryAssetPath(
            workspace.ModelOutputPaths[outputIndex],
            delivery.AssetRoot,
            requireExistingFile: false,
            "模型原图归档");
        var target = contextResolver.ResolveDeliveryAssetPath(
            delivery.TargetPaths[outputIndex],
            delivery.AssetRoot,
            requireExistingFile: false,
            "最终交付");
        var normalized = contextResolver.ResolveDeliveryAssetPath(
            hasBackgroundRemoval
                ? delivery.StagingPaths[outputIndex]
                : BuildDirectNormalizedPath(workspace, outputIndex),
            delivery.AssetRoot,
            requireExistingFile: false,
            "软件归一化输入");

        string sourceSha256;
        using (var measurement = GenerationRunJournal.Measure(
                   delivery,
                   task,
                   "model_output.import",
                   agentId,
                   outputIndex,
                   modelOutput.RelativePath,
                   normalizedSourcePath))
        {
            try
            {
                sourceSha256 = await ImportModelOutputAsync(
                        normalizedSourcePath,
                        modelOutput.FullPath,
                        cancellationToken)
                    .ConfigureAwait(false);
                measurement.Complete(
                    $"模型原图已由软件归档，sha256={sourceSha256}",
                    modelOutput.RelativePath);
            }
            catch (Exception exception)
            {
                measurement.Fail(exception);
                throw;
            }
        }

        bool resized;
        using (var measurement = GenerationRunJournal.Measure(
                   delivery,
                   task,
                   "model_output.normalize",
                   agentId,
                   outputIndex,
                   normalized.RelativePath,
                   modelOutput.RelativePath))
        {
            try
            {
                resized = await NormalizeAsync(
                        modelOutput.FullPath,
                        normalized.FullPath,
                        output,
                        preserveNormalizedCoordinates: regionEditContract is not null,
                        cancellationToken)
                    .ConfigureAwait(false);
                measurement.Complete(
                    regionEditContract is not null
                        ? "局部编辑候选已由 Harness 无插值恢复为 authoritative base 精确像素尺寸。"
                        : resized
                        ? "软件已按冻结画布 contain-fit 并补齐精确首道底色。"
                        : "模型原图尺寸已匹配；软件仍完成了不透明底色与边框归一化。",
                    normalized.RelativePath);
            }
            catch (Exception exception)
            {
                measurement.Fail(exception);
                throw;
            }
        }

        GeneratedOutputProcessingResult processing;
        using (var measurement = GenerationRunJournal.Measure(
                   delivery,
                   task,
                   "output.process_and_commit",
                   agentId,
                   outputIndex,
                   target.RelativePath,
                   normalized.RelativePath))
        {
            try
            {
                processing = regionEditContract is not null
                    ? await CommitRegionEditOutputAsync(
                            task,
                            delivery,
                            workspace,
                            regionEditContract,
                            normalized,
                            target,
                            outputIndex,
                            cancellationToken)
                        .ConfigureAwait(false)
                    : hasBackgroundRemoval
                    ? await postProcessor.ProcessAsync(
                            task,
                            delivery,
                            agentId,
                            outputIndex,
                            cancellationToken)
                        .ConfigureAwait(false)
                    : await CommitDirectOutputAsync(
                            task,
                            delivery,
                            workspace,
                            normalized,
                            target,
                            outputIndex,
                            cancellationToken)
                        .ConfigureAwait(false);
                measurement.Complete(processing.Message, processing.RelativeTargetPath);
            }
            catch (TilemapGeometryValidationException exception)
            {
                measurement.Fail(exception, exception.ErrorCode);
                throw;
            }
            catch (ArtWorkspaceRegionEditValidationException exception)
            {
                measurement.Fail(exception, ArtWorkspaceRegionEditValidationException.ErrorCode);
                throw;
            }
            catch (Exception exception)
            {
                measurement.Fail(exception);
                throw;
            }
        }

        return new GeneratedOutputSubmissionResult(
            outputIndex,
            normalizedSourcePath,
            modelOutput.RelativePath,
            normalized.RelativePath,
            processing.RelativeProcessedPath,
            processing.RelativeTargetPath ?? target.RelativePath,
            sourceSha256,
            resized,
            processing);
    }

    private static string ValidateSourcePath(string sourcePath, string assetRoot)
    {
        if (!Path.IsPathFullyQualified(sourcePath))
        {
            throw new InvalidDataException("sourcePath 必须是图像模型返回的绝对本地文件路径。");
        }

        var fullPath = Path.GetFullPath(sourcePath);
        var allowedRoots = new[]
            {
                Path.GetFullPath(assetRoot),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"),
                Path.GetFullPath(Path.GetTempPath()),
                Path.GetFullPath(Environment.CurrentDirectory)
            }
            .Where(Directory.Exists)
            .Select(Path.GetFullPath)
            .Distinct(OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal)
            .ToArray();
        if (!allowedRoots.Any(root => IsWithinRoot(root, fullPath)))
        {
            throw new UnauthorizedAccessException(
                "sourcePath 只能来自当前项目、Codex 生成目录、系统临时目录或 MCP 工作目录。");
        }

        var info = new FileInfo(fullPath);
        if (!info.Exists || (info.Attributes & FileAttributes.Directory) != 0)
        {
            throw new FileNotFoundException("图像模型返回文件不存在。", fullPath);
        }

        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("拒绝导入重解析点模型产物。");
        }

        if (info.Length is <= 0 or > MaximumSourceFileBytes)
        {
            throw new InvalidDataException(
                $"模型产物大小必须位于 1 到 {MaximumSourceFileBytes / 1024 / 1024} MiB 之间。");
        }

        using var stream = info.Open(FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> signature = stackalloc byte[PngSignature.Length];
        try
        {
            stream.ReadExactly(signature);
        }
        catch (EndOfStreamException exception)
        {
            throw new InvalidDataException("图像模型返回文件不是完整 PNG。", exception);
        }

        if (!signature.SequenceEqual(PngSignature))
        {
            throw new InvalidDataException("submit_generation_output 当前只接受真实 PNG 模型产物。");
        }

        stream.Position = 0;
        _ = Image.Identify(stream)
            ?? throw new InvalidDataException("无法识别图像模型返回的 PNG。");
        return fullPath;
    }

    private static string BuildDirectNormalizedPath(
        GenerationRunWorkspacePlan workspace,
        int outputIndex)
    {
        var rootPath = GenerationInputValidator.NormalizeRelativeAssetPath(workspace.RootPath);
        var modelPath = GenerationInputValidator.NormalizeRelativeAssetPath(
            workspace.ModelOutputPaths[outputIndex]);
        var modelPrefix = $"{rootPath}/30-model-output/";
        if (!modelPath.StartsWith(modelPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "无背景剔除任务的模型原图必须位于 generation workspace 的 30-model-output 目录。");
        }

        var suffix = modelPath[modelPrefix.Length..];
        var fileName = Path.GetFileName(suffix);
        var directory = Path.GetDirectoryName(suffix)?.Replace('\\', '/');
        var normalizedFileName = fileName.EndsWith(".model.png", StringComparison.OrdinalIgnoreCase)
            ? $"{fileName[..^".model.png".Length]}.normalized.png"
            : $"{Path.GetFileNameWithoutExtension(fileName)}.normalized.png";
        var childPath = string.IsNullOrWhiteSpace(directory)
            ? normalizedFileName
            : $"{directory}/{normalizedFileName}";
        return GenerationInputValidator.NormalizeRelativeAssetPath(
            $"{rootPath}/35-normalized-input/{childPath}");
    }

    private async Task<GeneratedOutputProcessingResult> CommitRegionEditOutputAsync(
        GenerationTask task,
        GenerationDeliveryPlan delivery,
        GenerationRunWorkspacePlan workspace,
        ArtWorkspaceRegionEditContract contract,
        ValidatedAssetPath normalized,
        ValidatedAssetPath target,
        int outputIndex,
        CancellationToken cancellationToken)
    {
        if (task.Constraints.Output.BackgroundRemoval is not null)
        {
            throw new InvalidDataException("局部像素编辑不能与背景剔除首道工序同时启用。");
        }

        var baseImage = contextResolver.ResolveDeliveryAssetPath(
            contract.BaseImagePath,
            delivery.AssetRoot,
            requireExistingFile: true,
            "局部编辑 authoritative base");
        var mask = contextResolver.ResolveDeliveryAssetPath(
            contract.MaskPath,
            delivery.AssetRoot,
            requireExistingFile: true,
            "局部编辑 Alpha mask");
        var composite = contextResolver.ResolveDeliveryAssetPath(
            contract.CompositePath,
            delivery.AssetRoot,
            requireExistingFile: false,
            "局部编辑强制合成产物");
        var differenceReport = contextResolver.ResolveDeliveryAssetPath(
            contract.DifferenceReportPath,
            delivery.AssetRoot,
            requireExistingFile: false,
            "局部编辑选区外差异报告");
        var differencePreview = contextResolver.ResolveDeliveryAssetPath(
            contract.DifferencePreviewPath,
            delivery.AssetRoot,
            requireExistingFile: false,
            "局部编辑选区外差异预览");
        var processed = contextResolver.ResolveDeliveryAssetPath(
            workspace.ProcessedPaths[outputIndex],
            delivery.AssetRoot,
            requireExistingFile: false,
            "软件处理后局部编辑产物");

        var actualBaseSha256 = await ComputeSha256Async(baseImage.FullPath, cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(
                actualBaseSha256,
                contract.BaseImageSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "局部编辑 authoritative base 与冻结 SHA-256 不一致，已拒绝继续合成。");
        }


        var actualMaskSha256 = await ComputeSha256Async(mask.FullPath, cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(actualMaskSha256, contract.MaskSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "局部编辑 mask 与冻结 SHA-256 不一致，已拒绝继续合成。");
        }

        if (File.Exists(target.FullPath))
        {
            GeneratedAssetValidator.Validate(target.FullPath, task.Constraints);
            await EnsureOutsideMaskMatchesBaseAsync(
                    baseImage.FullPath,
                    mask.FullPath,
                    target.FullPath,
                    cancellationToken)
                .ConfigureAwait(false);
            return new GeneratedOutputProcessingResult(
                outputIndex,
                normalized.RelativePath,
                File.Exists(processed.FullPath) ? processed.RelativePath : null,
                target.RelativePath,
                Processed: false,
                AlreadyFinal: true,
                Skipped: true,
                ReplacedExistingTarget: false,
                StagingDeleted: false,
                "局部编辑最终目标已经存在，并已验证 mask 外逐像素等于 authoritative base。",
                null);
        }

        ArtWorkspaceRegionEditDiagnostics diagnostics;
        using (var measurement = GenerationRunJournal.Measure(
                   delivery,
                   task,
                   "art_workspace.region_edit.compose",
                   task.ClaimedBy,
                   outputIndex,
                   composite.RelativePath,
                   normalized.RelativePath))
        {
            try
            {
                diagnostics = await AnalyzeAndCompositeRegionEditAsync(
                        baseImage.FullPath,
                        mask.FullPath,
                        normalized.FullPath,
                        composite.FullPath,
                        differenceReport.FullPath,
                        differencePreview.FullPath,
                        contract.MaximumOutsideMaskChangedPercent,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!diagnostics.Passed)
                {
                    throw new ArtWorkspaceRegionEditValidationException(
                        $"局部编辑模型结果在 mask 外有 " +
                        $"{diagnostics.SignificantOutsideChangedPixels}/{diagnostics.PreservedPixelCount} " +
                        $"个显著变化像素（{diagnostics.SignificantOutsideChangedPercent:0.###}%），" +
                        $"超过允许的 {diagnostics.MaximumAllowedPercent:0.###}%；" +
                        "模型原图、归一化图、Mask 与差异报告均已保留，任务不得登记 Completed。",
                        diagnostics);
                }

                measurement.Complete(
                    $"局部编辑已强制合成；mask 外显著变化 " +
                    $"{diagnostics.SignificantOutsideChangedPixels}/{diagnostics.PreservedPixelCount} " +
                    $"({diagnostics.SignificantOutsideChangedPercent:0.###}%)，最终将逐像素恢复 base。",
                    composite.RelativePath);
            }
            catch (ArtWorkspaceRegionEditValidationException exception)
            {
                measurement.Fail(exception, ArtWorkspaceRegionEditValidationException.ErrorCode);
                throw;
            }
            catch (Exception exception)
            {
                measurement.Fail(exception);
                throw;
            }
        }

        GeneratedAssetValidator.Validate(composite.FullPath, task.Constraints);
        await CopyArtifactIfAbsentOrEqualAsync(
                composite.FullPath,
                processed.FullPath,
                cancellationToken)
            .ConfigureAwait(false);
        GeneratedAssetValidator.Validate(processed.FullPath, task.Constraints);
        await CopyArtifactIfAbsentOrEqualAsync(
                processed.FullPath,
                target.FullPath,
                cancellationToken)
            .ConfigureAwait(false);
        GeneratedAssetValidator.Validate(target.FullPath, task.Constraints);
        await EnsureOutsideMaskMatchesBaseAsync(
                baseImage.FullPath,
                mask.FullPath,
                target.FullPath,
                cancellationToken)
            .ConfigureAwait(false);
        return new GeneratedOutputProcessingResult(
            outputIndex,
            normalized.RelativePath,
            processed.RelativePath,
            target.RelativePath,
            Processed: true,
            AlreadyFinal: false,
            Skipped: false,
            ReplacedExistingTarget: false,
            StagingDeleted: false,
            "软件已按真实 Alpha mask 强制合成局部编辑结果；mask 外像素逐像素保留 authoritative base。",
            null);
    }

    private static async Task<ArtWorkspaceRegionEditDiagnostics> AnalyzeAndCompositeRegionEditAsync(
        string baseImagePath,
        string maskPath,
        string candidatePath,
        string compositePath,
        string differenceReportPath,
        string differencePreviewPath,
        double maximumOutsideMaskChangedPercent,
        CancellationToken cancellationToken)
    {
        const int significantChannelDelta = 32;
        var diagnostics = await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var baseline = Image.Load<Rgba32>(baseImagePath);
            using var mask = Image.Load<Rgba32>(maskPath);
            using var candidate = Image.Load<Rgba32>(candidatePath);
            if (mask.Width != baseline.Width || mask.Height != baseline.Height ||
                candidate.Width != baseline.Width || candidate.Height != baseline.Height)
            {
                throw new InvalidDataException(
                    "局部编辑 base、mask 与归一化模型图必须具有完全相同的像素尺寸。");
            }

            using var composite = baseline.Clone();
            using var differencePreview = new Image<Rgba32>(
                baseline.Width,
                baseline.Height,
                new Rgba32(0, 0, 0, 0));
            var editablePixels = 0;
            var preservedPixels = 0;
            var significantOutsideChanges = 0;
            var hasEditablePixels = false;
            var hasPreservedPixels = false;
            for (var y = 0; y < baseline.Height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (var x = 0; x < baseline.Width; x++)
                {
                    var alpha = mask[x, y].A;
                    if (alpha is not (byte.MinValue or byte.MaxValue))
                    {
                        throw new InvalidDataException(
                            $"局部编辑 mask 只能使用 0/255 Alpha；发现 ({x},{y})={alpha}。");
                    }

                    if (alpha == byte.MinValue)
                    {
                        editablePixels++;
                        hasEditablePixels = true;
                        composite[x, y] = candidate[x, y];
                        continue;
                    }

                    preservedPixels++;
                    hasPreservedPixels = true;
                    if (!HasSignificantPixelDifference(
                            baseline[x, y],
                            candidate[x, y],
                            significantChannelDelta))
                    {
                        continue;
                    }

                    significantOutsideChanges++;
                    differencePreview[x, y] = new Rgba32(255, 32, 32, 255);
                }
            }

            if (!hasEditablePixels || !hasPreservedPixels)
            {
                throw new InvalidDataException(
                    "局部编辑 mask 必须同时包含可编辑透明区和需保留的不透明区。");
            }

            var changedPercent = preservedPixels == 0
                ? 100d
                : significantOutsideChanges * 100d / preservedPixels;
            var passed = changedPercent <= maximumOutsideMaskChangedPercent;
            Directory.CreateDirectory(Path.GetDirectoryName(compositePath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(differencePreviewPath)!);
            SavePngAtomic(composite, compositePath);
            SavePngAtomic(differencePreview, differencePreviewPath);
            return new ArtWorkspaceRegionEditDiagnostics(
                baseImagePath,
                maskPath,
                candidatePath,
                compositePath,
                differenceReportPath,
                differencePreviewPath,
                editablePixels,
                preservedPixels,
                significantOutsideChanges,
                changedPercent,
                maximumOutsideMaskChangedPercent,
                significantChannelDelta,
                passed);
        }, cancellationToken).ConfigureAwait(false);

        await WriteRegionEditDiagnosticsAtomicAsync(
                diagnostics,
                differenceReportPath,
                cancellationToken)
            .ConfigureAwait(false);
        return diagnostics;
    }

    private static bool HasSignificantPixelDifference(
        Rgba32 expected,
        Rgba32 actual,
        int threshold) =>
        Math.Abs(expected.R - actual.R) > threshold ||
        Math.Abs(expected.G - actual.G) > threshold ||
        Math.Abs(expected.B - actual.B) > threshold ||
        Math.Abs(expected.A - actual.A) > threshold;

    private static async Task EnsureOutsideMaskMatchesBaseAsync(
        string baseImagePath,
        string maskPath,
        string outputPath,
        CancellationToken cancellationToken)
    {
        await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var baseline = Image.Load<Rgba32>(baseImagePath);
            using var mask = Image.Load<Rgba32>(maskPath);
            using var output = Image.Load<Rgba32>(outputPath);
            if (mask.Width != baseline.Width || mask.Height != baseline.Height ||
                output.Width != baseline.Width || output.Height != baseline.Height)
            {
                throw new InvalidDataException(
                    "局部编辑最终图、base 与 mask 的像素尺寸不一致。");
            }

            for (var y = 0; y < baseline.Height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (var x = 0; x < baseline.Width; x++)
                {
                    if (mask[x, y].A == byte.MaxValue && baseline[x, y] != output[x, y])
                    {
                        throw new InvalidDataException(
                            $"局部编辑最终产物在 mask 外发生像素变化：({x},{y})。");
                    }
                }
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteRegionEditDiagnosticsAtomicAsync(
        ArtWorkspaceRegionEditDiagnostics diagnostics,
        string path,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                        stream,
                        new
                        {
                            schemaVersion = 1,
                            operationMode = ArtWorkspaceRegionEditContract.RegionEditOperationMode,
                            observedAtUtc = DateTimeOffset.UtcNow,
                            diagnostics.BaseImagePath,
                            diagnostics.MaskPath,
                            diagnostics.CandidatePath,
                            diagnostics.CompositePath,
                            diagnostics.DifferencePreviewPath,
                            diagnostics.EditablePixelCount,
                            diagnostics.PreservedPixelCount,
                            diagnostics.SignificantOutsideChangedPixels,
                            diagnostics.SignificantOutsideChangedPercent,
                            diagnostics.MaximumAllowedPercent,
                            diagnostics.SignificantChannelDelta,
                            diagnostics.Passed
                        },
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            TryDeleteFile(temporaryPath);
        }
    }

    private static void SavePngAtomic(Image<Rgba32> image, string path)
    {
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp.png";
        try
        {
            image.SaveAsPng(temporaryPath);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            TryDeleteFile(temporaryPath);
        }
    }

    private async Task<GeneratedOutputProcessingResult> CommitDirectOutputAsync(
        GenerationTask task,
        GenerationDeliveryPlan delivery,
        GenerationRunWorkspacePlan workspace,
        ValidatedAssetPath normalized,
        ValidatedAssetPath target,
        int outputIndex,
        CancellationToken cancellationToken)
    {
        var processed = contextResolver.ResolveDeliveryAssetPath(
            workspace.ProcessedPaths[outputIndex],
            delivery.AssetRoot,
            requireExistingFile: false,
            "软件处理后产物");

        if (GeneratedAssetPostProcessor.IsValidFinalTarget(target.FullPath, task.Constraints))
        {
            return new GeneratedOutputProcessingResult(
                outputIndex,
                normalized.RelativePath,
                File.Exists(processed.FullPath) ? processed.RelativePath : null,
                target.RelativePath,
                Processed: false,
                AlreadyFinal: true,
                Skipped: true,
                ReplacedExistingTarget: false,
                StagingDeleted: false,
                "无背景剔除任务的最终目标已经存在并通过约束校验。",
                null);
        }

        GeneratedAssetValidator.Validate(normalized.FullPath, task.Constraints);
        await CopyArtifactIfAbsentOrEqualAsync(
                normalized.FullPath,
                processed.FullPath,
                cancellationToken)
            .ConfigureAwait(false);
        GeneratedAssetValidator.Validate(processed.FullPath, task.Constraints);
        await CopyArtifactIfAbsentOrEqualAsync(
                processed.FullPath,
                target.FullPath,
                cancellationToken)
            .ConfigureAwait(false);
        GeneratedAssetValidator.Validate(target.FullPath, task.Constraints);
        return new GeneratedOutputProcessingResult(
            outputIndex,
            normalized.RelativePath,
            processed.RelativePath,
            target.RelativePath,
            Processed: true,
            AlreadyFinal: false,
            Skipped: false,
            ReplacedExistingTarget: false,
            StagingDeleted: false,
            "软件已归一化无背景剔除产物、保留处理副本并提交最终目标。",
            null);
    }

    private static async Task CopyArtifactIfAbsentOrEqualAsync(
        string sourcePath,
        string targetPath,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        var sourceHash = await ComputeSha256Async(sourcePath, cancellationToken).ConfigureAwait(false);
        if (File.Exists(targetPath))
        {
            var existingHash = await ComputeSha256Async(targetPath, cancellationToken).ConfigureAwait(false);
            if (string.Equals(existingHash, sourceHash, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            throw new IOException($"运行工作区已经存在不同内容的产物，拒绝覆盖：{targetPath}");
        }

        var temporaryPath = $"{targetPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using var source = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using (var targetStream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await source.CopyToAsync(targetStream, cancellationToken).ConfigureAwait(false);
                await targetStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, targetPath);
        }
        catch (IOException) when (File.Exists(targetPath))
        {
            var existingHash = await ComputeSha256Async(targetPath, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(existingHash, sourceHash, StringComparison.OrdinalIgnoreCase))
            {
                throw;
            }
        }
        finally
        {
            TryDeleteFile(temporaryPath);
        }
    }

    private static async Task<string> ImportModelOutputAsync(
        string sourcePath,
        string targetPath,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        var sourceHash = await ComputeSha256Async(sourcePath, cancellationToken).ConfigureAwait(false);
        if (File.Exists(targetPath))
        {
            var existingHash = await ComputeSha256Async(targetPath, cancellationToken).ConfigureAwait(false);
            if (string.Equals(existingHash, sourceHash, StringComparison.OrdinalIgnoreCase))
            {
                return sourceHash;
            }

            throw new IOException("项目运行工作区已归档不同的模型原图；为保留审计链拒绝覆盖。");
        }

        var temporaryPath = $"{targetPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using var source = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using (var target = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
                await target.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, targetPath);
            return sourceHash;
        }
        finally
        {
            TryDeleteFile(temporaryPath);
        }
    }

    private static async Task<bool> NormalizeAsync(
        string sourcePath,
        string targetPath,
        GenerationOutputSpec output,
        bool preserveNormalizedCoordinates,
        CancellationToken cancellationToken)
    {
        var removal = output.BackgroundRemoval;
        var background = removal is null
            ? output.TransparentBackground
                ? new Rgba32(0, 0, 0, 0)
                : new Rgba32(0, 0, 0, byte.MaxValue)
            : ParseColor(removal.ColorHex);
        var temporaryPath = $"{targetPath}.{Guid.NewGuid():N}.tmp.png";
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        try
        {
            var result = await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var source = Image.Load<Rgba32>(sourcePath);
                var wasResized = source.Width != output.Width || source.Height != output.Height;
                if (preserveNormalizedCoordinates)
                {
                    if (wasResized)
                    {
                        throw new InvalidDataException(
                            $"局部编辑可提交候选必须已由 Harness 无插值恢复为 authoritative base " +
                            $"{output.Width}x{output.Height}，实际为 {source.Width}x{source.Height}。");
                    }
                }
                else
                {
                    source.Mutate(context => context.Resize(new ResizeOptions
                    {
                        Size = new Size(output.Width, output.Height),
                        Mode = ResizeMode.Max,
                        Sampler = KnownResamplers.Lanczos3,
                        Compand = true
                    }));
                }
                using var normalized = new Image<Rgba32>(output.Width, output.Height, background);
                var location = preserveNormalizedCoordinates
                    ? Point.Empty
                    : new Point(
                        (output.Width - source.Width) / 2,
                        (output.Height - source.Height) / 2);
                normalized.Mutate(context => context.DrawImage(source, location, 1f));
                if (removal is not null)
                {
                    ForceExactBorder(normalized, background, Math.Max(1, removal.EdgeContract));
                }

                normalized.SaveAsPng(temporaryPath);
                return wasResized;
            }, cancellationToken).ConfigureAwait(false);

            File.Move(temporaryPath, targetPath, overwrite: true);
            return result;
        }
        finally
        {
            TryDeleteFile(temporaryPath);
        }
    }

    private static void ForceExactBorder(Image<Rgba32> image, Rgba32 color, int thickness)
    {
        var safeThickness = Math.Min(thickness, Math.Min(image.Width, image.Height) / 2);
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < image.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                if (y < safeThickness || y >= image.Height - safeThickness)
                {
                    row.Fill(color);
                    continue;
                }

                for (var x = 0; x < safeThickness; x++)
                {
                    row[x] = color;
                    row[image.Width - 1 - x] = color;
                }
            }
        });
    }

    private static Rgba32 ParseColor(string colorHex)
    {
        var value = colorHex.AsSpan().Trim();
        if (value.Length != 7 || value[0] != '#')
        {
            throw new InvalidDataException("背景剔除颜色必须是 #RRGGBB。");
        }

        return new Rgba32(
            byte.Parse(value.Slice(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(value.Slice(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(value.Slice(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.MaxValue);
    }

    private static async Task<string> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            hash.AppendData(buffer.AsSpan(0, read));
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static bool IsWithinRoot(string root, string candidate)
    {
        var relative = Path.GetRelativePath(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)),
            Path.GetFullPath(candidate));
        return relative == "." ||
               (!Path.IsPathRooted(relative) &&
                relative != ".." &&
                !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
    }

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
        }
    }
}
