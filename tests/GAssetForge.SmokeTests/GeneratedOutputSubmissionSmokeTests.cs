using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.IO;
using GAssetForge.App.Services;
using GAssetForge.Core.Generation;
using GAssetForge.Core.Projects;
using GAssetForge.McpServer;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace GAssetForge.SmokeTests;

internal static class GeneratedOutputSubmissionSmokeTests
{
    private const int SourceSize = 1254;
    private const int OpaqueSourceWidth = 640;
    private const int OpaqueSourceHeight = 320;
    private const int OutputSize = 512;
    private const string AgentId = "codex-submit-generation-output";
    private static readonly Rgba32 KeyColor = new(255, 0, 255, 255);
    private static readonly Rgba32 SubjectColor = new(225, 95, 25, 255);

    public static async Task RunAllAsync()
    {
        var dataRoot = Path.Combine(
            Path.GetTempPath(),
            "GAssetForge.GeneratedOutputSubmissionSmokeTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataRoot);

        try
        {
            LegacyFrozenRunOpenOrRepairStaysInPlace(dataRoot);
            await RetryReferenceMirroringRejectsRunJunctionAsync(dataRoot);
            await SubmissionPreservesAuditableStagesAndCompletesIdempotentlyAsync(dataRoot);
            await SubmissionWithoutBackgroundRemovalPreservesOpaqueDirectStagesAsync(dataRoot);
            await GeometryFailureReturnsWarningAndPreservesEveryIntermediateAsync(dataRoot);
            await HarnessFallbackGeneratesExactRecommendedPathAndCompletesAsync(dataRoot);
            await HarnessFallbackRefreshesTaskAfterConcurrentCompletionAsync(dataRoot);
        }
        finally
        {
            if (Directory.Exists(dataRoot))
            {
                Directory.Delete(dataRoot, recursive: true);
            }
        }
    }

    private static void LegacyFrozenRunOpenOrRepairStaysInPlace(string dataRoot)
    {
        var paths = new WorkspacePaths(Path.Combine(dataRoot, "legacy-frozen-run-repair"));
        var batchId = Guid.NewGuid().ToString("N");
        var legacyRootRelativePath = $"Generated/GAssetForge/Runs/{batchId}";
        var legacyRootFullPath = ResolveArtifactPath(paths.AssetDirectory, legacyRootRelativePath);
        var markerPath = Path.Combine(legacyRootFullPath, "00-manifest", "legacy-marker.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(markerPath)!);
        File.WriteAllText(markerPath, "frozen legacy run");

        var repaired = new GenerationRunWorkspaceService().OpenOrRepair(
            batchId,
            paths.AssetDirectory,
            legacyRootRelativePath);

        Equal(legacyRootRelativePath, repaired.RootRelativePath,
            "旧 Generated/GAssetForge/Runs/<batch> 冻结路径必须保持原值");
        Equal(legacyRootFullPath, repaired.RootFullPath,
            "OpenOrRepair 必须原地打开旧 Run");
        True(File.Exists(markerPath),
            "原地修复不得覆盖旧 Run 中已存在的审计文件");
        foreach (var requiredDirectory in new[]
                 {
                     "00-manifest",
                     "10-style-reference",
                     "20-prompts",
                     "25-region-edit",
                     "30-model-output",
                     "35-normalized-input",
                     "40-background-removed",
                     "50-final",
                     "90-diagnostics"
                 })
        {
            True(Directory.Exists(Path.Combine(legacyRootFullPath, requiredDirectory)),
                $"旧 Run 原地修复后应补齐 {requiredDirectory}");
        }

        var canonicalCopy = ResolveArtifactPath(
            paths.AssetDirectory,
            ProjectAssetLayout.BuildAiRunRelativePath(
                ProjectAssetLayout.GeneralCategory,
                batchId));
        False(Directory.Exists(canonicalCopy),
            "旧冻结 Run 原地修复时不得创建 AI/General 副本");
    }

    private static async Task RetryReferenceMirroringRejectsRunJunctionAsync(string dataRoot)
    {
        var paths = new WorkspacePaths(Path.Combine(dataRoot, "retry-reference-run-junction"));
        var parentBatchId = Guid.NewGuid().ToString("N");
        var parentRootRelativePath = $"Generated/GAssetForge/Runs/{parentBatchId}";
        var parentRoot = ResolveArtifactPath(paths.AssetDirectory, parentRootRelativePath);
        Directory.CreateDirectory(parentRoot);
        var outsideReferenceRoot = Path.Combine(dataRoot, "outside-run-references");
        Directory.CreateDirectory(outsideReferenceRoot);
        File.WriteAllText(Path.Combine(outsideReferenceRoot, "outside.png"), "not a model reference");
        var linkedReferenceRoot = Path.Combine(parentRoot, "10-style-reference");
        try
        {
            Directory.CreateSymbolicLink(linkedReferenceRoot, outsideReferenceRoot);
        }
        catch (Exception exception) when (exception is
            UnauthorizedAccessException or
            IOException or
            PlatformNotSupportedException or
            NotSupportedException)
        {
            return;
        }

        try
        {
            var service = new GenerationRunWorkspaceService();
            var retry = service.Prepare(
                Guid.NewGuid().ToString("N"),
                paths.AssetDirectory,
                ProjectAssetLayout.TilemapCategory);
            var previousDelivery = new GenerationDeliveryPlan
            {
                AssetRoot = paths.AssetDirectory,
                Workspace = new GenerationRunWorkspacePlan
                {
                    RunId = parentBatchId,
                    RootPath = parentRootRelativePath
                }
            };

            await ThrowsAsync<InvalidDataException>(
                () => service.MirrorExistingRunReferencesAsync(retry, previousDelivery),
                "重试参考复制必须拒绝冻结 Run 中经过目录联接的 reference 根");
        }
        finally
        {
            if (Directory.Exists(linkedReferenceRoot))
            {
                Directory.Delete(linkedReferenceRoot);
            }
        }
    }

    private static async Task SubmissionWithoutBackgroundRemovalPreservesOpaqueDirectStagesAsync(
        string dataRoot)
    {
        var root = Path.Combine(dataRoot, "opaque-direct-output");
        var store = new JsonGenerationTaskStore(Path.Combine(root, "Generation"));
        var options = new ServerOptions(
            root,
            McpTransport.Stdio,
            IPAddress.Loopback,
            43120,
            "codex",
            null,
            null);
        var fixture = await CreateOpaqueDirectTaskFixtureAsync(root, store);
        Equal(false, fixture.Task.Constraints.Output.TransparentBackground,
            "无背景剔除回归必须冻结为不透明输出");
        True(fixture.Task.Constraints.Output.BackgroundRemoval is null,
            "无背景剔除回归不能携带 BackgroundRemoval");
        Equal(0, fixture.Delivery.StagingPaths.Length,
            "无背景剔除 delivery 必须保持 StagingPaths=[]");

        var sourcePath = Path.Combine(root, "ModelReturns", "opaque-wide-640x320.png");
        WriteOpaqueWideModelFixture(sourcePath);
        var sourceHash = ComputeSha256(sourcePath);
        var catalog = new GenerationToolProvider(
                store,
                new WorkspaceContextResolver(options),
                options,
                store.StoreFilePath)
            .CreateCatalog();
        var claim = await catalog.CallAsync(
            "claim_generation_task",
            JsonSerializer.SerializeToElement(new { taskId = fixture.Task.Id, agentId = AgentId }),
            CancellationToken.None);
        False(IsToolError(claim), "无背景剔除回归任务应先成功认领");

        var result = await catalog.CallAsync(
            "submit_generation_output",
            JsonSerializer.SerializeToElement(new
            {
                taskId = fixture.Task.Id,
                agentId = AgentId,
                outputIndex = 0,
                sourcePath
            }),
            CancellationToken.None);
        False(IsToolError(result),
            "无 BackgroundRemoval、StagingPaths=[] 的模型 PNG 应由 submit_generation_output 直接完成");
        var structured = JsonSerializer.SerializeToElement(result, ProtocolJson.Options)
            .GetProperty("structuredContent");
        Equal("completed", structured.GetProperty("task").GetProperty("status").GetString(),
            "无背景剔除提交应自动完成任务");
        Equal(false, structured.GetProperty("idempotent").GetBoolean(),
            "无背景剔除首次提交不得误报幂等");

        var submission = structured.GetProperty("submission");
        Equal(true, submission.GetProperty("sourceResized").GetBoolean(),
            "640x320 模型原图应 contain-fit 到冻结 512x512 画布");
        Equal(sourceHash.ToLowerInvariant(), submission.GetProperty("sourceSha256").GetString(),
            "无背景剔除提交仍应记录原始模型文件 SHA-256");

        var workspace = fixture.Delivery.Workspace
            ?? throw new InvalidOperationException("无背景剔除回归缺少 generation workspace。");
        var expectedNormalizedRelativePath = BuildExpectedDirectNormalizedPath(workspace, 0);
        Equal(expectedNormalizedRelativePath, submission.GetProperty("normalizedPath").GetString(),
            "StagingPaths=[] 时软件应从 30-model-output 派生 35-normalized-input 路径");
        Equal(workspace.ProcessedPaths[0], submission.GetProperty("processedPath").GetString(),
            "无背景剔除提交仍应保留 workspace processed 产物");
        Equal(fixture.Delivery.TargetPaths[0], submission.GetProperty("targetPath").GetString(),
            "无背景剔除提交只能写入冻结 Final target");

        var rawPath = AssertArtifactInsideAssets(
            fixture.AssetRoot,
            submission.GetProperty("modelOutputPath").GetString(),
            "无背景剔除 raw 模型原图");
        var normalizedPath = AssertArtifactInsideAssets(
            fixture.AssetRoot,
            submission.GetProperty("normalizedPath").GetString(),
            "无背景剔除 normalized 输入");
        var processedPath = AssertArtifactInsideAssets(
            fixture.AssetRoot,
            submission.GetProperty("processedPath").GetString(),
            "无背景剔除 processed 产物");
        var targetPath = AssertArtifactInsideAssets(
            fixture.AssetRoot,
            submission.GetProperty("targetPath").GetString(),
            "无背景剔除 Final target");
        AssertImageSize(rawPath, OpaqueSourceWidth, OpaqueSourceHeight,
            "无背景剔除归档 raw");
        Equal(sourceHash, ComputeSha256(rawPath),
            "无背景剔除归档 raw 必须逐字节保留模型原图");
        AssertOpaqueContainFitOutput(normalizedPath, "无背景剔除 normalized");
        AssertOpaqueContainFitOutput(processedPath, "无背景剔除 processed");
        AssertOpaqueContainFitOutput(targetPath, "无背景剔除 Final");
        Equal(ComputeSha256(normalizedPath), ComputeSha256(processedPath),
            "无背景剔除 processed 应完整保留归一化像素");
        Equal(ComputeSha256(processedPath), ComputeSha256(targetPath),
            "无背景剔除 Final 应完整提交 workspace processed 产物");

        var persisted = await store.GetAsync(fixture.Task.Id)
            ?? throw new InvalidOperationException("无背景剔除提交任务未持久化。");
        Equal(GenerationTaskStatus.Completed, persisted.Status,
            "无背景剔除 submit_generation_output 返回后真相源必须为 Completed");
        Equal(fixture.Delivery.TargetPaths[0], persisted.RelativeAssetPaths.Single(),
            "无背景剔除 Completed 任务只应登记冻结 Final target");

        var diagnosticsPath = ResolveArtifactPath(
            fixture.AssetRoot,
            workspace.DiagnosticsPath);
        var journalEvents = ReadJournal(diagnosticsPath);
        AssertMeasuredStage(journalEvents, "model_output.import");
        AssertMeasuredStage(journalEvents, "model_output.normalize");
        AssertMeasuredStage(journalEvents, "output.process_and_commit");
        AssertMeasuredStage(journalEvents, "task.complete");
    }

    private static async Task GeometryFailureReturnsWarningAndPreservesEveryIntermediateAsync(
        string dataRoot)
    {
        var root = Path.Combine(dataRoot, "geometry-warning");
        var store = new JsonGenerationTaskStore(Path.Combine(root, "Generation"));
        var options = new ServerOptions(
            root,
            McpTransport.Stdio,
            IPAddress.Loopback,
            43120,
            "codex",
            null,
            null);
        var fixture = await CreateTaskFixtureAsync(
            root,
            store,
            maxAttempts: 4,
            diamondAngle: 90d,
            rotationAngle: 0d);
        var sourcePath = Path.Combine(root, "ModelReturns", "axis-aligned-384x448.png");
        WriteGeometryMismatchFixture(sourcePath);
        var catalog = new GenerationToolProvider(
                store,
                new WorkspaceContextResolver(options),
                options,
                store.StoreFilePath)
            .CreateCatalog();
        var claim = await catalog.CallAsync(
            "claim_generation_task",
            JsonSerializer.SerializeToElement(new { taskId = fixture.Task.Id, agentId = AgentId }),
            CancellationToken.None);
        False(IsToolError(claim), "几何 warning 回归任务应先成功认领");

        var result = await catalog.CallAsync(
            "submit_generation_output",
            JsonSerializer.SerializeToElement(new
            {
                taskId = fixture.Task.Id,
                agentId = AgentId,
                outputIndex = 0,
                sourcePath
            }),
            CancellationToken.None);
        False(IsToolError(result),
            "图片已落盘但几何未通过时应正常返回 warning，而不是抛 MCP tool error");
        var structured = JsonSerializer.SerializeToElement(result, ProtocolJson.Options)
            .GetProperty("structuredContent");
        Equal("retryable_validation_failure", structured.GetProperty("outcome").GetString(),
            "几何未通过应返回可自动重做 outcome");
        Equal("output_geometry_validation_failed",
            structured.GetProperty("failure").GetProperty("code").GetString(),
            "外层失败码不得再误报图片交付失败");
        Equal("tilemap_geometry_aspect_ratio_mismatch",
            structured.GetProperty("failure").GetProperty("subCode").GetString(),
            "384×448 轴对齐矩形应归类为主体宽高比错误");
        Equal(3, structured.GetProperty("failure").GetProperty("remainingAttempts").GetInt32(),
            "默认四次总尝试的首次失败后应剩余三次");

        var persisted = await store.GetAsync(fixture.Task.Id)
            ?? throw new InvalidOperationException("几何 warning 任务未持久化。");
        Equal(GenerationTaskStatus.Failed, persisted.Status,
            "单次 attempt 应持久化失败以便原子创建后继任务");
        Equal("output_geometry_validation_failed", persisted.FailureCode,
            "任务应保留外层几何校验失败码");
        Equal("tilemap_geometry_aspect_ratio_mismatch", persisted.FailureSubCode,
            "任务应保留具体宽高比子错误");
        Equal(true, persisted.FailureRetryable, "几何质量问题应标记为可重做");

        var modelPath = ResolveArtifactPath(fixture.AssetRoot, fixture.Delivery.Workspace!.ModelOutputPaths[0]);
        var normalizedPath = ResolveArtifactPath(fixture.AssetRoot, fixture.Delivery.StagingPaths[0]);
        var processedPath = ResolveArtifactPath(fixture.AssetRoot, fixture.Delivery.Workspace.ProcessedPaths[0]);
        var targetPath = ResolveArtifactPath(fixture.AssetRoot, fixture.Delivery.TargetPaths[0]);
        True(File.Exists(modelPath), "几何失败前的模型原图必须保留");
        True(File.Exists(normalizedPath), "几何失败前的归一化图必须保留");
        True(File.Exists(processedPath), "未通过几何校验的背景处理图必须先落盘并保留");
        False(File.Exists(targetPath), "未通过几何校验的图片不得进入最终 target");

        var diagnosticsPath = ResolveArtifactPath(
            fixture.AssetRoot,
            fixture.Delivery.Workspace.DiagnosticsPath);
        var warning = ReadJournal(diagnosticsPath).LastOrDefault(item =>
            string.Equals(item.Stage, "output.geometry_validation", StringComparison.Ordinal) &&
            string.Equals(item.Status, "retryable_warning", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("几何失败缺少结构化 warning journal。");
        Equal(processedPath, warning.ObservedPath, "结构化诊断必须记录实际校验文件路径");
        Equal(384, warning.GeometryDiagnostics?.ActualWidth, "结构化诊断实际宽度");
        Equal(448, warning.GeometryDiagnostics?.ActualHeight, "结构化诊断实际高度");
        True(warning.GeometryDiagnostics?.DiamondScore is < 0.5d,
            "轴对齐长方形不应被误判为高菱形评分");
    }

    private static async Task HarnessFallbackGeneratesExactRecommendedPathAndCompletesAsync(
        string dataRoot)
    {
        var root = Path.Combine(dataRoot, "harness-fallback-tool");
        var store = new JsonGenerationTaskStore(Path.Combine(root, "Generation"));
        var options = new ServerOptions(
            root,
            McpTransport.Stdio,
            IPAddress.Loopback,
            43120,
            "codex",
            null,
            null);
        var contextResolver = new WorkspaceContextResolver(options);
        var fixture = await CreateTaskFixtureAsync(root, store);
        var fallback = new RecordingFallbackService(request =>
        {
            True(request.PromptText.Contains("Final target canvas: 512x512", StringComparison.Ordinal),
                "Harness fallback prompt 必须保留冻结目标画布");
            True(request.PromptText.Contains("Required constraints", StringComparison.Ordinal),
                "Harness fallback prompt 必须包含冻结 required rules");
            True(request.PromptText.Contains("#FF00FF", StringComparison.Ordinal),
                "Harness fallback prompt 必须包含第一道纯色底合同");
            True(request.PromptFilePath.EndsWith(".imagegen-cli.txt", StringComparison.OrdinalIgnoreCase),
                "Harness fallback 应在项目 prompts 分类中写入独立冻结提示文件");
            True(Path.IsPathFullyQualified(request.RecommendedSourcePath),
                "Harness fallback 只能接收软件计算的绝对 recommendedSourcePath");
            WriteModelFixture(request.RecommendedSourcePath, SubjectColor);
            return new ImageGenerationCliFallbackResult(
                request.RecommendedSourcePath,
                "fixture-python",
                "fixture-image_gen.py",
                "fixture generated exact PNG",
                AlreadyExisted: false);
        });
        var provider = new GenerationToolProvider(
            store,
            contextResolver,
            options,
            store.StoreFilePath,
            fallback);
        var catalog = provider.CreateCatalog();
        var claim = await catalog.CallAsync(
            "claim_generation_task",
            JsonSerializer.SerializeToElement(new
            {
                taskId = fixture.Task.Id,
                agentId = AgentId
            }),
            CancellationToken.None);
        False(IsToolError(claim), "Harness fallback 回归任务应先成功认领");

        var result = await catalog.CallAsync(
            "run_image_delivery_fallback",
            JsonSerializer.SerializeToElement(new
            {
                taskId = fixture.Task.Id,
                agentId = AgentId
            }),
            CancellationToken.None);
        False(IsToolError(result), "Harness fallback 工具应生成、验证、后处理并完成任务");
        Equal(1, fallback.Requests.Count,
            "一次任务只应触发一次受控 Harness fallback 调用");

        var structured = JsonSerializer.SerializeToElement(result, ProtocolJson.Options)
            .GetProperty("structuredContent");
        Equal("completed",
            structured.GetProperty("submission").GetProperty("task").GetProperty("status").GetString(),
            "run_image_delivery_fallback 必须自动完成单图任务登记");
        var persisted = await store.GetAsync(fixture.Task.Id)
            ?? throw new InvalidOperationException("Harness fallback 任务未持久化。");
        Equal(GenerationTaskStatus.Completed, persisted.Status,
            "Harness fallback 成功返回后任务真相源必须为 Completed");
        var target = Path.Combine(
            fixture.AssetRoot,
            fixture.Delivery.TargetPaths[0].Replace('/', Path.DirectorySeparatorChar));
        True(File.Exists(target), "Harness fallback 必须产出冻结最终 target");

        var diagnostics = Path.Combine(
            fixture.AssetRoot,
            fixture.Delivery.Workspace!.DiagnosticsPath.Replace('/', Path.DirectorySeparatorChar));
        AssertMeasuredStage(ReadJournal(diagnostics), "image.cli_fallback");

        var idempotent = await catalog.CallAsync(
            "run_image_delivery_fallback",
            JsonSerializer.SerializeToElement(new
            {
                taskId = fixture.Task.Id,
                agentId = AgentId
            }),
            CancellationToken.None);
        False(IsToolError(idempotent), "Completed fallback 任务重复调用应幂等成功");
        Equal(1, fallback.Requests.Count,
            "Completed 任务不得再次消耗 Image API 或调用 fallback service");
    }

    private static async Task HarnessFallbackRefreshesTaskAfterConcurrentCompletionAsync(
        string dataRoot)
    {
        var root = Path.Combine(dataRoot, "harness-fallback-concurrent-completion");
        var store = new JsonGenerationTaskStore(Path.Combine(root, "Generation"));
        var options = new ServerOptions(
            root,
            McpTransport.Stdio,
            IPAddress.Loopback,
            43120,
            "codex",
            null,
            null);
        var contextResolver = new WorkspaceContextResolver(options);
        var fixture = await CreateTaskFixtureAsync(root, store);
        var submissionService = new GeneratedOutputSubmissionService(
            contextResolver,
            new GeneratedAssetPostProcessor(contextResolver));
        var fallback = new RecordingFallbackService(request =>
        {
            WriteModelFixture(request.RecommendedSourcePath, SubjectColor);
            var current = store.GetAsync(fixture.Task.Id).GetAwaiter().GetResult()
                ?? throw new InvalidOperationException("并发完成回归任务不存在。");
            _ = submissionService.SubmitAsync(
                    current,
                    fixture.Delivery,
                    AgentId,
                    0,
                    request.RecommendedSourcePath,
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            _ = store.CompleteAsync(
                    fixture.Task.Id,
                    AgentId,
                    fixture.Delivery.TargetPaths,
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            return new ImageGenerationCliFallbackResult(
                request.RecommendedSourcePath,
                "fixture-python",
                "fixture-image_gen.py",
                "another invocation completed this task",
                AlreadyExisted: false);
        });
        var catalog = new GenerationToolProvider(
                store,
                contextResolver,
                options,
                store.StoreFilePath,
                fallback)
            .CreateCatalog();
        var claim = await catalog.CallAsync(
            "claim_generation_task",
            JsonSerializer.SerializeToElement(new
            {
                taskId = fixture.Task.Id,
                agentId = AgentId
            }),
            CancellationToken.None);
        False(IsToolError(claim), "并发完成回归任务应先成功认领");

        var result = await catalog.CallAsync(
            "run_image_delivery_fallback",
            JsonSerializer.SerializeToElement(new
            {
                taskId = fixture.Task.Id,
                agentId = AgentId
            }),
            CancellationToken.None);

        False(IsToolError(result),
            "fallback 返回后任务若已由同一认领者并发完成，应重新读取状态并幂等成功");
        var structured = JsonSerializer.SerializeToElement(result, ProtocolJson.Options)
            .GetProperty("structuredContent");
        Equal(true, structured.GetProperty("submission").GetProperty("idempotent").GetBoolean(),
            "并发完成后的外层 fallback 应报告 idempotent=true，不得用陈旧 Claimed 快照再次续租");
        Equal(1, fallback.Requests.Count,
            "状态刷新只能复用本次真实生成结果，不得再次调用图片 API");
    }

    private static async Task SubmissionPreservesAuditableStagesAndCompletesIdempotentlyAsync(
        string dataRoot)
    {
        var store = new JsonGenerationTaskStore(Path.Combine(dataRoot, "Generation"));
        var options = new ServerOptions(
            dataRoot,
            McpTransport.Stdio,
            IPAddress.Loopback,
            43120,
            "codex",
            null,
            null);
        var contextResolver = new WorkspaceContextResolver(options);
        var fixture = await CreateTaskFixtureAsync(dataRoot, store);
        var sourcePath = Path.Combine(dataRoot, "ModelReturns", "model-source-1254.png");
        var differentSourcePath = Path.Combine(dataRoot, "ModelReturns", "different-model-source-1254.png");
        WriteModelFixture(sourcePath, SubjectColor);
        WriteModelFixture(differentSourcePath, new Rgba32(25, 135, 225, 255));
        var sourceHash = ComputeSha256(sourcePath);

        var claimed = await InvokeAsync(
            dataRoot,
            "claim_generation_task",
            new { agentId = AgentId, taskId = fixture.Task.Id });
        Equal(0, claimed.ExitCode, $"submit_generation_output 回归任务应能认领：{claimed.Output}");

        var submitted = await InvokeAsync(
            dataRoot,
            "submit_generation_output",
            new
            {
                taskId = fixture.Task.Id,
                agentId = AgentId,
                outputIndex = 0,
                sourcePath
            });
        Equal(0, submitted.ExitCode, $"真实模型 PNG 应完成软件接管提交：{submitted.Output}");
        var content = ReadStructuredContent(submitted);
        Equal(false, content.GetProperty("idempotent").GetBoolean(), "首次提交不得误报幂等");
        Equal("completed", content.GetProperty("task").GetProperty("status").GetString(),
            "一次 submit_generation_output 应自动完成单图任务");
        Equal(fixture.Delivery.TargetPaths[0],
            content.GetProperty("relativeAssetPaths")[0].GetString(),
            "任务完成登记只能使用冻结 targetPath");

        var submission = content.GetProperty("submission");
        Equal(true, submission.GetProperty("sourceResized").GetBoolean(),
            "1254x1254 模型原图应由软件归一化到 512x512");
        Equal(sourceHash.ToLowerInvariant(), submission.GetProperty("sourceSha256").GetString(),
            "提交结果应记录原始模型文件 SHA-256");

        var rawPath = AssertArtifactInsideAssets(
            fixture.AssetRoot,
            submission.GetProperty("modelOutputPath").GetString(),
            "raw 模型原图");
        var normalizedPath = AssertArtifactInsideAssets(
            fixture.AssetRoot,
            submission.GetProperty("normalizedPath").GetString(),
            "normalized 输入");
        var processedPath = AssertArtifactInsideAssets(
            fixture.AssetRoot,
            submission.GetProperty("processedPath").GetString(),
            "processed 透明中间产物");
        var targetPath = AssertArtifactInsideAssets(
            fixture.AssetRoot,
            submission.GetProperty("targetPath").GetString(),
            "最终 target");
        var diagnosticsPath = AssertArtifactInsideAssets(
            fixture.AssetRoot,
            fixture.Delivery.Workspace?.DiagnosticsPath,
            "stages.v1.jsonl");
        Equal(
            Path.GetFullPath(sourcePath),
            Path.GetFullPath(submission.GetProperty("sourcePath").GetString()
                ?? throw new InvalidDataException("提交结果缺少 sourcePath。")),
            "提交结果应保留外部图像模型返回文件的来源路径");
        var sourceRelativeToAssets = Path.GetRelativePath(fixture.AssetRoot, sourcePath);
        True(
            Path.IsPathRooted(sourceRelativeToAssets) ||
            sourceRelativeToAssets == ".." ||
            sourceRelativeToAssets.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal),
            "回归源文件应模拟 Codex 默认目录等项目外模型返回，再验证软件导入项目 Run");

        AssertImageSize(rawPath, SourceSize, SourceSize, "归档 raw");
        Equal(sourceHash, ComputeSha256(rawPath), "归档 raw 必须逐字节保留 1254x1254 模型原图");
        AssertNormalizedImage(normalizedPath);
        AssertTransparentOutput(processedPath, "processed 中间产物");
        AssertTransparentOutput(targetPath, "最终 target");
        Equal(ComputeSha256(processedPath), ComputeSha256(targetPath),
            "最终 target 应与已保留的 processed 产物完全一致");

        var persisted = await store.GetAsync(fixture.Task.Id)
            ?? throw new InvalidOperationException("submit_generation_output 回归任务未持久化。");
        Equal(GenerationTaskStatus.Completed, persisted.Status,
            "submit_generation_output 返回后任务存储应已经是 Completed");
        Equal(fixture.Delivery.TargetPaths[0], persisted.RelativeAssetPaths.Single(),
            "Completed 任务只应登记项目内最终 target");

        var journalEvents = ReadJournal(diagnosticsPath);
        AssertMeasuredStage(journalEvents, "model_output.import");
        AssertMeasuredStage(journalEvents, "model_output.normalize");
        AssertMeasuredStage(journalEvents, "output.process_and_commit");
        AssertMeasuredStage(journalEvents, "task.complete");
        var journalLineCount = journalEvents.Length;

        var rawHashBeforeRetry = ComputeSha256(rawPath);
        var targetHashBeforeRetry = ComputeSha256(targetPath);
        var sameSourceRetry = await InvokeAsync(
            dataRoot,
            "submit_generation_output",
            new { taskId = fixture.Task.Id, agentId = AgentId, outputIndex = 0, sourcePath });
        Equal(0, sameSourceRetry.ExitCode, $"同源重复提交应幂等成功：{sameSourceRetry.Output}");
        Equal(true, ReadStructuredContent(sameSourceRetry).GetProperty("idempotent").GetBoolean(),
            "同源重复提交应明确报告 idempotent=true");
        Equal(rawHashBeforeRetry, ComputeSha256(rawPath), "同源重试不得重写归档 raw");
        Equal(targetHashBeforeRetry, ComputeSha256(targetPath), "同源重试不得重写最终 target");
        Equal(journalLineCount, ReadJournal(diagnosticsPath).Length,
            "Completed 任务的同源幂等提交不得重复记录执行阶段");

        var differentSourceRetry = await InvokeAsync(
            dataRoot,
            "submit_generation_output",
            new
            {
                taskId = fixture.Task.Id,
                agentId = AgentId,
                outputIndex = 0,
                sourcePath = differentSourcePath
            });
        Equal(0, differentSourceRetry.ExitCode,
            $"Completed 任务收到不同 source 时仍应保留既有完成结果：{differentSourceRetry.Output}");
        Equal(true, ReadStructuredContent(differentSourceRetry).GetProperty("idempotent").GetBoolean(),
            "Completed 任务不得因新的 sourcePath 重开施工");
        Equal(rawHashBeforeRetry, ComputeSha256(rawPath), "不同源重复提交绝不能覆盖已归档 raw");
        Equal(targetHashBeforeRetry, ComputeSha256(targetPath), "不同源重复提交不得改变最终 target");

        var submissionService = new GeneratedOutputSubmissionService(
            contextResolver,
            new GeneratedAssetPostProcessor(contextResolver));
        var claimedSnapshot = persisted with
        {
            Status = GenerationTaskStatus.Claimed,
            ClaimedBy = AgentId
        };
        await ThrowsAsync<IOException>(
            () => submissionService.SubmitAsync(
                claimedSnapshot,
                fixture.Delivery,
                AgentId,
                0,
                differentSourcePath,
                CancellationToken.None),
            "即使绕过 Completed 工具短路，提交服务也必须拒绝不同源覆盖既有 raw");
        Equal(rawHashBeforeRetry, ComputeSha256(rawPath),
            "提交服务拒绝不同源后必须保留原 raw 审计文件");
    }

    private static async Task<TaskFixture> CreateOpaqueDirectTaskFixtureAsync(
        string dataRoot,
        JsonGenerationTaskStore store)
    {
        var paths = new WorkspacePaths(dataRoot);
        var assetRoot = paths.AssetDirectory;
        var batchId = Guid.NewGuid().ToString("N");
        var itemId = Guid.NewGuid().ToString("N");
        var suggestedBaseName = "opaque-direct-submit-v001";
        var workspaceService = new GenerationRunWorkspaceService();
        var run = workspaceService.Prepare(
            batchId,
            assetRoot,
            ProjectAssetLayout.TilemapCategory);
        Equal(
            ProjectAssetLayout.BuildAiRunRelativePath(ProjectAssetLayout.TilemapCategory, batchId),
            run.RootRelativePath,
            "无背景剔除夹具 Run 应落入 AI/Tilemap/<batch>");
        var workspace = workspaceService.CreateOutputPlan(run, itemId, suggestedBaseName);
        var outputDirectory = ProjectAssetLayout.BuildFinalBatchRelativePath(
            ProjectAssetLayout.TilemapCategory,
            batchId);
        var finalBatchDirectory = Path.Combine(
            paths.FinalAssetDirectory,
            ProjectAssetLayout.TilemapCategory,
            batchId);
        Directory.CreateDirectory(finalBatchDirectory);
        Equal(finalBatchDirectory, ResolveArtifactPath(assetRoot, outputDirectory),
            "无背景剔除夹具应使用 WorkspacePaths.FinalAssetDirectory");
        var targetPath = $"{outputDirectory}/{suggestedBaseName}.png";
        var constraints = MaterialGenerationConstraintComposer.Compose(
            new GenerationConstraintPackage
            {
                WorkflowId = "opaque-direct-output-smoke",
                PreferredSkill = "imagegen",
                Prompt = "生成一张横向不透明游戏美术画面，用于验证软件直接交付链路。",
                Output = new GenerationOutputSpec
                {
                    Width = OutputSize,
                    Height = OutputSize,
                    Count = 1,
                    MaxParallelism = 1,
                    Format = "png",
                    TransparentBackground = false,
                    BackgroundRemoval = null
                }
            },
            new MaterialGenerationStyleSelection
            {
                GroupId = "opaque-direct-output-style",
                Name = "不透明直接交付回归风格",
                Description = "验证无背景剔除任务的 raw、归一化、processed 与 Final 交付。",
                Prompt = "clean opaque game art"
            },
            new GenerationDeliveryPlan
            {
                AssetRoot = assetRoot,
                OutputDirectory = outputDirectory,
                TargetPaths = [targetPath],
                StagingPaths = [],
                SuggestedBaseName = suggestedBaseName,
                Workspace = workspace
            });
        var tasks = await store.CreateBatchAsync(new CreateGenerationBatchRequest
        {
            BatchId = batchId,
            DisplayName = "无背景剔除 submit_generation_output 回归批次",
            RequestedBy = "smoke-tests",
            Items =
            [
                new CreateGenerationBatchItemRequest
                {
                    ItemId = itemId,
                    DisplayName = "不透明模型原图软件直接接管提交",
                    Constraints = constraints
                }
            ]
        });
        True(
            MaterialGenerationConstraintComposer.TryReadDelivery(tasks.Single().Constraints, out var delivery),
            "无背景剔除 submit_generation_output 测试任务应包含有效冻结 delivery");
        return new TaskFixture(
            tasks.Single(),
            delivery ?? throw new InvalidOperationException("无背景剔除 delivery 缺失。"),
            assetRoot);
    }

    private static async Task<TaskFixture> CreateTaskFixtureAsync(
        string dataRoot,
        JsonGenerationTaskStore store,
        int maxAttempts = 1,
        double? diamondAngle = null,
        double? rotationAngle = null)
    {
        var paths = new WorkspacePaths(dataRoot);
        var assetRoot = paths.AssetDirectory;
        var batchId = Guid.NewGuid().ToString("N");
        var itemId = Guid.NewGuid().ToString("N");
        var suggestedBaseName = "tilemap-submit-output-v001";
        var workspaceService = new GenerationRunWorkspaceService();
        var run = workspaceService.Prepare(
            batchId,
            assetRoot,
            ProjectAssetLayout.TilemapCategory);
        Equal(
            ProjectAssetLayout.BuildAiRunRelativePath(ProjectAssetLayout.TilemapCategory, batchId),
            run.RootRelativePath,
            "submit_generation_output 夹具 Run 应落入 AI/Tilemap/<batch>");
        var workspace = workspaceService.CreateOutputPlan(run, itemId, suggestedBaseName);
        var normalizedPath = workspaceService.CreateNormalizedPath(run, itemId, suggestedBaseName);
        var outputDirectory = ProjectAssetLayout.BuildFinalBatchRelativePath(
            ProjectAssetLayout.TilemapCategory,
            batchId);
        var finalBatchDirectory = Path.Combine(
            paths.FinalAssetDirectory,
            ProjectAssetLayout.TilemapCategory,
            batchId);
        Directory.CreateDirectory(finalBatchDirectory);
        Equal(finalBatchDirectory, ResolveArtifactPath(assetRoot, outputDirectory),
            "submit_generation_output 夹具应使用 WorkspacePaths.FinalAssetDirectory");
        var targetPath = $"{outputDirectory}/{suggestedBaseName}.png";
        var basePackage = TilemapConstraintPackageBuilder.Build(new TilemapGenerationConstraintInput
        {
            ArtWidth = OutputSize,
            ArtHeight = OutputSize,
            OutputCount = 1,
            ContentPrompt = "中央单一游戏瓦片物体，纯洋红背景",
            DiamondAngle = diamondAngle ?? TilemapGenerationConstraintInput.DefaultDiamondAngle,
            RotationAngle = rotationAngle ?? 0d,
            BackgroundRemoval = new GenerationBackgroundRemovalSpec
            {
                Preset = "magenta",
                ColorHex = "#FF00FF",
                TransparentThreshold = 0d,
                OpaqueThreshold = 1d,
                EdgeContract = 0,
                EdgeFeather = 0,
                Despill = false,
                EdgeConnectedOnly = true
            }
        });
        var constraints = MaterialGenerationConstraintComposer.Compose(
            basePackage,
            new MaterialGenerationStyleSelection
            {
                GroupId = "submit-output-style",
                Name = "提交产物回归风格",
                Description = "验证软件接管 raw、归一化、背景剔除与最终交付。",
                Prompt = "clean readable game asset"
            },
            new GenerationDeliveryPlan
            {
                AssetRoot = assetRoot,
                OutputDirectory = outputDirectory,
                TargetPaths = [targetPath],
                StagingPaths = [normalizedPath],
                SuggestedBaseName = suggestedBaseName,
                Workspace = workspace
            });
        var tasks = await store.CreateBatchAsync(new CreateGenerationBatchRequest
        {
            BatchId = batchId,
            DisplayName = "submit_generation_output 回归批次",
            RequestedBy = "smoke-tests",
            Items =
            [
                new CreateGenerationBatchItemRequest
                {
                    ItemId = itemId,
                    DisplayName = "1254 raw 软件接管提交",
                    MaxAttempts = maxAttempts,
                    Constraints = constraints
                }
            ]
        });
        True(
            MaterialGenerationConstraintComposer.TryReadDelivery(tasks.Single().Constraints, out var delivery),
            "submit_generation_output 测试任务应包含有效冻结 delivery");
        return new TaskFixture(
            tasks.Single(),
            delivery ?? throw new InvalidOperationException("submit_generation_output delivery 缺失。"),
            assetRoot);
    }

    private static void WriteModelFixture(string path, Rgba32 subjectColor)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("无法解析模型源图片目录。"));
        using var image = new Image<Rgba32>(SourceSize, SourceSize, KeyColor);
        var center = SourceSize / 2;
        for (var y = center - 64; y <= center + 64; y++)
        {
            for (var x = center - 64; x <= center + 64; x++)
            {
                image[x, y] = subjectColor;
            }
        }

        image.Save(path, new PngEncoder { ColorType = PngColorType.RgbWithAlpha });
    }

    private static void WriteOpaqueWideModelFixture(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("无法解析不透明模型源图片目录。"));
        using var image = new Image<Rgba32>(OpaqueSourceWidth, OpaqueSourceHeight, SubjectColor);
        image.Save(path, new PngEncoder { ColorType = PngColorType.RgbWithAlpha });
    }

    private static void WriteGeometryMismatchFixture(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("无法解析几何回归图片目录。"));
        using var image = new Image<Rgba32>(OutputSize, OutputSize, KeyColor);
        for (var y = 32; y < 480; y++)
        {
            for (var x = 64; x < 448; x++)
            {
                image[x, y] = SubjectColor;
            }
        }

        image.Save(path, new PngEncoder { ColorType = PngColorType.RgbWithAlpha });
    }

    private static string ResolveArtifactPath(string assetRoot, string relativePath) =>
        Path.GetFullPath(Path.Combine(
            assetRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private static string BuildExpectedDirectNormalizedPath(
        GenerationRunWorkspacePlan workspace,
        int outputIndex)
    {
        var rootPath = workspace.RootPath.Trim().Replace('\\', '/').Trim('/');
        var modelPath = workspace.ModelOutputPaths[outputIndex].Trim().Replace('\\', '/').Trim('/');
        var modelPrefix = $"{rootPath}/30-model-output/";
        True(modelPath.StartsWith(modelPrefix, StringComparison.OrdinalIgnoreCase),
            "模型原图路径应位于 generation workspace 的 30-model-output 分类");
        var suffix = modelPath[modelPrefix.Length..];
        var directory = Path.GetDirectoryName(suffix)?.Replace('\\', '/');
        var modelFileName = Path.GetFileName(suffix);
        True(modelFileName.EndsWith(".model.png", StringComparison.OrdinalIgnoreCase),
            "回归模型原图应使用 .model.png 后缀");
        var normalizedFileName =
            $"{modelFileName[..^".model.png".Length]}.normalized.png";
        return string.IsNullOrWhiteSpace(directory)
            ? $"{rootPath}/35-normalized-input/{normalizedFileName}"
            : $"{rootPath}/35-normalized-input/{directory}/{normalizedFileName}";
    }

    private static void AssertNormalizedImage(string path)
    {
        using var image = Image.Load<Rgba32>(path);
        Equal(OutputSize, image.Width, "normalized 宽度");
        Equal(OutputSize, image.Height, "normalized 高度");
        for (var x = 0; x < image.Width; x++)
        {
            Equal(KeyColor, image[x, 0], $"normalized 顶边像素 {x}");
            Equal(KeyColor, image[x, image.Height - 1], $"normalized 底边像素 {x}");
        }

        for (var y = 0; y < image.Height; y++)
        {
            Equal(KeyColor, image[0, y], $"normalized 左边像素 {y}");
            Equal(KeyColor, image[image.Width - 1, y], $"normalized 右边像素 {y}");
        }

        Equal(SubjectColor, image[image.Width / 2, image.Height / 2],
            "normalized 中心主体颜色应保持稳定");
    }

    private static void AssertTransparentOutput(string path, string label)
    {
        using var image = Image.Load<Rgba32>(path);
        Equal(OutputSize, image.Width, $"{label}宽度");
        Equal(OutputSize, image.Height, $"{label}高度");
        Equal((byte)0, image[0, 0].A, $"{label}边框应完成背景剔除");
        Equal(SubjectColor, image[image.Width / 2, image.Height / 2],
            $"{label}中心主体应保持不透明原色");
    }

    private static void AssertOpaqueContainFitOutput(string path, string label)
    {
        using var image = Image.Load<Rgba32>(path);
        Equal(OutputSize, image.Width, $"{label}宽度");
        Equal(OutputSize, image.Height, $"{label}高度");
        var padding = new Rgba32(0, 0, 0, byte.MaxValue);
        var verticalPadding = (OutputSize - OutputSize * OpaqueSourceHeight / OpaqueSourceWidth) / 2;
        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                Equal(byte.MaxValue, image[x, y].A,
                    $"{label}像素 ({x},{y}) 必须保持完全不透明");
                if (y < verticalPadding || y >= image.Height - verticalPadding)
                {
                    Equal(padding, image[x, y],
                        $"{label}补边像素 ({x},{y}) 应为不透明黑色");
                }
            }
        }

        Equal(SubjectColor, image[image.Width / 2, image.Height / 2],
            $"{label}中心主体颜色应保持稳定");
    }

    private static void AssertImageSize(string path, int width, int height, string label)
    {
        using var image = Image.Load<Rgba32>(path);
        Equal(width, image.Width, $"{label}宽度");
        Equal(height, image.Height, $"{label}高度");
    }

    private static GenerationRunStageEvent[] ReadJournal(string path) =>
        File.ReadLines(path)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => JsonSerializer.Deserialize<GenerationRunStageEvent>(line, GenerationJson.Options)
                ?? throw new InvalidDataException("stages.v1.jsonl 包含空事件。"))
            .ToArray();

    private static void AssertMeasuredStage(
        IReadOnlyList<GenerationRunStageEvent> events,
        string stage)
    {
        True(events.Any(item =>
                string.Equals(item.Stage, stage, StringComparison.Ordinal) &&
                string.Equals(item.Status, "started", StringComparison.Ordinal)),
            $"stages.v1.jsonl 应包含 {stage} started 事件");
        var completed = events.LastOrDefault(item =>
            string.Equals(item.Stage, stage, StringComparison.Ordinal) &&
            string.Equals(item.Status, "completed", StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"stages.v1.jsonl 缺少 {stage} completed 事件。");
        True(completed.StartedAtUtc.HasValue, $"{stage} completed 应记录开始时间");
        True(completed.EndedAtUtc.HasValue, $"{stage} completed 应记录结束时间");
        True(completed.ElapsedMilliseconds is >= 0, $"{stage} completed 应记录非负耗时");
    }

    private static string AssertArtifactInsideAssets(
        string assetRoot,
        string? suppliedPath,
        string label)
    {
        True(!string.IsNullOrWhiteSpace(suppliedPath), $"{label}路径不能为空");
        var path = suppliedPath!;
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(assetRoot));
        var fullPath = Path.IsPathFullyQualified(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(
                root,
                path.Replace('/', Path.DirectorySeparatorChar)));
        var relative = Path.GetRelativePath(root, fullPath);
        True(
            !Path.IsPathRooted(relative) &&
            relative is not "." and not ".." &&
            !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal),
            $"{label}必须位于项目 Assets 内：{suppliedPath}");
        True(File.Exists(fullPath), $"{label}文件应真实存在：{fullPath}");
        return fullPath;
    }

    private static JsonElement ReadStructuredContent(DirectToolInvocationResult result)
    {
        using var document = JsonDocument.Parse(result.Output);
        return document.RootElement.GetProperty("structuredContent").Clone();
    }

    private static string ComputeSha256(string path) => Convert.ToHexString(
        SHA256.HashData(File.ReadAllBytes(path)));

    private static bool IsToolError(object result) =>
        JsonSerializer.SerializeToElement(result, ProtocolJson.Options)
            .GetProperty("isError")
            .GetBoolean();

    private static Task<DirectToolInvocationResult> InvokeAsync(
        string dataRoot,
        string toolName,
        object arguments) =>
        DirectToolInvocationExecutor.ExecuteAsync(
        [
            "--data-root",
            dataRoot,
            DirectToolInvocationExecutor.InvokeToolOption,
            toolName,
            DirectToolInvocationExecutor.ArgumentsBase64Option,
            Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(arguments)))
        ]);

    private static async Task ThrowsAsync<TException>(Func<Task> action, string message)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(message);
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
            throw new InvalidOperationException($"{message}。期望：{expected}，实际：{actual}");
        }
    }

    private sealed record TaskFixture(
        GenerationTask Task,
        GenerationDeliveryPlan Delivery,
        string AssetRoot);

    private sealed class RecordingFallbackService(
        Func<ImageGenerationCliFallbackRequest, ImageGenerationCliFallbackResult> handler)
        : IImageGenerationCliFallbackService
    {
        internal List<ImageGenerationCliFallbackRequest> Requests { get; } = [];

        public Task<ImageGenerationCliFallbackResult> GenerateAsync(
            ImageGenerationCliFallbackRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(handler(request));
        }
    }
}
