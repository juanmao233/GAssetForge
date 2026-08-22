using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.IO;
using GAssetForge.Core.Generation;
using GAssetForge.McpServer;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace GAssetForge.SmokeTests;

internal static class BackgroundRemovalMcpSmokeTests
{
    private const int ImageWidth = 48;
    private const int ImageHeight = 48;
    private static readonly Rgba32 KeyColor = new(255, 0, 255, 255);

    public static async Task RunAllAsync()
    {
        var dataRoot = Path.Combine(
            Path.GetTempPath(),
            "GAssetForge.BackgroundRemovalMcpSmokeTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataRoot);

        try
        {
            var store = new JsonGenerationTaskStore(Path.Combine(dataRoot, "Generation"));
            await ProcessOutputRejectsInvalidCallerThenCommitsAndSkipsIdempotentlyAsync(dataRoot, store);
            await CleanupFailureAfterCommitRemainsSuccessfulAndDiagnosticAsync(dataRoot, store);
            await CompleteTaskProcessesUnsubmittedStagingAsFallbackAsync(dataRoot, store);
            await CompleteTaskReplacesInvalidExistingTargetFromStagingAsync(dataRoot, store);
            await StagingContractRejectsAlphaAndWrongBorderButAcceptsThresholdAndAntialiasAsync(dataRoot, store);
            await OutOfOrderConcurrentProcessingPreservesIndexMappingAsync(dataRoot, store);
        }
        finally
        {
            if (Directory.Exists(dataRoot))
            {
                Directory.Delete(dataRoot, recursive: true);
            }
        }
    }

    private static async Task CleanupFailureAfterCommitRemainsSuccessfulAndDiagnosticAsync(
        string dataRoot,
        JsonGenerationTaskStore store)
    {
        const string agentId = "codex-background-cleanup-best-effort";
        var fixture = await CreateTaskFixtureAsync(dataRoot, store, "cleanup-best-effort", 1);
        var stagingPath = FullAssetPath(fixture.AssetRoot, fixture.Delivery.StagingPaths[0]);
        var targetPath = FullAssetPath(fixture.AssetRoot, fixture.Delivery.TargetPaths[0]);
        var subject = new Rgba32(205, 80, 35, 255);
        WriteOpaqueStagingFixture(stagingPath, subject);

        var claimed = await InvokeAsync(
            dataRoot,
            "claim_generation_task",
            new { agentId, taskId = fixture.Task.Id });
        Equal(0, claimed.ExitCode, "清理失败回归任务应能被认领");

        var originalAttributes = File.GetAttributes(stagingPath);
        File.SetAttributes(stagingPath, originalAttributes | FileAttributes.ReadOnly);
        try
        {
            var processed = await InvokeAsync(
                dataRoot,
                "process_generation_output",
                new { taskId = fixture.Task.Id, agentId, outputIndex = 0 });
            Equal(0, processed.ExitCode,
                $"最终 target 已提交后 staging 清理失败仍应返回成功：{processed.Output}");
            var processedContent = ReadStructuredContent(processed);
            Equal(true, processedContent.GetProperty("processed").GetBoolean(),
                "清理失败不能否定已经完成的本地剔色和原子提交");
            Equal(false, processedContent.GetProperty("stagingDeleted").GetBoolean(),
                "无法删除只读 staging 时应明确回传 stagingDeleted=false");
            True(
                processedContent.GetProperty("message").GetString()?.Contains("清理失败", StringComparison.Ordinal) == true,
                "清理失败成功响应应携带可诊断信息");
            True(File.Exists(stagingPath), "清理失败时 staging 应保留供稍后重试清理");
            True(File.Exists(targetPath), "清理失败不能回滚已通过校验的最终 target");
            AssertTransparentTarget(targetPath, subject, "清理失败后的最终产物");

            var idempotent = await InvokeAsync(
                dataRoot,
                "process_generation_output",
                new { taskId = fixture.Task.Id, agentId, outputIndex = 0 });
            Equal(0, idempotent.ExitCode,
                $"已有有效 target 时 staging 清理失败也应幂等成功：{idempotent.Output}");
            var idempotentContent = ReadStructuredContent(idempotent);
            Equal(true, idempotentContent.GetProperty("alreadyFinal").GetBoolean(),
                "重复调用应识别已经存在的有效 target");
            Equal(false, idempotentContent.GetProperty("stagingDeleted").GetBoolean(),
                "幂等路径清理失败同样应回传 stagingDeleted=false");
            True(
                idempotentContent.GetProperty("message").GetString()?.Contains("清理失败", StringComparison.Ordinal) == true,
                "幂等路径也必须回传 staging 清理诊断");

            var persisted = await store.GetAsync(fixture.Task.Id)
                ?? throw new InvalidOperationException("清理失败回归任务未持久化。");
            Equal(GenerationTaskStatus.Claimed, persisted.Status,
                "非关键 staging 清理失败不得把已认领任务标记为 Failed");

            var completed = await InvokeAsync(
                dataRoot,
                "complete_generation_task",
                new
                {
                    taskId = fixture.Task.Id,
                    agentId,
                    assetPaths = fixture.Delivery.TargetPaths
                });
            Equal(0, completed.ExitCode,
                $"有效 target 不应因残留只读 staging 阻止任务完成：{completed.Output}");
            persisted = await store.GetAsync(fixture.Task.Id)
                ?? throw new InvalidOperationException("清理失败完成任务未持久化。");
            Equal(GenerationTaskStatus.Completed, persisted.Status,
                "staging 清理失败后任务仍应能进入 Completed，而不是 Failed");
        }
        finally
        {
            if (File.Exists(stagingPath))
            {
                File.SetAttributes(stagingPath, originalAttributes);
            }
        }
    }

    private static async Task ProcessOutputRejectsInvalidCallerThenCommitsAndSkipsIdempotentlyAsync(
        string dataRoot,
        JsonGenerationTaskStore store)
    {
        const string agentId = "codex-background-process";
        var fixture = await CreateTaskFixtureAsync(dataRoot, store, "direct-process", 1);
        var stagingPath = FullAssetPath(fixture.AssetRoot, fixture.Delivery.StagingPaths[0]);
        var targetPath = FullAssetPath(fixture.AssetRoot, fixture.Delivery.TargetPaths[0]);
        WriteOpaqueStagingFixture(stagingPath, new Rgba32(220, 65, 25, 255));

        False(File.Exists(targetPath), "写入纯色 staging 时最终 target 不得提前出现");
        True(File.Exists(stagingPath), "调用本地处理工具前 staging 应真实存在");

        var claimed = await InvokeAsync(
            dataRoot,
            "claim_generation_task",
            new { agentId, taskId = fixture.Task.Id });
        Equal(0, claimed.ExitCode, "背景剔除任务应能被指定 Agent 认领");

        var wrongAgent = await InvokeAsync(
            dataRoot,
            "process_generation_output",
            new { taskId = fixture.Task.Id, agentId = "opencode-wrong-owner", outputIndex = 0 });
        Equal(2, wrongAgent.ExitCode, "非认领 Agent 必须被 process_generation_output 拒绝");
        True(wrongAgent.Output.Contains("Agent", StringComparison.Ordinal),
            "错误 Agent 响应应包含可诊断的所有权信息");
        True(File.Exists(stagingPath), "错误 Agent 调用不得删除 staging");
        False(File.Exists(targetPath), "错误 Agent 调用不得创建最终 target");

        var invalidIndex = await InvokeAsync(
            dataRoot,
            "process_generation_output",
            new { taskId = fixture.Task.Id, agentId, outputIndex = 1 });
        Equal(2, invalidIndex.ExitCode, "越界 outputIndex 必须被 process_generation_output 拒绝");
        True(invalidIndex.Output.Contains("outputIndex", StringComparison.Ordinal),
            "越界索引响应应明确指出 outputIndex");
        True(File.Exists(stagingPath), "越界索引调用不得删除正确索引的 staging");
        False(File.Exists(targetPath), "越界索引调用不得创建其它最终 target");

        var processed = await InvokeAsync(
            dataRoot,
            "process_generation_output",
            new { taskId = fixture.Task.Id, agentId, outputIndex = 0 });
        Equal(0, processed.ExitCode, $"合法 staging 应完成本地背景剔除：{processed.Output}");
        var processedContent = ReadStructuredContent(processed);
        Equal(true, processedContent.GetProperty("processed").GetBoolean(), "首次处理应执行本地剔色");
        Equal(true, processedContent.GetProperty("stagingDeleted").GetBoolean(), "首次处理应删除 staging");
        Equal(fixture.Delivery.TargetPaths[0], processedContent.GetProperty("targetPath").GetString(),
            "process_generation_output 应提交同索引最终路径");
        False(File.Exists(stagingPath), "成功处理后 staging 必须被清理");
        True(File.Exists(targetPath), "成功处理后最终 target 才能出现");
        AssertTransparentTarget(targetPath, new Rgba32(220, 65, 25, 255), "首次本地剔色结果");

        var originalTargetHash = ComputeSha256(targetPath);
        WriteOpaqueStagingFixture(stagingPath, new Rgba32(15, 185, 230, 255));
        var idempotent = await InvokeAsync(
            dataRoot,
            "process_generation_output",
            new { taskId = fixture.Task.Id, agentId, outputIndex = 0 });
        Equal(0, idempotent.ExitCode, "已有有效 target 时重复处理应幂等成功");
        var idempotentContent = ReadStructuredContent(idempotent);
        Equal(false, idempotentContent.GetProperty("processed").GetBoolean(), "幂等处理不得重写有效 target");
        Equal(true, idempotentContent.GetProperty("alreadyFinal").GetBoolean(), "幂等处理应识别已有最终产物");
        Equal(true, idempotentContent.GetProperty("skipped").GetBoolean(), "幂等处理应报告跳过");
        Equal(true, idempotentContent.GetProperty("stagingDeleted").GetBoolean(), "幂等跳过仍应清理残留 staging");
        Equal(originalTargetHash, ComputeSha256(targetPath), "幂等跳过不得改变已有有效 target");
        False(File.Exists(stagingPath), "幂等跳过后残留 staging 必须被删除");
    }

    private static async Task CompleteTaskProcessesUnsubmittedStagingAsFallbackAsync(
        string dataRoot,
        JsonGenerationTaskStore store)
    {
        const string agentId = "codex-background-complete-fallback";
        var fixture = await CreateTaskFixtureAsync(dataRoot, store, "complete-fallback", 1);
        var stagingPath = FullAssetPath(fixture.AssetRoot, fixture.Delivery.StagingPaths[0]);
        var targetPath = FullAssetPath(fixture.AssetRoot, fixture.Delivery.TargetPaths[0]);
        var subject = new Rgba32(35, 190, 80, 255);
        WriteOpaqueStagingFixture(stagingPath, subject);

        var claimed = await InvokeAsync(
            dataRoot,
            "claim_generation_task",
            new { agentId, taskId = fixture.Task.Id });
        Equal(0, claimed.ExitCode, "兜底处理任务应能被认领");
        False(File.Exists(targetPath), "complete_generation_task 前最终 target 不应存在");

        var completed = await InvokeAsync(
            dataRoot,
            "complete_generation_task",
            new
            {
                taskId = fixture.Task.Id,
                agentId,
                assetPaths = fixture.Delivery.TargetPaths
            });
        Equal(0, completed.ExitCode, $"complete_generation_task 应兜底处理未提交 staging：{completed.Output}");
        var completedContent = ReadStructuredContent(completed);
        var fallback = completedContent.GetProperty("fallbackProcessing");
        Equal(1, fallback.GetArrayLength(), "完成调用应报告一项 staging 兜底处理");
        Equal(true, fallback[0].GetProperty("processed").GetBoolean(), "兜底项应真实执行背景剔除");
        Equal(0, fallback[0].GetProperty("outputIndex").GetInt32(), "兜底项应保持原 outputIndex");
        True(File.Exists(targetPath), "兜底完成后最终 target 应出现");
        False(File.Exists(stagingPath), "兜底完成后 staging 应删除");
        AssertTransparentTarget(targetPath, subject, "complete_generation_task 兜底结果");

        var persisted = await store.GetAsync(fixture.Task.Id)
            ?? throw new InvalidOperationException("兜底完成任务未持久化。");
        Equal(GenerationTaskStatus.Completed, persisted.Status, "兜底处理后任务应进入 Completed");
        Equal(fixture.Delivery.TargetPaths[0], persisted.RelativeAssetPaths.Single(),
            "完成任务只能登记最终 target，不能登记 staging");
    }

    private static async Task CompleteTaskReplacesInvalidExistingTargetFromStagingAsync(
        string dataRoot,
        JsonGenerationTaskStore store)
    {
        const string agentId = "codex-background-replace-invalid-target";
        var fixture = await CreateTaskFixtureAsync(dataRoot, store, "replace-invalid-target", 1);
        var stagingPath = FullAssetPath(fixture.AssetRoot, fixture.Delivery.StagingPaths[0]);
        var targetPath = FullAssetPath(fixture.AssetRoot, fixture.Delivery.TargetPaths[0]);
        var subject = new Rgba32(40, 120, 225, 255);
        WriteOpaqueStagingFixture(stagingPath, subject);
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)
            ?? throw new InvalidOperationException("无法解析无效 target 目录。"));
        File.WriteAllText(targetPath, "invalid pre-existing target", Encoding.UTF8);

        var claimed = await InvokeAsync(
            dataRoot,
            "claim_generation_task",
            new { agentId, taskId = fixture.Task.Id });
        Equal(0, claimed.ExitCode, "无效 target 替换任务应能被认领");

        var completed = await InvokeAsync(
            dataRoot,
            "complete_generation_task",
            new
            {
                taskId = fixture.Task.Id,
                agentId,
                assetPaths = fixture.Delivery.TargetPaths
            });
        Equal(0, completed.ExitCode,
            $"存在无效 target 且同索引 staging 有效时，完成兜底应原子替换：{completed.Output}");
        var completedContent = ReadStructuredContent(completed);
        var fallback = completedContent.GetProperty("fallbackProcessing");
        Equal(1, fallback.GetArrayLength(), "无效 target 不得让完成兜底错误跳过 staging");
        Equal(true, fallback[0].GetProperty("processed").GetBoolean(),
            "无效 target 应由 staging 重新执行后处理");
        Equal(true, fallback[0].GetProperty("replacedExistingTarget").GetBoolean(),
            "兜底结果应报告原子替换了无效 target");
        False(File.Exists(stagingPath), "替换无效 target 后 staging 应清理");
        AssertTransparentTarget(targetPath, subject, "替换无效 target 的兜底结果");

        var persisted = await store.GetAsync(fixture.Task.Id)
            ?? throw new InvalidOperationException("无效 target 替换任务未持久化。");
        Equal(GenerationTaskStatus.Completed, persisted.Status,
            "无效 target 被有效 staging 替换后任务应正常 Completed");
    }

    private static async Task StagingContractRejectsAlphaAndWrongBorderButAcceptsThresholdAndAntialiasAsync(
        string dataRoot,
        JsonGenerationTaskStore store)
    {
        const string alphaAgentId = "codex-background-alpha-contract";
        var alphaFixture = await CreateTaskFixtureAsync(dataRoot, store, "reject-source-alpha", 1);
        var alphaStagingPath = FullAssetPath(alphaFixture.AssetRoot, alphaFixture.Delivery.StagingPaths[0]);
        var alphaTargetPath = FullAssetPath(alphaFixture.AssetRoot, alphaFixture.Delivery.TargetPaths[0]);
        WriteOpaqueStagingFixture(alphaStagingPath, new Rgba32(215, 70, 30, 255));
        using (var image = Image.Load<Rgba32>(alphaStagingPath))
        {
            var center = image[ImageWidth / 2, ImageHeight / 2];
            center.A = 254;
            image[ImageWidth / 2, ImageHeight / 2] = center;
            image.Save(alphaStagingPath, new PngEncoder { ColorType = PngColorType.RgbWithAlpha });
        }

        Equal(0, (await InvokeAsync(
            dataRoot,
            "claim_generation_task",
            new { agentId = alphaAgentId, taskId = alphaFixture.Task.Id })).ExitCode,
            "Alpha staging 契约任务应能被认领");
        var alphaRejected = await InvokeAsync(
            dataRoot,
            "process_generation_output",
            new { taskId = alphaFixture.Task.Id, agentId = alphaAgentId, outputIndex = 0 });
        Equal(2, alphaRejected.ExitCode, "第一阶段 staging 含任意非 255 Alpha 都必须拒绝");
        True(alphaRejected.Output.Contains("Alpha=255", StringComparison.Ordinal),
            "Alpha 拒绝响应应明确第一阶段必须完全不透明");
        True(File.Exists(alphaStagingPath), "staging 契约拒绝不得删除原始输入");
        False(File.Exists(alphaTargetPath), "staging 契约拒绝不得提交最终 target");

        const string borderAgentId = "codex-background-border-contract";
        var borderFixture = await CreateTaskFixtureAsync(
            dataRoot,
            store,
            "reject-wrong-border",
            1,
            transparentThreshold: 12d,
            opaqueThreshold: 40d);
        var borderStagingPath = FullAssetPath(borderFixture.AssetRoot, borderFixture.Delivery.StagingPaths[0]);
        var borderTargetPath = FullAssetPath(borderFixture.AssetRoot, borderFixture.Delivery.TargetPaths[0]);
        WriteOpaqueStagingFixture(borderStagingPath, new Rgba32(35, 180, 80, 255));
        using (var image = Image.Load<Rgba32>(borderStagingPath))
        {
            for (var x = 4; x < image.Width - 4; x++)
            {
                image[x, 0] = new Rgba32(0, 255, 0, 255);
            }

            image.Save(borderStagingPath, new PngEncoder { ColorType = PngColorType.RgbWithAlpha });
        }

        Equal(0, (await InvokeAsync(
            dataRoot,
            "claim_generation_task",
            new { agentId = borderAgentId, taskId = borderFixture.Task.Id })).ExitCode,
            "边缘底色 staging 契约任务应能被认领");
        var borderRejected = await InvokeAsync(
            dataRoot,
            "process_generation_output",
            new { taskId = borderFixture.Task.Id, agentId = borderAgentId, outputIndex = 0 });
        Equal(2, borderRejected.ExitCode, "画布边缘明显偏离冻结底色时必须拒绝");
        var borderErrorText = ReadErrorText(borderRejected);
        True(borderErrorText.Contains("画布", StringComparison.Ordinal) &&
             borderErrorText.Contains("#FF00FF", StringComparison.Ordinal),
            "边缘拒绝响应应包含画布位置与冻结 ColorHex 诊断");
        True(File.Exists(borderStagingPath), "边缘底色拒绝不得删除 staging");
        False(File.Exists(borderTargetPath), "边缘底色拒绝不得提交 target");

        const string acceptedAgentId = "codex-background-threshold-antialias";
        var acceptedFixture = await CreateTaskFixtureAsync(
            dataRoot,
            store,
            "accept-threshold-antialias",
            1,
            transparentThreshold: 12d,
            opaqueThreshold: 40d);
        var acceptedStagingPath = FullAssetPath(acceptedFixture.AssetRoot, acceptedFixture.Delivery.StagingPaths[0]);
        var acceptedTargetPath = FullAssetPath(acceptedFixture.AssetRoot, acceptedFixture.Delivery.TargetPaths[0]);
        var acceptedSubject = new Rgba32(40, 150, 225, 255);
        WriteOpaqueStagingFixture(
            acceptedStagingPath,
            acceptedSubject,
            backgroundColor: new Rgba32(250, 5, 250, 255),
            includeAntialiasedSubject: true);

        Equal(0, (await InvokeAsync(
            dataRoot,
            "claim_generation_task",
            new { agentId = acceptedAgentId, taskId = acceptedFixture.Task.Id })).ExitCode,
            "阈值及抗锯齿 staging 契约任务应能被认领");
        var accepted = await InvokeAsync(
            dataRoot,
            "process_generation_output",
            new { taskId = acceptedFixture.Task.Id, agentId = acceptedAgentId, outputIndex = 0 });
        Equal(0, accepted.ExitCode,
            $"边缘颜色位于透明阈值内且主体含抗锯齿时不应误拒：{accepted.Output}");
        False(File.Exists(acceptedStagingPath), "合法阈值 staging 应在提交后清理");
        AssertTransparentTarget(acceptedTargetPath, acceptedSubject, "阈值与抗锯齿合法 staging");
    }

    private static async Task OutOfOrderConcurrentProcessingPreservesIndexMappingAsync(
        string dataRoot,
        JsonGenerationTaskStore store)
    {
        const string agentId = "codex-background-parallel";
        var fixture = await CreateTaskFixtureAsync(dataRoot, store, "parallel-index", 3);
        Rgba32[] subjects =
        [
            new Rgba32(235, 45, 30, 255),
            new Rgba32(40, 210, 70, 255),
            new Rgba32(30, 95, 235, 255)
        ];
        for (var index = 0; index < subjects.Length; index++)
        {
            WriteOpaqueStagingFixture(
                FullAssetPath(fixture.AssetRoot, fixture.Delivery.StagingPaths[index]),
                subjects[index]);
        }

        var claimed = await InvokeAsync(
            dataRoot,
            "claim_generation_task",
            new { agentId, taskId = fixture.Task.Id });
        Equal(0, claimed.ExitCode, "并行索引映射任务应能被认领");

        int[] requestOrder = [2, 0, 1];
        var calls = requestOrder.Select(async (outputIndex, order) =>
        {
            await Task.Delay(order * 20);
            var result = await InvokeAsync(
                dataRoot,
                "process_generation_output",
                new { taskId = fixture.Task.Id, agentId, outputIndex });
            return (OutputIndex: outputIndex, Result: result);
        });
        var results = await Task.WhenAll(calls);

        foreach (var item in results)
        {
            Equal(0, item.Result.ExitCode,
                $"乱序并行处理索引 {item.OutputIndex} 应成功：{item.Result.Output}");
            var content = ReadStructuredContent(item.Result);
            Equal(item.OutputIndex, content.GetProperty("outputIndex").GetInt32(),
                "process_generation_output 回传索引必须与请求一致");
            Equal(fixture.Delivery.StagingPaths[item.OutputIndex], content.GetProperty("stagingPath").GetString(),
                "乱序处理不得串换 stagingPath");
            Equal(fixture.Delivery.TargetPaths[item.OutputIndex], content.GetProperty("targetPath").GetString(),
                "乱序处理不得串换 targetPath");
        }

        for (var index = 0; index < subjects.Length; index++)
        {
            var stagingPath = FullAssetPath(fixture.AssetRoot, fixture.Delivery.StagingPaths[index]);
            var targetPath = FullAssetPath(fixture.AssetRoot, fixture.Delivery.TargetPaths[index]);
            False(File.Exists(stagingPath), $"索引 {index} 的 staging 应被独立清理");
            True(File.Exists(targetPath), $"索引 {index} 的 target 应按冻结映射出现");
            AssertTransparentTarget(targetPath, subjects[index], $"乱序并行索引 {index}");
        }

        var completed = await InvokeAsync(
            dataRoot,
            "complete_generation_task",
            new
            {
                taskId = fixture.Task.Id,
                agentId,
                assetPaths = fixture.Delivery.TargetPaths
            });
        Equal(0, completed.ExitCode, "乱序处理完成后应按冻结 target 顺序完成任务");
        var persisted = await store.GetAsync(fixture.Task.Id)
            ?? throw new InvalidOperationException("并行映射完成任务未持久化。");
        Equal(
            fixture.Delivery.TargetPaths.Length,
            persisted.RelativeAssetPaths.Length,
            "并行任务应登记全部最终产物");
        for (var index = 0; index < fixture.Delivery.TargetPaths.Length; index++)
        {
            Equal(fixture.Delivery.TargetPaths[index], persisted.RelativeAssetPaths[index],
                $"最终登记顺序应保持冻结 targetPaths 索引 {index}");
        }
    }

    private static async Task<TaskFixture> CreateTaskFixtureAsync(
        string dataRoot,
        JsonGenerationTaskStore store,
        string name,
        int outputCount,
        double transparentThreshold = 0d,
        double opaqueThreshold = 1d)
    {
        var assetRoot = Path.Combine(dataRoot, "Assets");
        Directory.CreateDirectory(assetRoot);
        var outputDirectory = $"Generated/BackgroundRemovalMcp/{name}";
        var targetPaths = Enumerable.Range(1, outputCount)
            .Select(index => $"{outputDirectory}/{name}-{index:000}.png")
            .ToArray();
        var basePackage = TilemapConstraintPackageBuilder.Build(new TilemapGenerationConstraintInput
        {
            ArtWidth = ImageWidth,
            ArtHeight = ImageHeight,
            OutputCount = outputCount,
            ContentPrompt = $"{name} 背景剔除 MCP 回归瓦片",
            BackgroundRemoval = new GenerationBackgroundRemovalSpec
            {
                Preset = "magenta",
                ColorHex = "#FF00FF",
                TransparentThreshold = transparentThreshold,
                OpaqueThreshold = opaqueThreshold,
                EdgeContract = 0,
                EdgeFeather = 0,
                Despill = false,
                EdgeConnectedOnly = true
            }
        });
        basePackage = basePackage with
        {
            Output = basePackage.Output with
            {
                MaxParallelism = outputCount
            }
        };
        var constraints = MaterialGenerationConstraintComposer.Compose(
            basePackage,
            new MaterialGenerationStyleSelection
            {
                GroupId = $"background-removal-mcp-{name}",
                Name = $"背景剔除 MCP {name}",
                Description = "用于验证本地后处理工具的隔离风格快照。",
                Prompt = "clean readable game asset"
            },
            new GenerationDeliveryPlan
            {
                AssetRoot = assetRoot,
                OutputDirectory = outputDirectory,
                TargetPaths = targetPaths,
                SuggestedBaseName = name
            });
        var task = await store.CreateAsync(new CreateGenerationTaskRequest
        {
            DisplayName = $"背景剔除 MCP {name}",
            RequestedBy = "smoke-tests",
            Constraints = constraints
        });
        True(
            MaterialGenerationConstraintComposer.TryReadDelivery(task.Constraints, out var delivery),
            "背景剔除 MCP 测试任务应包含有效 delivery");
        return new TaskFixture(
            task,
            delivery ?? throw new InvalidOperationException("背景剔除 MCP 测试 delivery 缺失。"),
            assetRoot);
    }

    private static void WriteOpaqueStagingFixture(
        string path,
        Rgba32 subjectColor,
        Rgba32? backgroundColor = null,
        bool includeAntialiasedSubject = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("无法解析 staging 目录。"));
        using var image = new Image<Rgba32>(ImageWidth, ImageHeight, backgroundColor ?? KeyColor);
        var centerX = ImageWidth / 2;
        var centerY = ImageHeight / 2;
        if (includeAntialiasedSubject)
        {
            var antialiasColor = new Rgba32(
                (byte)((subjectColor.R + KeyColor.R) / 2),
                (byte)((subjectColor.G + KeyColor.G) / 2),
                (byte)((subjectColor.B + KeyColor.B) / 2),
                255);
            for (var y = centerY - 4; y <= centerY + 4; y++)
            {
                for (var x = centerX - 4; x <= centerX + 4; x++)
                {
                    image[x, y] = antialiasColor;
                }
            }
        }

        for (var y = centerY - 3; y <= centerY + 3; y++)
        {
            for (var x = centerX - 3; x <= centerX + 3; x++)
            {
                image[x, y] = subjectColor;
            }
        }

        image.Save(path, new PngEncoder
        {
            ColorType = PngColorType.RgbWithAlpha
        });
    }

    private static void AssertTransparentTarget(string path, Rgba32 expectedSubject, string message)
    {
        using var image = Image.Load<Rgba32>(path);
        Equal(ImageWidth, image.Width, $"{message}宽度");
        Equal(ImageHeight, image.Height, $"{message}高度");
        Equal((byte)0, image[0, 0].A, $"{message}画布边缘应为透明 Alpha");
        var center = image[ImageWidth / 2, ImageHeight / 2];
        Equal((byte)255, center.A, $"{message}主体中心应保持不透明");
        Equal(expectedSubject.R, center.R, $"{message}主体中心 R");
        Equal(expectedSubject.G, center.G, $"{message}主体中心 G");
        Equal(expectedSubject.B, center.B, $"{message}主体中心 B");
    }

    private static JsonElement ReadStructuredContent(DirectToolInvocationResult result)
    {
        using var document = JsonDocument.Parse(result.Output);
        return document.RootElement.GetProperty("structuredContent").Clone();
    }

    private static string ReadErrorText(DirectToolInvocationResult result)
    {
        using var document = JsonDocument.Parse(result.Output);
        return document.RootElement
            .GetProperty("content")[0]
            .GetProperty("text")
            .GetString() ?? string.Empty;
    }

    private static string FullAssetPath(string assetRoot, string relativePath) => Path.GetFullPath(
        Path.Combine(assetRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private static string ComputeSha256(string path) => Convert.ToHexString(
        SHA256.HashData(File.ReadAllBytes(path)));

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
            Encode(arguments)
        ]);

    private static string Encode(object value) => Convert.ToBase64String(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value)));

    private static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void False(bool condition, string message)
    {
        if (condition)
        {
            throw new InvalidOperationException(message);
        }
    }

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
}
