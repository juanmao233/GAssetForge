using GAssetForge.Core.Generation;
using GAssetForge.Core.Projects;
using GAssetForge.McpServer;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GAssetForge.SmokeTests;

internal static class GenerationClaimSafetySmokeTests
{
    public static async Task RunAllAsync(string temporaryRoot)
    {
        await InvalidRecommendedSourcePathDoesNotClaimTaskAsync(temporaryRoot);
        await LegacyTaskWithMissingStyleSnapshotCanStillBeClaimedAsync(temporaryRoot);
        await TamperedStyleReferenceSnapshotDoesNotClaimTaskAsync(temporaryRoot);
        await StyleReferenceReparsePointAncestorDoesNotClaimTaskAsync(temporaryRoot);
    }

    private static async Task InvalidRecommendedSourcePathDoesNotClaimTaskAsync(string temporaryRoot)
    {
        var root = Path.Combine(
            temporaryRoot,
            "generation-claim-safety",
            Guid.NewGuid().ToString("N"));
        var dataRoot = Path.Combine(root, "data");
        var assetRoot = Path.Combine(root, "Project", "Assets");
        Directory.CreateDirectory(assetRoot);

        var options = ServerOptions.Parse(["--data-root", dataRoot]);
        var store = new JsonGenerationTaskStore(options.GenerationStorageRoot);
        var task = await store.CreateAsync(new CreateGenerationTaskRequest
        {
            DisplayName = "claim path validation",
            RequestedBy = "smoke-tests",
            Constraints = CreateConstraints(assetRoot)
        });
        Directory.Delete(assetRoot, recursive: true);
        File.WriteAllText(assetRoot, "asset-root-is-now-a-file");

        var provider = new GenerationToolProvider(
            store,
            new WorkspaceContextResolver(options),
            options,
            store.StoreFilePath);
        var result = await provider.CreateCatalog().CallAsync(
            "claim_generation_task",
            JsonSerializer.SerializeToElement(new
            {
                taskId = task.Id,
                agentId = "claim-safety-agent"
            }),
            CancellationToken.None);
        var resultJson = JsonSerializer.SerializeToElement(result, ProtocolJson.Options);
        True(resultJson.GetProperty("isError").GetBoolean(),
            "推荐模型路径失效时 claim 必须返回可诊断错误");

        var persisted = await store.GetAsync(task.Id)
            ?? throw new InvalidOperationException("路径校验失败后任务不应丢失。");
        Equal(GenerationTaskStatus.Pending, persisted.Status,
            "recommendedSourcePath 校验必须在持久化 Claimed 之前完成");
        True(string.IsNullOrWhiteSpace(persisted.ClaimedBy),
            "路径校验失败不得留下认领所有者");
    }

    private static async Task LegacyTaskWithMissingStyleSnapshotCanStillBeClaimedAsync(
        string temporaryRoot)
    {
        var root = Path.Combine(
            temporaryRoot,
            "generation-legacy-claim-safety",
            Guid.NewGuid().ToString("N"));
        var dataRoot = Path.Combine(root, "data");
        var assetRoot = Path.Combine(root, "Project", "Assets");
        Directory.CreateDirectory(assetRoot);

        var options = ServerOptions.Parse(["--data-root", dataRoot]);
        var store = new JsonGenerationTaskStore(options.GenerationStorageRoot);
        var task = await store.CreateAsync(new CreateGenerationTaskRequest
        {
            DisplayName = "legacy claim with stale style reference",
            RequestedBy = "smoke-tests",
            Constraints = CreateConstraints(
                assetRoot,
                includeWorkspace: false,
                styleReferences:
                [
                    new MaterialGenerationStyleReference
                    {
                        Id = "missing-legacy-reference",
                        Name = "Missing legacy reference",
                        Sha256 = new string('A', 64),
                        MediaType = "image/png",
                        DataRootRelativePath = "StyleSnapshots/missing-legacy-reference.png"
                    }
                ])
        });

        var provider = new GenerationToolProvider(
            store,
            new WorkspaceContextResolver(options),
            options,
            store.StoreFilePath);
        var result = await provider.CreateCatalog().CallAsync(
            "claim_generation_task",
            JsonSerializer.SerializeToElement(new
            {
                taskId = task.Id,
                agentId = "legacy-claim-agent"
            }),
            CancellationToken.None);
        var resultJson = JsonSerializer.SerializeToElement(result, ProtocolJson.Options);
        False(resultJson.GetProperty("isError").GetBoolean(),
            "无 workspace 的旧任务不应因历史风格快照失效而无法认领");
        var structuredContent = resultJson.GetProperty("structuredContent");
        True(structuredContent.GetProperty("claimed").GetBoolean(),
            "无 workspace 的旧任务应继续使用旧认领合同");
        Equal(JsonValueKind.Null, structuredContent.GetProperty("imageGeneration").ValueKind,
            "无 workspace 的旧任务必须返回 imageGeneration=null");
        Equal(JsonValueKind.Null, structuredContent.GetProperty("recommendedSourcePath").ValueKind,
            "无 workspace 的旧任务不应返回新推荐源路径");

        var persisted = await store.GetAsync(task.Id)
            ?? throw new InvalidOperationException("旧任务认领后不应丢失。");
        Equal(GenerationTaskStatus.Claimed, persisted.Status,
            "旧任务应被正常认领");
        Equal("legacy-claim-agent", persisted.ClaimedBy,
            "旧任务应记录实际认领者");
    }

    private static async Task StyleReferenceReparsePointAncestorDoesNotClaimTaskAsync(
        string temporaryRoot)
    {
        var root = Path.Combine(
            temporaryRoot,
            "generation-style-reference-reparse-safety",
            Guid.NewGuid().ToString("N"));
        var dataRoot = Path.Combine(root, "data");
        var assetRoot = Path.Combine(root, "Project", "Assets");
        var styleRoot = Path.Combine(dataRoot, "StyleSnapshots");
        var outsideRoot = Path.Combine(root, "outside-style-snapshots");
        var linkedStyleRoot = Path.Combine(styleRoot, "linked");
        Directory.CreateDirectory(assetRoot);
        Directory.CreateDirectory(styleRoot);
        Directory.CreateDirectory(outsideRoot);
        File.WriteAllBytes(Path.Combine(outsideRoot, "reference.png"), [0x89, 0x50, 0x4E, 0x47]);
        try
        {
            try
            {
                Directory.CreateSymbolicLink(linkedStyleRoot, outsideRoot);
            }
            catch (Exception exception) when (exception is
                       UnauthorizedAccessException or
                       PlatformNotSupportedException)
            {
                return;
            }

            var options = ServerOptions.Parse(["--data-root", dataRoot]);
            var store = new JsonGenerationTaskStore(options.GenerationStorageRoot);
            var task = await store.CreateAsync(new CreateGenerationTaskRequest
            {
                DisplayName = "style reference reparse claim safety",
                RequestedBy = "smoke-tests",
                Constraints = CreateConstraints(
                    assetRoot,
                    includeWorkspace: true,
                    styleReferences:
                    [
                        new MaterialGenerationStyleReference
                        {
                            Id = "linked-style-reference",
                            Name = "Linked style reference",
                            Sha256 = new string('B', 64),
                            MediaType = "image/png",
                            DataRootRelativePath = "StyleSnapshots/linked/reference.png"
                        }
                    ])
            });
            var provider = new GenerationToolProvider(
                store,
                new WorkspaceContextResolver(options),
                options,
                store.StoreFilePath);
            var result = await provider.CreateCatalog().CallAsync(
                "claim_generation_task",
                JsonSerializer.SerializeToElement(new
                {
                    taskId = task.Id,
                    agentId = "reparse-claim-agent"
                }),
                CancellationToken.None);
            var resultJson = JsonSerializer.SerializeToElement(result, ProtocolJson.Options);
            True(resultJson.GetProperty("isError").GetBoolean(),
                "风格引用路径经过 dataRoot 内符号链接或目录联接时必须拒绝认领");

            var persisted = await store.GetAsync(task.Id)
                ?? throw new InvalidOperationException("重解析点校验失败后任务不应丢失。");
            Equal(GenerationTaskStatus.Pending, persisted.Status,
                "风格引用重解析点校验必须在持久化 Claimed 之前完成");
            True(string.IsNullOrWhiteSpace(persisted.ClaimedBy),
                "风格引用重解析点校验失败不得留下认领所有者");
        }
        finally
        {
            if (Directory.Exists(linkedStyleRoot))
            {
                Directory.Delete(linkedStyleRoot);
            }
        }
    }

    private static async Task TamperedStyleReferenceSnapshotDoesNotClaimTaskAsync(
        string temporaryRoot)
    {
        var root = Path.Combine(
            temporaryRoot,
            "generation-style-reference-hash-safety",
            Guid.NewGuid().ToString("N"));
        var dataRoot = Path.Combine(root, "data");
        var assetRoot = Path.Combine(root, "Project", "Assets");
        var snapshotRelativePath = "StyleSnapshots/reference.png";
        var snapshotFullPath = Path.Combine(dataRoot, "StyleSnapshots", "reference.png");
        Directory.CreateDirectory(assetRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(snapshotFullPath)!);

        var frozenSnapshot = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x01 };
        await File.WriteAllBytesAsync(snapshotFullPath, frozenSnapshot);
        var frozenSha256 = Convert.ToHexString(SHA256.HashData(frozenSnapshot)).ToLowerInvariant();

        var options = ServerOptions.Parse(["--data-root", dataRoot]);
        var store = new JsonGenerationTaskStore(options.GenerationStorageRoot);
        var task = await store.CreateAsync(new CreateGenerationTaskRequest
        {
            DisplayName = "tampered style reference claim safety",
            RequestedBy = "smoke-tests",
            Constraints = CreateConstraints(
                assetRoot,
                includeWorkspace: true,
                styleReferences:
                [
                    new MaterialGenerationStyleReference
                    {
                        Id = "tampered-style-reference",
                        Name = "Tampered style reference",
                        Sha256 = frozenSha256,
                        MediaType = "image/png",
                        DataRootRelativePath = snapshotRelativePath
                    }
                ])
        });

        await File.WriteAllBytesAsync(
            snapshotFullPath,
            [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x02]);

        var provider = new GenerationToolProvider(
            store,
            new WorkspaceContextResolver(options),
            options,
            store.StoreFilePath);
        var result = await provider.CreateCatalog().CallAsync(
            "claim_generation_task",
            JsonSerializer.SerializeToElement(new
            {
                taskId = task.Id,
                agentId = "hash-safety-agent"
            }),
            CancellationToken.None);
        var resultJson = JsonSerializer.SerializeToElement(result, ProtocolJson.Options);
        True(resultJson.GetProperty("isError").GetBoolean(),
            "风格参考图快照内容与冻结 SHA-256 不一致时必须拒绝认领");

        var persisted = await store.GetAsync(task.Id)
            ?? throw new InvalidOperationException("摘要校验失败后任务不应丢失。");
        Equal(GenerationTaskStatus.Pending, persisted.Status,
            "风格引用摘要校验必须在持久化 Claimed 之前完成");
        True(string.IsNullOrWhiteSpace(persisted.ClaimedBy),
            "风格引用摘要校验失败不得留下认领所有者");
    }

    private static GenerationConstraintPackage CreateConstraints(
        string assetRoot,
        bool includeWorkspace = true,
        MaterialGenerationStyleReference[]? styleReferences = null)
    {
        var runId = Guid.NewGuid().ToString("N");
        var rootPath = ProjectAssetLayout.BuildAiRunRelativePath(
            ProjectAssetLayout.TilemapCategory,
            runId);
        var finalDirectory = ProjectAssetLayout.BuildFinalBatchRelativePath(
            ProjectAssetLayout.TilemapCategory,
            runId);
        var itemId = Guid.NewGuid().ToString("N");
        var basePackage = new GenerationConstraintPackage
        {
            WorkflowId = "claim-safety",
            PreferredSkill = "imagegen",
            Prompt = "Generate one test image.",
            Output = new GenerationOutputSpec
            {
                Width = 64,
                Height = 64,
                Count = 1,
                MaxParallelism = 1,
                Format = "png",
                TransparentBackground = true,
                BackgroundRemoval = new GenerationBackgroundRemovalSpec()
            },
            Parameters = new JsonObject()
        };
        return MaterialGenerationConstraintComposer.Compose(
            basePackage,
            new MaterialGenerationStyleSelection
            {
                GroupId = "claim-safety-style",
                Name = "Claim safety style",
                Prompt = "neutral test style",
                References = styleReferences ?? []
            },
            new GenerationDeliveryPlan
            {
                AssetRoot = assetRoot,
                OutputDirectory = finalDirectory,
                TargetPaths = [$"{finalDirectory}/{itemId}.png"],
                StagingPaths = includeWorkspace
                    ? [$"{rootPath}/35-normalized-input/{itemId}.normalized.png"]
                    : [],
                SuggestedBaseName = itemId,
                Workspace = includeWorkspace
                    ? new GenerationRunWorkspacePlan
                    {
                        RunId = runId,
                        RootPath = rootPath,
                        ManifestPath = $"{rootPath}/00-manifest/run.v1.json",
                        PromptPath = $"{rootPath}/20-prompts/{itemId}.txt",
                        DiagnosticsPath = $"{rootPath}/90-diagnostics/stages.v1.jsonl",
                        ModelOutputPaths = [$"{rootPath}/30-model-output/{itemId}.model.png"],
                        ProcessedPaths = [$"{rootPath}/40-background-removed/{itemId}.transparent.png"]
                    }
                    : null
            });
    }

    private static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void False(bool condition, string message) => True(!condition, message);

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message}：预期 {expected}，实际 {actual}。");
        }
    }
}
