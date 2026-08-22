using GAssetForge.Core;
using GAssetForge.Core.Generation;
using System.IO;
using System.Text;
using System.Text.Json;

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
        "30-model-output",
        "35-normalized-input",
        "40-background-removed",
        "50-final",
        "90-diagnostics"
    ];

    public PreparedGenerationRunWorkspace Prepare(string batchId, string assetRoot)
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

        var rootRelativePath = $"{RunContainerRelativePath}/{batchId.ToLowerInvariant()}";
        var rootFullPath = ResolveAssetPath(normalizedAssetRoot, rootRelativePath);
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

            var prepared = new PreparedGenerationRunWorkspace(
                batchId.ToLowerInvariant(),
                normalizedAssetRoot,
                rootRelativePath,
                rootFullPath,
                $"{rootRelativePath}/00-manifest/run.v1.json",
                $"{rootRelativePath}/20-prompts",
                $"{rootRelativePath}/90-diagnostics/stages.v1.jsonl",
                Path.Combine(rootFullPath, "10-style-reference"));
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
