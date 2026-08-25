using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.IO;
using GAssetForge.App.Services;
using GAssetForge.Core.Generation;
using GAssetForge.McpServer;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace GAssetForge.SmokeTests;

internal static class GeneratedOutputSubmissionSmokeTests
{
    private const int SourceSize = 1254;
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
            await SubmissionPreservesAuditableStagesAndCompletesIdempotentlyAsync(dataRoot);
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

    private static async Task<TaskFixture> CreateTaskFixtureAsync(
        string dataRoot,
        JsonGenerationTaskStore store)
    {
        var assetRoot = Path.Combine(dataRoot, "Assets");
        Directory.CreateDirectory(assetRoot);
        var batchId = Guid.NewGuid().ToString("N");
        var itemId = Guid.NewGuid().ToString("N");
        var suggestedBaseName = "tilemap-submit-output-v001";
        var workspaceService = new GenerationRunWorkspaceService();
        var run = workspaceService.Prepare(batchId, assetRoot);
        var workspace = workspaceService.CreateOutputPlan(run, itemId, suggestedBaseName);
        var normalizedPath = workspaceService.CreateNormalizedPath(run, itemId, suggestedBaseName);
        var targetPath =
            $"{run.RootRelativePath}/50-final/{itemId}/{suggestedBaseName}.png";
        var outputDirectory = Path.GetDirectoryName(
                targetPath.Replace('/', Path.DirectorySeparatorChar))
            ?.Replace('\\', '/') ?? string.Empty;
        var basePackage = TilemapConstraintPackageBuilder.Build(new TilemapGenerationConstraintInput
        {
            ArtWidth = OutputSize,
            ArtHeight = OutputSize,
            OutputCount = 1,
            ContentPrompt = "中央单一游戏瓦片物体，纯洋红背景",
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
