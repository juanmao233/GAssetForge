using GAssetForge.App.Services;
using GAssetForge.App.Services.AgentRuntime;
using GAssetForge.Core.Generation;
using System.IO;

namespace GAssetForge.SmokeTests;

internal static class GenerationBatchDispatchCoordinatorSmokeTests
{
    public static async Task RunAllAsync()
    {
        AgentPromptDelegatesOnlyImageGenerationAndSingleOutputSubmission();
        RecommendedSourcePathCannotEscapeFrozenAssetRoot();
        RecommendedSourcePathRejectsReparsePointAncestors();
        await DispatchProgressIsMonotonicAndSerializedAsync();
        await LegacyTaskSkipsNewImageDeliveryPreflightAsync();
        await PublishedBatchesRunAsSerializedTurnsAsync();
        await DispatchFailuresAreReportedWithoutRetryLoopsAsync();
        await ActiveDispatchReportsCanceledWhenCoordinatorStopsAsync();
    }

    private static async Task LegacyTaskSkipsNewImageDeliveryPreflightAsync()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "GAssetForge.LegacyDispatchPreflightSmokeTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new JsonGenerationTaskStore(root);
            var task = await store.CreateAsync(new CreateGenerationTaskRequest
            {
                DisplayName = "legacy delivery task",
                RequestedBy = "smoke-tests",
                Constraints = new GenerationConstraintPackage
                {
                    WorkflowId = "legacy-workflow",
                    PreferredSkill = "imagegen",
                    Prompt = "legacy task without delivery workspace",
                    Output = new GenerationOutputSpec
                    {
                        Width = 64,
                        Height = 64,
                        Count = 1,
                        MaxParallelism = 1,
                        Format = "png"
                    }
                }
            });
            var preflight = new FailIfCalledImageDeliveryPreflight();
            string? observedPrompt = null;
            var completion = new TaskCompletionSource<GenerationBatchDispatchCompletedEventArgs>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            await using var coordinator = new GenerationBatchDispatchCoordinator(
                (prompt, _) =>
                {
                    observedPrompt = prompt;
                    return Task.FromResult(new AgentTurnResult(
                        ThreadId: "legacy-thread",
                        TurnId: "legacy-turn",
                        Status: "completed",
                        ErrorMessage: null));
                },
                store,
                preflight);
            coordinator.DispatchCompleted += (_, eventArgs) => completion.TrySetResult(eventArgs);

            True(coordinator.TryEnqueue("legacy-batch", [task.Id], isRetry: false),
                "旧任务应能进入自动调度队列");
            var completed = await completion.Task.WaitAsync(TimeSpan.FromSeconds(3));

            True(completed.IsSuccess, "没有 workspace 的旧任务必须保留原自动调度流程");
            Equal(0, preflight.InvocationCount,
                "旧任务不得调用只适用于 recommendedSourcePath 的新预检");
            True(observedPrompt?.Contains("若 claim 返回的旧任务没有 delivery.workspace", StringComparison.Ordinal) == true,
                "旧任务调度必须保留旧 delivery 合同说明");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static void RecommendedSourcePathCannotEscapeFrozenAssetRoot()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "GAssetForge.GenerationBatchDispatchCoordinatorSmokeTests",
            Guid.NewGuid().ToString("N"));
        var assetRoot = Path.Combine(root, "Assets");
        Directory.CreateDirectory(assetRoot);
        try
        {
            var delivery = new GenerationDeliveryPlan { AssetRoot = assetRoot };
            var valid = GenerationBatchDispatchCoordinator.ResolveRecommendedSourcePath(
                "task-valid-source-path",
                delivery,
                "Generated/GAssetForge/Runs/run/30-model-output/model.png");
            True(AssetRootService.IsWithinRoot(assetRoot, valid),
                "合法 recommendedSourcePath 必须保持在冻结 Assets 根目录内");

            Throws<InvalidDataException>(
                () => GenerationBatchDispatchCoordinator.ResolveRecommendedSourcePath(
                    "task-escaping-source-path",
                    delivery,
                    "../outside/model.png"),
                "损坏任务不得让图片交付预检探测 Assets 根目录之外的位置");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void RecommendedSourcePathRejectsReparsePointAncestors()
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            "GAssetForge.RecommendedSourceReparseSmokeTests",
            Guid.NewGuid().ToString("N"));
        var assetRoot = Path.Combine(testRoot, "Project", "Assets");
        var outside = Path.Combine(testRoot, "Outside");
        var linkedGenerated = Path.Combine(assetRoot, "Generated");
        Directory.CreateDirectory(assetRoot);
        Directory.CreateDirectory(outside);
        try
        {
            try
            {
                Directory.CreateSymbolicLink(linkedGenerated, outside);
            }
            catch (Exception exception) when (exception is
                       UnauthorizedAccessException or
                       PlatformNotSupportedException)
            {
                return;
            }

            Throws<InvalidDataException>(
                () => GenerationBatchDispatchCoordinator.ResolveRecommendedSourcePath(
                    "task-reparse-source-path",
                    new GenerationDeliveryPlan { AssetRoot = assetRoot },
                    "Generated/run/model.png"),
                "recommendedSourcePath 不得经过 Assets 内的符号链接或目录联接");
        }
        finally
        {
            if (Directory.Exists(linkedGenerated))
            {
                Directory.Delete(linkedGenerated);
            }

            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    private static void AgentPromptDelegatesOnlyImageGenerationAndSingleOutputSubmission()
    {
        var prompt = GenerationAgentPromptBuilder.Build(["task-sequential-output"]);

        True(prompt.Contains(
                "一张图片一个任务",
                StringComparison.Ordinal),
            "Agent 指令必须声明单图任务合同");
        True(prompt.Contains(
                "任务间并发和多 Agent 分配交给当前 Agent Host 默认调度",
                StringComparison.Ordinal),
            "Agent 指令必须把跨任务调度交回 Host");
        True(prompt.Contains(
                "claim_generation_task(taskId, agentId)",
                StringComparison.Ordinal),
            "Agent 指令必须先认领冻结任务");
        True(prompt.Contains(
                "按 preferredSkill 调用真实图像生成能力",
                StringComparison.Ordinal),
            "Agent 指令必须只委托真实图像生成能力");
        True(prompt.Contains("recommendedSourcePath", StringComparison.Ordinal),
            "Agent 指令必须使用 claim 返回的项目内绝对推荐源路径");
        True(prompt.Contains(
                "只允许检查本次内置调用明确返回的 sourcePath 或 claim 返回的精确 recommendedSourcePath",
                StringComparison.Ordinal),
            "Agent 指令必须同时允许本次内置 ImageGen 明确返回的路径和项目推荐路径");
        False(prompt.Contains(
                "只允许检查 claim 返回的精确 recommendedSourcePath",
                StringComparison.Ordinal),
            "Agent 指令不得用只允许 recommendedSourcePath 的条款否定本次内置工具的明确 sourcePath");
        True(prompt.Contains(
                "只出现可视图片但 savedPath 为空是协议允许的未落盘状态，不算取得可提交产物",
                StringComparison.Ordinal),
            "内置 ImageGen 只有可视结果时不得误报得到可提交产物");
        True(prompt.Contains(
                "recommendedSourcePath 已存在且是可读的有效 PNG",
                StringComparison.Ordinal) &&
             prompt.Contains("禁止覆盖或使用 --force", StringComparison.Ordinal),
            "断连恢复必须先提交精确现有 PNG，且不得盲目覆盖损坏文件");
        True(prompt.Contains(
                "严禁扫描、按时间挑选或复用 `~/.codex/generated_images`",
                StringComparison.Ordinal),
            "Agent 指令必须禁止从历史 generated_images 猜测本次源文件");
        True(prompt.Contains(
                "run_image_delivery_fallback(taskId, agentId)",
                StringComparison.Ordinal),
            "内置 ImageGen 没有 savedPath 时必须进入软件 Harness 的窄兜底工具");
        True(prompt.Contains(
                "savedPath 为空是协议允许的未落盘状态",
                StringComparison.Ordinal),
            "Agent 指令必须准确识别可视结果没有本地路径的 app-server 合同");
        True(prompt.Contains(
                "从冻结任务读取完整提示、required rules、风格参考、画幅、纯色底和 recommendedSourcePath",
                StringComparison.Ordinal),
            "Harness 兜底必须自行读取全部冻结生成合同");
        True(prompt.Contains(
                "Agent 禁止自行运行 python、py、uv 或脚本",
                StringComparison.Ordinal),
            "Agent 不得继续自行探测旧 PATH 或拼装图片 CLI");
        True(prompt.Contains(
                "存在、可读且具有有效 PNG 签名",
                StringComparison.Ordinal),
            "提交前必须验证推荐源文件确实可读取");
        True(prompt.Contains("required rules", StringComparison.Ordinal) &&
             prompt.Contains("画幅", StringComparison.Ordinal),
            "Harness 指令必须从冻结合同保留 Tilemap 朝向与画幅规则");
        True(prompt.Contains(
                "submit_generation_output(taskId, agentId, outputIndex=0",
                StringComparison.Ordinal),
            "Agent 指令必须提交单一模型原图给软件流水线");
        True(prompt.Contains(
                "GAssetForge 会自行把原图导入项目运行工作区",
                StringComparison.Ordinal),
            "Agent 指令必须说明软件负责中间落盘与后处理");
        True(prompt.Contains(
                "只有内置 imagegen、run_image_delivery_fallback 或 submit_generation_output 返回真实错误时才调用 fail_generation_task",
                StringComparison.Ordinal),
            "Agent 指令必须把失败登记限制在真实生成或提交失败");
        True(prompt.Contains(
                "image_delivery_fallback_failed:",
                StringComparison.Ordinal),
            "Harness 兜底失败时必须报告准确的图片交付错误而不是误报模型失败");
        False(prompt.Contains("output.maxParallelism", StringComparison.Ordinal),
            "Agent 指令不得继续依赖页面并行数量");
        True(prompt.Contains("不要创建或复用固定命名的子 Agent 槽位", StringComparison.Ordinal),
            "Agent 指令必须禁止固定并行槽位路径");
        False(prompt.Contains("/root/tile_slot_", StringComparison.Ordinal),
            "Agent 指令不得再固化曾经失败的子 Agent 路径");
        True(prompt.Contains(
                "不得自行建目录、搬运、缩放、抠图、修改 Alpha",
                StringComparison.Ordinal),
            "Agent 指令必须把可确定的文件处理留给软件");
    }

    private static async Task DispatchProgressIsMonotonicAndSerializedAsync()
    {
        var progress = new List<GenerationBatchDispatchProgressSnapshot>();
        var completionCount = 0;
        var allCompleted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var coordinator = new GenerationBatchDispatchCoordinator(
            async (_, cancellationToken) =>
            {
                await Task.Delay(25, cancellationToken);
                return new AgentTurnResult(
                    ThreadId: "thread-progress-smoke",
                    TurnId: Guid.NewGuid().ToString("N"),
                    Status: "completed",
                    ErrorMessage: null);
            });
        coordinator.DispatchProgressChanged += (_, eventArgs) =>
        {
            lock (progress)
            {
                progress.Add(eventArgs.Snapshot);
            }
        };
        coordinator.DispatchCompleted += (_, _) =>
        {
            if (Interlocked.Increment(ref completionCount) == 2)
            {
                allCompleted.TrySetResult();
            }
        };

        True(coordinator.TryEnqueue("batch-progress-a", ["task-a"], isRetry: false),
            "第一进度批次应进入调度队列");
        True(coordinator.TryEnqueue("batch-progress-b", ["task-b"], isRetry: false),
            "第二进度批次应进入调度队列");
        await allCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        GenerationBatchDispatchProgressSnapshot[] snapshots;
        lock (progress)
        {
            snapshots = progress.ToArray();
        }

        var expectedStages = new[]
        {
            GenerationBatchDispatchStage.BuildingAgentInstruction,
            GenerationBatchDispatchStage.StartingAgentTurn,
            GenerationBatchDispatchStage.WaitingForTaskClaim
        };
        Equal(6, snapshots.Length, "两个批次应各报告三个结构化调度阶段");
        Equal(
            "batch-progress-a,batch-progress-a,batch-progress-a," +
            "batch-progress-b,batch-progress-b,batch-progress-b",
            string.Join(',', snapshots.Select(snapshot => snapshot.Request.BatchId)),
            "串行调度时后一批次不得与前一批次的阶段交错");
        foreach (var batchId in new[] { "batch-progress-a", "batch-progress-b" })
        {
            var batchSnapshots = snapshots
                .Where(snapshot => string.Equals(snapshot.Request.BatchId, batchId, StringComparison.Ordinal))
                .ToArray();
            Equal(
                string.Join(',', expectedStages),
                string.Join(',', batchSnapshots.Select(snapshot => snapshot.Stage)),
                $"批次 {batchId} 的阶段顺序");
            True(batchSnapshots.Zip(batchSnapshots.Skip(1),
                    (current, next) => current.ProgressPercentage <= next.ProgressPercentage)
                .All(isMonotonic => isMonotonic),
                $"批次 {batchId} 的进度百分比必须单调不减");
            True(batchSnapshots.All(snapshot =>
                    snapshot.ProgressPercentage is >= 0d and <= 100d &&
                    !string.IsNullOrWhiteSpace(snapshot.Instruction) &&
                    !string.IsNullOrWhiteSpace(snapshot.Detail)),
                $"批次 {batchId} 的每个阶段都应提供可显示的指令与详情");
        }
    }

    private static async Task PublishedBatchesRunAsSerializedTurnsAsync()
    {
        var prompts = new List<string>();
        var activeTurns = 0;
        var maximumConcurrentTurns = 0;
        var completed = new List<GenerationBatchDispatchCompletedEventArgs>();
        var allCompleted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        await using var coordinator = new GenerationBatchDispatchCoordinator(
            async (prompt, cancellationToken) =>
            {
                var active = Interlocked.Increment(ref activeTurns);
                UpdateMaximum(ref maximumConcurrentTurns, active);
                lock (prompts)
                {
                    prompts.Add(prompt);
                }

                try
                {
                    await Task.Delay(35, cancellationToken);
                    return new AgentTurnResult(
                        ThreadId: "thread-dispatch-smoke",
                        TurnId: $"turn-{prompts.Count}",
                        Status: "completed",
                        ErrorMessage: null);
                }
                finally
                {
                    Interlocked.Decrement(ref activeTurns);
                }
            });
        coordinator.DispatchCompleted += (_, eventArgs) =>
        {
            lock (completed)
            {
                completed.Add(eventArgs);
                if (completed.Count == 3)
                {
                    allCompleted.TrySetResult();
                }
            }
        };

        True(coordinator.TryEnqueue("batch-1", ["task-a"], isRetry: false),
            "第一批次应进入 Agent 调度队列");
        True(coordinator.TryEnqueue("batch-2", ["task-b", "task-c"], isRetry: false),
            "第二批次应进入 Agent 调度队列");
        True(coordinator.TryEnqueue("batch-3", ["task-d"], isRetry: true),
            "重试批次应进入 Agent 调度队列");

        await allCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Equal(1, maximumConcurrentTurns, "同一 Agent 会话的批次 Turn 必须严格串行");
        Equal(3, prompts.Count, "每个已发布批次应生成一个 Agent Turn prompt");
        True(prompts[0].Contains("task-a", StringComparison.Ordinal),
            "第一 Turn prompt 应包含第一批任务");
        True(prompts[1].Contains("task-b", StringComparison.Ordinal) &&
             prompts[1].Contains("task-c", StringComparison.Ordinal),
            "第二 Turn prompt 应包含完整有序任务列表");
        True(prompts[2].Contains("task-d", StringComparison.Ordinal),
            "重试 Turn prompt 应包含新任务 ID");
        True(completed.All(result => result.IsSuccess),
            "正常完成的 Turn 应报告成功调度结果");
        True(completed[2].Request.IsRetry, "重试批次标记必须保留到调度结果");
    }

    private static async Task DispatchFailuresAreReportedWithoutRetryLoopsAsync()
    {
        var invocationCount = 0;
        var progress = new List<GenerationBatchDispatchProgressSnapshot>();
        var completion = new TaskCompletionSource<GenerationBatchDispatchCompletedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var coordinator = new GenerationBatchDispatchCoordinator((_, _) =>
        {
            Interlocked.Increment(ref invocationCount);
            throw new InvalidOperationException("fixture transport unavailable");
        });
        coordinator.DispatchProgressChanged += (_, eventArgs) => progress.Add(eventArgs.Snapshot);
        coordinator.DispatchCompleted += (_, eventArgs) => completion.TrySetResult(eventArgs);

        True(coordinator.TryEnqueue("batch-failure", ["task-pending"], isRetry: false),
            "失败场景批次仍应先进入调度队列");
        var result = await completion.Task.WaitAsync(TimeSpan.FromSeconds(3));
        False(result.IsSuccess, "Transport 异常必须报告调度失败");
        True(result.FailureMessage?.Contains("fixture transport unavailable", StringComparison.Ordinal) == true,
            "调度失败应保留可诊断原因");
        Equal(1, invocationCount, "自动调度失败不得在内存中无限重试");
        var failedProgress = progress.Last();
        Equal(GenerationBatchDispatchStage.Failed, failedProgress.Stage,
            "Transport 启动异常应产生 Failed 调度阶段");
        Equal(GenerationBatchDispatchStage.StartingAgentTurn, failedProgress.FailureStage,
            "同步启动异常应记录 StartingAgentTurn 为失败阶段");
        True(failedProgress.FailureMessage?.Contains(
                "fixture transport unavailable",
                StringComparison.Ordinal) == true,
            "Failed 调度快照应携带可诊断原因");
        True(progress.Zip(progress.Skip(1),
                (current, next) => current.ProgressPercentage <= next.ProgressPercentage)
            .All(isMonotonic => isMonotonic),
            "失败终态不得让已报告的调度百分比回退");
    }

    private static async Task ActiveDispatchReportsCanceledWhenCoordinatorStopsAsync()
    {
        var progress = new List<GenerationBatchDispatchProgressSnapshot>();
        var waitingForClaim = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var completionCount = 0;
        var coordinator = new GenerationBatchDispatchCoordinator(
            async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new AgentTurnResult(
                    ThreadId: null,
                    TurnId: null,
                    Status: "completed",
                    ErrorMessage: null);
            });
        try
        {
            coordinator.DispatchProgressChanged += (_, eventArgs) =>
            {
                progress.Add(eventArgs.Snapshot);
                if (eventArgs.Snapshot.Stage == GenerationBatchDispatchStage.WaitingForTaskClaim)
                {
                    waitingForClaim.TrySetResult();
                }
            };
            coordinator.DispatchCompleted += (_, _) => Interlocked.Increment(ref completionCount);

            True(coordinator.TryEnqueue("batch-canceled", ["task-canceled"], isRetry: false),
                "取消场景批次应进入调度队列");
            await waitingForClaim.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await coordinator.DisposeAsync();

            var canceled = progress.Last();
            Equal(GenerationBatchDispatchStage.Canceled, canceled.Stage,
                "停止协调器时活动批次应报告 Canceled 阶段");
            True(canceled.IsTerminal, "Canceled 快照应为终态");
            True(string.IsNullOrWhiteSpace(canceled.FailureMessage),
                "正常关闭导致的取消不应伪装为调度失败");
            Equal(0, completionCount,
                "因协调器关闭而取消的批次不应再报告普通 DispatchCompleted");
            True(progress.Zip(progress.Skip(1),
                    (current, next) => current.ProgressPercentage <= next.ProgressPercentage)
                .All(isMonotonic => isMonotonic),
                "取消终态不得让已报告的调度百分比回退");
        }
        finally
        {
            await coordinator.DisposeAsync();
        }
    }

    private static void UpdateMaximum(ref int target, int candidate)
    {
        while (true)
        {
            var current = Volatile.Read(ref target);
            if (candidate <= current || Interlocked.CompareExchange(ref target, candidate, current) == current)
            {
                return;
            }
        }
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

    private static void Throws<TException>(Action action, string message)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }

    private sealed class FailIfCalledImageDeliveryPreflight : IImageDeliveryPreflight
    {
        public int InvocationCount { get; private set; }

        public Task<ImageDeliveryPreflightResult> CheckAsync(
            IReadOnlyCollection<string> recommendedSourcePaths,
            CancellationToken cancellationToken)
        {
            InvocationCount++;
            throw new InvalidOperationException("旧任务不应进入 recommendedSourcePath 预检。");
        }
    }
}
