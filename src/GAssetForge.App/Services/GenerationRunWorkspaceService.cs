using GAssetForge.Core;
using GAssetForge.Core.Generation;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace GAssetForge.App.Services;

public sealed record PreparedGenerationRunWorkspace(
    string BatchId,
    string AssetRoot,
    string RootRelativePath,
    string RootFullPath,
    string ManifestRelativePath,
    string PromptDirectoryRelativePath,
    string DiagnosticsRelativePath,
    string ReferenceDirectoryFullPath);

public sealed record GenerationRunManifestItem(
    string ItemId,
    string TaskId,
    string Description,
    int RowIndex,
    int VariantIndex,
    int VariantCount,
    string ModelOutputPath,
    string NormalizedPath,
    string ProcessedPath,
    string TargetPath);

public sealed record GenerationRunManifest(
    int SchemaVersion,
    string RunId,
    string BatchId,
    string AssetRoot,
    string WorkflowId,
    DateTimeOffset CreatedAtUtc,
    string StyleGroupId,
    string StyleName,
    IReadOnlyList<GenerationRunManifestItem> Items);

/// <summary>
/// Owns all deterministic filesystem preparation for a generation batch. Agents never create or
/// classify these directories; they only return a model-produced file to the MCP submission tool.
/// </summary>
public sealed class GenerationRunWorkspaceService
{
    public const string RunContainerRelativePath = ProductIdentity.GenerationRunContainerRelativePath;

    private static readonly string[] RequiredDirectories =
    [
        "00-manifest",
        "10-style-reference",
        "20-prompts",
        "25-region-edit",
        "30-model-output",
        "35-normalized-input",
        "40-background-removed",
        "50-final",
        "90-diagnostics"
    ];

    public PreparedGenerationRunWorkspace Prepare(string batchId, string assetRoot)
    {
        var prepared = CreateWorkspaceDescriptor(batchId, assetRoot);
        var normalizedAssetRoot = prepared.AssetRoot;
        var rootFullPath = prepared.RootFullPath;
        if (Directory.Exists(rootFullPath) || File.Exists(rootFullPath))
        {
            throw new IOException($"生成运行工作区已存在：{rootFullPath}");
        }

        Directory.CreateDirectory(rootFullPath);
        try
        {
            foreach (var directory in RequiredDirectories)
            {
                Directory.CreateDirectory(Path.Combine(rootFullPath, directory));
            }

            GenerationRunJournal.Append(
                CreateJournalDelivery(prepared),
                new GenerationRunStageEvent
                {
                    BatchId = prepared.BatchId,
                    Stage = "workspace.prepare",
                    Status = "completed",
                    EndedAtUtc = DateTimeOffset.UtcNow,
                    Message = $"软件已创建 {RequiredDirectories.Length} 个分类目录。"
                });
            return prepared;
        }
        catch
        {
            TryDeleteOwnedRun(preparedRoot: rootFullPath, normalizedAssetRoot);
            throw;
        }
    }

    public PreparedGenerationRunWorkspace OpenOrRepair(string batchId, string assetRoot)
    {
        var prepared = CreateWorkspaceDescriptor(batchId, assetRoot);
        if (File.Exists(prepared.RootFullPath))
        {
            throw new IOException($"生成运行工作区路径被文件占用：{prepared.RootFullPath}");
        }

        Directory.CreateDirectory(prepared.RootFullPath);
        if ((File.GetAttributes(prepared.RootFullPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException($"生成运行工作区不能是符号链接或目录联接：{prepared.RootFullPath}");
        }

        foreach (var directory in RequiredDirectories)
        {
            var path = Path.Combine(prepared.RootFullPath, directory);
            if (File.Exists(path))
            {
                throw new IOException($"生成运行分类目录被文件占用：{path}");
            }

            Directory.CreateDirectory(path);
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException($"生成运行分类目录不能是符号链接或目录联接：{path}");
            }
        }

        return prepared;
    }

    public GenerationRunWorkspacePlan CreateOutputPlan(
        PreparedGenerationRunWorkspace run,
        string itemId,
        string suggestedBaseName)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (!Guid.TryParseExact(itemId, "N", out _))
        {
            throw new ArgumentException("生成输出项目 ID 必须是 32 位 GUID。", nameof(itemId));
        }

        var safeBaseName = NormalizeFileName(suggestedBaseName);
        var promptPath = $"{run.PromptDirectoryRelativePath}/{itemId}.txt";
        var modelOutputPath =
            $"{run.RootRelativePath}/30-model-output/{itemId}/{safeBaseName}.model.png";
        var processedPath =
            $"{run.RootRelativePath}/40-background-removed/{itemId}/{safeBaseName}.transparent.png";
        EnsureParentDirectory(run.AssetRoot, promptPath);
        EnsureParentDirectory(run.AssetRoot, modelOutputPath);
        EnsureParentDirectory(run.AssetRoot, processedPath);
        return new GenerationRunWorkspacePlan
        {
            RunId = run.BatchId,
            RootPath = run.RootRelativePath,
            ManifestPath = run.ManifestRelativePath,
            PromptPath = promptPath,
            DiagnosticsPath = run.DiagnosticsRelativePath,
            ModelOutputPaths = [modelOutputPath],
            ProcessedPaths = [processedPath],
            PreserveIntermediates = true
        };
    }

    public string CreateNormalizedPath(
        PreparedGenerationRunWorkspace run,
        string itemId,
        string suggestedBaseName)
    {
        var relativePath =
            $"{run.RootRelativePath}/35-normalized-input/{itemId}/{NormalizeFileName(suggestedBaseName)}.normalized.png";
        EnsureParentDirectory(run.AssetRoot, relativePath);
        return relativePath;
    }

    /// <summary>
    /// Materializes the immutable pixel inputs for a single Art Workspace region edit. The
    /// authoritative base is copied from the already-frozen data-root snapshot; Agents never
    /// choose a current/history image or manufacture the mask themselves.
    /// </summary>
    public async Task<ArtWorkspaceRegionEditContract> PrepareArtWorkspaceRegionEditAsync(
        PreparedGenerationRunWorkspace run,
        string itemId,
        ArtWorkspaceRegionEditTarget target,
        string frozenBaseSourcePath,
        double? maximumOutsideMaskChangedPercent = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(target);
        if (!Guid.TryParseExact(itemId, "N", out _))
        {
            throw new ArgumentException("局部编辑项目 ID 必须是 32 位 GUID。", nameof(itemId));
        }

        if (string.IsNullOrWhiteSpace(frozenBaseSourcePath) ||
            !Path.IsPathFullyQualified(frozenBaseSourcePath))
        {
            throw new ArgumentException("局部编辑 authoritative base 必须是冻结的绝对路径。", nameof(frozenBaseSourcePath));
        }

        var sourcePath = Path.GetFullPath(frozenBaseSourcePath);
        var sourceInfo = new FileInfo(sourcePath);
        if (!sourceInfo.Exists || (sourceInfo.Attributes & FileAttributes.Directory) != 0)
        {
            throw new FileNotFoundException("局部编辑 authoritative base 快照不存在。", sourcePath);
        }

        if ((sourceInfo.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("局部编辑 authoritative base 不能是符号链接或重解析点。");
        }

        // Exact byte identity is important to downstream submission diagnostics. Art Workspace
        // generated previews are PNGs; fail closed for a differently encoded imported source until
        // the contract carries separate source-snapshot and transcoded-base hashes.
        if (!string.Equals(sourceInfo.Extension, ".png", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException(
                "局部编辑当前只接受 PNG authoritative base，以确保冻结 SHA-256 与项目内基准副本完全一致。");
        }

        var actualSha256 = await ComputeSha256Async(sourcePath, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(actualSha256, target.BaseImageSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "局部编辑 authoritative base 内容与冻结 SHA-256 不一致；已拒绝使用可能变化的历史图片。");
        }

        using var baseImage = await Image.LoadAsync<Rgba32>(sourcePath, cancellationToken)
            .ConfigureAwait(false);
        if (baseImage.Width != target.BasePixelWidth || baseImage.Height != target.BasePixelHeight)
        {
            throw new InvalidDataException(
                $"局部编辑 authoritative base 像素尺寸与冻结合同不一致：" +
                $"合同 {target.BasePixelWidth}×{target.BasePixelHeight}，文件 {baseImage.Width}×{baseImage.Height}。");
        }

        ValidateRegionBounds(target.SourcePixelBounds, baseImage.Width, baseImage.Height);

        var directory = $"{run.RootRelativePath}/25-region-edit/{itemId}";
        var baseImagePath = $"{directory}/authoritative-base.png";
        var maskPath = $"{directory}/api-mask.png";
        var previewPath = $"{directory}/region-preview.png";
        var compositePath = $"{directory}/composite.png";
        var differenceReportPath = $"{directory}/outside-mask-difference.json";
        var differencePreviewPath = $"{directory}/outside-mask-difference.png";
        var baseFullPath = ResolveAssetPath(run.AssetRoot, baseImagePath);
        var maskFullPath = ResolveAssetPath(run.AssetRoot, maskPath);
        var previewFullPath = ResolveAssetPath(run.AssetRoot, previewPath);
        Directory.CreateDirectory(Path.GetDirectoryName(baseFullPath)!);

        await CopyFileAtomicAsync(sourcePath, baseFullPath, cancellationToken).ConfigureAwait(false);
        await WriteRegionMaskAsync(
                maskFullPath,
                baseImage.Width,
                baseImage.Height,
                target.SourcePixelBounds,
                cancellationToken)
            .ConfigureAwait(false);
        await WriteRegionPreviewAsync(
                baseImage,
                previewFullPath,
                target.SourcePixelBounds,
                cancellationToken)
            .ConfigureAwait(false);

        var copiedSha256 = await ComputeSha256Async(baseFullPath, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(copiedSha256, target.BaseImageSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("项目内 authoritative base 副本未保持冻结 SHA-256。");
        }

        var maskSha256 = await ComputeSha256Async(maskFullPath, cancellationToken).ConfigureAwait(false);

        var allowedOutsideChange = maximumOutsideMaskChangedPercent ??
            new ArtWorkspaceRegionEditContract().MaximumOutsideMaskChangedPercent;
        if (!double.IsFinite(allowedOutsideChange) || allowedOutsideChange is <= 0d or > 100d)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumOutsideMaskChangedPercent),
                "局部编辑选区外变化阈值必须大于 0 且不超过 100。");
        }

        var contract = new ArtWorkspaceRegionEditContract
        {
            BaseImageId = target.BaseImageId,
            BaseImageSha256 = target.BaseImageSha256,
            BaseImagePath = baseImagePath,
            MaskPath = maskPath,
            MaskSha256 = maskSha256,
            RegionPreviewPath = previewPath,
            CompositePath = compositePath,
            DifferenceReportPath = differenceReportPath,
            DifferencePreviewPath = differencePreviewPath,
            MaximumOutsideMaskChangedPercent = allowedOutsideChange,
            SourcePixelBounds = target.SourcePixelBounds with { }
        };
        await WriteJsonAtomicAsync(
                ResolveAssetPath(run.AssetRoot, $"{directory}/contract.v1.json"),
                contract,
                cancellationToken)
            .ConfigureAwait(false);
        GenerationRunJournal.Append(
            CreateJournalDelivery(run),
            new GenerationRunStageEvent
            {
                BatchId = run.BatchId,
                Stage = "region_edit.prepare",
                Status = "completed",
                EndedAtUtc = DateTimeOffset.UtcNow,
                ObservedPath = maskPath,
                Message =
                    $"软件已冻结唯一 authoritative base，并生成 {baseImage.Width}×{baseImage.Height} API mask；" +
                    $"允许编辑区域 x={target.SourcePixelBounds.X}, y={target.SourcePixelBounds.Y}, " +
                    $"width={target.SourcePixelBounds.Width}, height={target.SourcePixelBounds.Height}。"
            });
        return contract;
    }

    /// <summary>
    /// Creates attempt-local region-edit artifacts for an automatic retry. The immutable pixels and
    /// selection are inherited from the parent contract, but every writable/model-cache path moves
    /// into the new Run so a failed API raw cannot be mistaken for a successful retry input.
    /// </summary>
    public async Task<GenerationConstraintPackage> PrepareArtWorkspaceRegionEditRetryAsync(
        PreparedGenerationRunWorkspace run,
        string itemId,
        GenerationConstraintPackage retryConstraints,
        GenerationDeliveryPlan previousDelivery,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(retryConstraints);
        ArgumentNullException.ThrowIfNull(previousDelivery);
        if (!ArtWorkspaceRegionEditContractComposer.HasDeclaredContract(retryConstraints))
        {
            return retryConstraints;
        }

        if (!ArtWorkspaceRegionEditContractComposer.TryRead(retryConstraints, out var parentContract) ||
            parentContract is null)
        {
            throw new InvalidDataException(
                "自动重做检测到损坏的 region-edit 合同，已拒绝退化为整图生成。");
        }

        if (!AssetRootService.PathEquals(run.AssetRoot, previousDelivery.AssetRoot))
        {
            throw new InvalidDataException(
                "region-edit 自动重做的新 Run 与父任务不属于同一项目 Assets 根目录。");
        }

        var parentBasePath = ResolveAssetPath(previousDelivery.AssetRoot, parentContract.BaseImagePath);
        var imageInfo = Image.Identify(parentBasePath)
            ?? throw new InvalidDataException("无法识别 region-edit authoritative base 的像素尺寸。");
        var reboundContract = await PrepareArtWorkspaceRegionEditAsync(
                run,
                itemId,
                new ArtWorkspaceRegionEditTarget
                {
                    BaseImageId = parentContract.BaseImageId,
                    BaseImageSha256 = parentContract.BaseImageSha256,
                    BaseImageDataRootRelativePath = parentContract.BaseImagePath,
                    BasePixelWidth = imageInfo.Width,
                    BasePixelHeight = imageInfo.Height,
                    SourcePixelBounds = parentContract.SourcePixelBounds with { }
                },
                parentBasePath,
                parentContract.MaximumOutsideMaskChangedPercent,
                cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(
                reboundContract.MaskSha256,
                parentContract.MaskSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "region-edit 自动重做生成的 mask 与父任务冻结 mask 不一致，已停止重做。");
        }

        return ArtWorkspaceRegionEditContractComposer.RebindAttemptArtifacts(
            retryConstraints,
            reboundContract);
    }

    public async Task MirrorStyleReferencesAsync(
        PreparedGenerationRunWorkspace run,
        GenerationReferenceSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(snapshot);
        if (string.IsNullOrWhiteSpace(snapshot.SnapshotDirectory))
        {
            await WriteJsonAtomicAsync(
                    Path.Combine(run.ReferenceDirectoryFullPath, "references.v1.json"),
                    Array.Empty<object>(),
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var sourceRoot = Path.GetFullPath(snapshot.SnapshotDirectory);
        if (!Directory.Exists(sourceRoot))
        {
            throw new DirectoryNotFoundException($"冻结风格参考目录不存在：{sourceRoot}");
        }

        foreach (var sourcePath in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceInfo = new FileInfo(sourcePath);
            if ((sourceInfo.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException($"风格参考快照不能是重解析点：{sourcePath}");
            }

            var targetPath = Path.Combine(run.ReferenceDirectoryFullPath, sourceInfo.Name);
            await using var source = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var target = new FileStream(
                targetPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
            await target.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        await WriteJsonAtomicAsync(
                Path.Combine(run.ReferenceDirectoryFullPath, "references.v1.json"),
                snapshot.References,
                cancellationToken)
            .ConfigureAwait(false);
        GenerationRunJournal.Append(
            CreateJournalDelivery(run),
            new GenerationRunStageEvent
            {
                BatchId = run.BatchId,
                Stage = "style.snapshot.project_copy",
                Status = "completed",
                EndedAtUtc = DateTimeOffset.UtcNow,
                Message = $"已复制 {snapshot.References.Count} 张冻结风格参考到项目运行工作区。"
            });
    }

    public async Task MirrorExistingRunReferencesAsync(
        PreparedGenerationRunWorkspace run,
        GenerationDeliveryPlan previousDelivery,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(previousDelivery);
        if (previousDelivery.Workspace is null)
        {
            return;
        }

        var sourceRelativePath = $"{previousDelivery.Workspace.RootPath}/10-style-reference";
        var sourceRoot = ResolveAssetPath(previousDelivery.AssetRoot, sourceRelativePath);
        if (!Directory.Exists(sourceRoot))
        {
            return;
        }

        foreach (var sourcePath in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(sourcePath);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException($"重试参考文件不能是重解析点：{sourcePath}");
            }

            var targetPath = Path.Combine(run.ReferenceDirectoryFullPath, info.Name);
            await using var source = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var target = new FileStream(
                targetPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task WritePromptAsync(
        PreparedGenerationRunWorkspace run,
        GenerationRunWorkspacePlan workspace,
        GenerationConstraintPackage constraints,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(constraints);
        var fullPath = ResolveAssetPath(run.AssetRoot, workspace.PromptPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var builder = new StringBuilder()
            .AppendLine($"workflow: {constraints.WorkflowId}")
            .AppendLine($"preferredSkill: {constraints.PreferredSkill}")
            .AppendLine($"canvas: {constraints.Output.Width}x{constraints.Output.Height}")
            .AppendLine($"target: {constraints.Parameters["delivery"]?["targetPaths"]?[0]}")
            .AppendLine()
            .AppendLine(constraints.Prompt);
        if (!string.IsNullOrWhiteSpace(constraints.NegativePrompt))
        {
            builder.AppendLine().AppendLine("negative:").AppendLine(constraints.NegativePrompt);
        }

        await WriteTextAtomicAsync(fullPath, builder.ToString(), cancellationToken).ConfigureAwait(false);
    }

    public Task WriteManifestAsync(
        PreparedGenerationRunWorkspace run,
        GenerationRunManifest manifest,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(manifest);
        var fullPath = ResolveAssetPath(run.AssetRoot, run.ManifestRelativePath);
        return WriteJsonAtomicAsync(fullPath, manifest, cancellationToken);
    }

    public void DeleteIfUnpublished(PreparedGenerationRunWorkspace run)
    {
        ArgumentNullException.ThrowIfNull(run);
        TryDeleteOwnedRun(run.RootFullPath, run.AssetRoot);
    }

    private static GenerationDeliveryPlan CreateJournalDelivery(PreparedGenerationRunWorkspace run) =>
        new()
        {
            AssetRoot = run.AssetRoot,
            Workspace = new GenerationRunWorkspacePlan
            {
                RunId = run.BatchId,
                RootPath = run.RootRelativePath,
                ManifestPath = run.ManifestRelativePath,
                PromptPath = $"{run.PromptDirectoryRelativePath}/batch.txt",
                DiagnosticsPath = run.DiagnosticsRelativePath,
                ModelOutputPaths = [ $"{run.RootRelativePath}/30-model-output/.journal.png" ],
                ProcessedPaths = [ $"{run.RootRelativePath}/40-background-removed/.journal.png" ]
            }
        };

    private static PreparedGenerationRunWorkspace CreateWorkspaceDescriptor(
        string batchId,
        string assetRoot)
    {
        if (!Guid.TryParseExact(batchId, "N", out _))
        {
            throw new ArgumentException("生成批次 ID 必须是 32 位 GUID。", nameof(batchId));
        }

        if (string.IsNullOrWhiteSpace(assetRoot) || !Path.IsPathFullyQualified(assetRoot))
        {
            throw new ArgumentException("生成运行工作区必须绑定绝对 Assets 路径。", nameof(assetRoot));
        }

        var normalizedAssetRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(assetRoot));
        if (!Directory.Exists(normalizedAssetRoot))
        {
            throw new DirectoryNotFoundException($"当前项目 Assets 目录不存在：{normalizedAssetRoot}");
        }

        var normalizedBatchId = batchId.ToLowerInvariant();
        var rootRelativePath = $"{RunContainerRelativePath}/{normalizedBatchId}";
        var rootFullPath = ResolveAssetPath(normalizedAssetRoot, rootRelativePath);
        return new PreparedGenerationRunWorkspace(
            normalizedBatchId,
            normalizedAssetRoot,
            rootRelativePath,
            rootFullPath,
            $"{rootRelativePath}/00-manifest/run.v1.json",
            $"{rootRelativePath}/20-prompts",
            $"{rootRelativePath}/90-diagnostics/stages.v1.jsonl",
            Path.Combine(rootFullPath, "10-style-reference"));
    }

    private static string ResolveAssetPath(string assetRoot, string relativePath)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(assetRoot));
        var normalized = GenerationInputValidator.NormalizeRelativeAssetPath(relativePath);
        var fullPath = Path.GetFullPath(Path.Combine(
            root,
            normalized.Replace('/', Path.DirectorySeparatorChar)));
        if (!AssetRootService.IsWithinRoot(root, fullPath) || AssetRootService.PathEquals(root, fullPath))
        {
            throw new InvalidDataException($"生成运行路径越出了 Assets 根目录：{relativePath}");
        }

        return fullPath;
    }

    private static void EnsureParentDirectory(string assetRoot, string relativePath)
    {
        var fullPath = ResolveAssetPath(assetRoot, relativePath);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidDataException($"无法解析运行工作区目录：{relativePath}");
        Directory.CreateDirectory(directory);
    }

    private static string NormalizeFileName(string value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length is < 1 or > 160 ||
            normalized is "." or ".." ||
            normalized.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            normalized.Any(char.IsControl))
        {
            throw new ArgumentException("运行工作区文件名无效。", nameof(value));
        }

        return normalized;
    }

    private static void ValidateRegionBounds(
        ArtWorkspaceRegionEditPixelBounds bounds,
        int imageWidth,
        int imageHeight)
    {
        ArgumentNullException.ThrowIfNull(bounds);
        if (bounds.X < 0 || bounds.Y < 0 || bounds.Width <= 0 || bounds.Height <= 0 ||
            bounds.X > imageWidth - bounds.Width || bounds.Y > imageHeight - bounds.Height)
        {
            throw new InvalidDataException(
                $"局部编辑 sourcePixelBounds 越出 authoritative base：" +
                $"x={bounds.X}, y={bounds.Y}, width={bounds.Width}, height={bounds.Height}, " +
                $"base={imageWidth}×{imageHeight}。");
        }

        if (bounds.Width == imageWidth && bounds.Height == imageHeight)
        {
            throw new InvalidDataException(
                "局部编辑区域不能覆盖整张 authoritative base；整图指令应使用普通图片引用，" +
                "局部编辑 mask 必须保留至少一个选区外像素。");
        }
    }

    private static async Task WriteRegionMaskAsync(
        string targetPath,
        int width,
        int height,
        ArtWorkspaceRegionEditPixelBounds bounds,
        CancellationToken cancellationToken)
    {
        using var mask = new Image<Rgba32>(width, height, new Rgba32(255, 255, 255, 255));
        mask.ProcessPixelRows(accessor =>
        {
            for (var y = bounds.Y; y < bounds.Y + bounds.Height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var row = accessor.GetRowSpan(y);
                for (var x = bounds.X; x < bounds.X + bounds.Width; x++)
                {
                    row[x] = new Rgba32(255, 255, 255, 0);
                }
            }
        });
        await SavePngAtomicAsync(mask, targetPath, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteRegionPreviewAsync(
        Image<Rgba32> baseImage,
        string targetPath,
        ArtWorkspaceRegionEditPixelBounds bounds,
        CancellationToken cancellationToken)
    {
        using var preview = baseImage.Clone();
        preview.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < preview.Height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < preview.Width; x++)
                {
                    var inside = x >= bounds.X && x < bounds.X + bounds.Width &&
                                 y >= bounds.Y && y < bounds.Y + bounds.Height;
                    if (inside)
                    {
                        continue;
                    }

                    var pixel = row[x];
                    row[x] = new Rgba32(
                        (byte)(pixel.R / 4),
                        (byte)(pixel.G / 4),
                        (byte)(pixel.B / 4),
                        pixel.A);
                }
            }
        });
        await SavePngAtomicAsync(preview, targetPath, cancellationToken).ConfigureAwait(false);
    }

    private static async Task SavePngAtomicAsync(
        Image<Rgba32> image,
        string targetPath,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        var temporaryPath = $"{targetPath}.{Guid.NewGuid():N}.tmp.png";
        try
        {
            await image.SaveAsync(
                    temporaryPath,
                    new PngEncoder { ColorType = PngColorType.RgbWithAlpha },
                    cancellationToken)
                .ConfigureAwait(false);
            File.Move(temporaryPath, targetPath);
        }
        finally
        {
            TryDeleteFile(temporaryPath);
        }
    }

    private static async Task CopyFileAtomicAsync(
        string sourcePath,
        string targetPath,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
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
        }
        finally
        {
            TryDeleteFile(temporaryPath);
        }
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
        var digest = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    private static async Task WriteJsonAtomicAsync<T>(
        string targetPath,
        T value,
        CancellationToken cancellationToken)
    {
        var temporaryPath = $"{targetPath}.{Guid.NewGuid():N}.tmp";
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
                        value,
                        GenerationJson.Options,
                        cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, targetPath, overwrite: true);
        }
        finally
        {
            TryDeleteFile(temporaryPath);
        }
    }

    private static async Task WriteTextAtomicAsync(
        string targetPath,
        string content,
        CancellationToken cancellationToken)
    {
        var temporaryPath = $"{targetPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(
                    temporaryPath,
                    content,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    cancellationToken)
                .ConfigureAwait(false);
            File.Move(temporaryPath, targetPath, overwrite: true);
        }
        finally
        {
            TryDeleteFile(temporaryPath);
        }
    }

    private static void TryDeleteOwnedRun(string preparedRoot, string normalizedAssetRoot)
    {
        try
        {
            var fullPath = Path.GetFullPath(preparedRoot);
            var expectedContainer = ResolveAssetPath(normalizedAssetRoot, RunContainerRelativePath);
            if (!Directory.Exists(fullPath) ||
                !AssetRootService.IsWithinRoot(expectedContainer, fullPath) ||
                AssetRootService.PathEquals(expectedContainer, fullPath))
            {
                return;
            }

            var attributes = File.GetAttributes(fullPath);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                return;
            }

            Directory.Delete(fullPath, recursive: true);
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or InvalidDataException or
            NotSupportedException or System.Security.SecurityException)
        {
            // Failed, unpublished workspaces are retained if safe cleanup cannot be proven.
        }
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
