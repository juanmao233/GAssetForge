using GAssetForge.App.Services;
using GAssetForge.App.Services.AgentRuntime;

namespace GAssetForge.SmokeTests;

internal static class GenerationBatchDispatchCoordinatorSmokeTests
{
    public static async Task RunAllAsync()
    {
        AgentPromptDelegatesOnlyImageGenerationAndSingleOutputSubmission();
        await DispatchProgressIsMonotonicAndSerializedAsync();
        await PublishedBatchesRunAsSerializedTurnsAsync();
        await DispatchFailuresAreReportedWithoutRetryLoopsAsync();
        await ActiveDispatchReportsCanceledWhenCoordinatorStopsAsync();
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
        True(prompt.Contains("axisA/axisB", StringComparison.Ordinal) &&
             prompt.Contains("不得把控制角 90°、旋转 0°改写成 diamond/rhombus", StringComparison.Ordinal),
            "Agent 指令必须原样保留冻结的 Tilemap 朝向合同，禁止二次翻译为 45° 钻石形");
        True(prompt.Contains(
                "submit_generation_output(taskId, agentId, outputIndex=0",
                StringComparison.Ordinal),
            "Agent 指令必须提交单一模型原图给软件流水线");
        True(prompt.Contains(
                "GAssetForge 会自行把原图导入项目运行工作区",
                StringComparison.Ordinal),
            "Agent 指令必须说明软件负责中间落盘与后处理");
        True(prompt.Contains(
                "只有 imagegen 或 submit_generation_output 真实失败时才调用 fail_generation_task",
                StringComparison.Ordinal),
            "Agent 指令必须把失败登记限制在真实生成或提交失败");
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
}
