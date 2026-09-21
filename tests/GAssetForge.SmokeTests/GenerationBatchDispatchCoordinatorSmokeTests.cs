using GAssetForge.App.Services;
using GAssetForge.App.Services.AgentRuntime;
using GAssetForge.App.ViewModels;
using GAssetForge.Core.Generation;
using System.IO;

namespace GAssetForge.SmokeTests;

internal static class GenerationBatchDispatchCoordinatorSmokeTests
{
    public static async Task RunAllAsync()
    {
        await FaultedSubmissionNeverReportsAcceptedTurnAsync();
        await ReadinessFailuresKeepStructuredCauseAcrossPreparationAndSubmissionAsync();
        await TurnAcknowledgementRejectsStaleEventsAndDistinguishesExecutionFailureAsync();
        AgentPromptDelegatesOnlyImageGenerationAndSingleOutputSubmission();
        RecommendedSourcePathCannotEscapeFrozenAssetRoot();
        RecommendedSourcePathRejectsReparsePointAncestors();
        PendingRecoveryOnlyRunsOnConnectedRisingEdge();
        await EnqueueResultsDistinguishNewDuplicateAndStoppedCoordinatorAsync();
        await BatchReleaseWaitersTrackEachSchedulingLifecycleAsync();
        await DisposeCompletesBatchReleaseWaitersAsync();
        await TargetedCancellationStopsOnlyCapturedLifecyclesAsync();
        await TargetedCancellationReachesBatchPreparationAsync();
        await ManagedCodexClaimResumesIdempotentlyAndForeignClaimIsNotStolenAsync();
        await DispatchProgressIsMonotonicAndSerializedAsync();
        await LegacyTaskSkipsNewImageDeliveryPreflightAsync();
        await PublishedBatchesRunAsSerializedTurnsAsync();
        await LargeBatchUsesFiveFiveTwoTurnsWithSinglePreparationAsync();
        await FailedChunkStopsLaterTurnsAsync();
        await DispatchFailuresAreReportedWithoutRetryLoopsAsync();
        await ActiveDispatchReportsCanceledWhenCoordinatorStopsAsync();
    }

    private static async Task FaultedSubmissionNeverReportsAcceptedTurnAsync()
    {
        var progress = new List<GenerationBatchDispatchProgressSnapshot>();
        var completion = new TaskCompletionSource<GenerationBatchDispatchCompletedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var original = new InvalidOperationException(
            "Agent 会话尚未达到可执行状态。请先启动会话并完成认证与 MCP 工具初始化。");
        await using var coordinator = new GenerationBatchDispatchCoordinator((_, _) =>
            Task.FromException<AgentTurnResult>(original));
        coordinator.DispatchProgressChanged += (_, e) => progress.Add(e.Snapshot);
        coordinator.DispatchCompleted += (_, e) => completion.TrySetResult(e);
        coordinator.TryEnqueue("batch-unready-replay", ["task-original-pending"], isRetry: false);
        var result = await completion.Task.WaitAsync(TimeSpan.FromSeconds(3));

        False(progress.Any(snapshot => snapshot.Stage == GenerationBatchDispatchStage.WaitingForTaskClaim),
            "启动入口返回 faulted Task 时不得出现已提交/等待领取的假阶段");
        Equal(GenerationBatchDispatchStage.StartingAgentTurn, progress.Last().FailureStage,
            "未成功启动的 Turn 必须停留在提交阶段，不得把等待领取当作失败点");
        True(result.FailureMessage?.Contains(original.Message, StringComparison.Ordinal) == true,
            "调度拒绝必须保留原始原因");
        True(result.Result is null, "提交失败不能伪造 Agent Turn 结果");
        True(ReferenceEquals(original, result.Diagnostic?.Exception),
            "未知启动错误必须保留原始 Exception 实例供结构化日志记录");
        Equal("agent_turn_submission_failed", result.Diagnostic?.Code,
            "未知提交错误必须有稳定错误码，不能把界面进度阶段当成错误码");
        Equal(result.Diagnostic?.Code, progress.Last().FailureCode,
            "进度和完成事件必须共享同一稳定错误码");
    }

    private static async Task ReadinessFailuresKeepStructuredCauseAcrossPreparationAndSubmissionAsync()
    {
        foreach (var duringPreparation in new[] { true, false })
        {
            var progress = new List<GenerationBatchDispatchProgressSnapshot>();
            var completion = new TaskCompletionSource<GenerationBatchDispatchCompletedEventArgs>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var original = new AgentReadinessException(
                "agent_mcp_initialization_timeout", "mcp_readiness", "fixture 初始化超时",
                "thread-new", "epoch=2; gasset_forge=starting", "等待工具初始化完成后重试原任务。");
            var runCount = 0;
            await using var coordinator = new GenerationBatchDispatchCoordinator((_, _) =>
            {
                Interlocked.Increment(ref runCount);
                return Task.FromException<AgentTurnResult>(original);
            }, prepareBatch: duringPreparation ? (_, _) => Task.FromException(original) : null);
            coordinator.DispatchProgressChanged += (_, e) => progress.Add(e.Snapshot);
            coordinator.DispatchCompleted += (_, e) => completion.TrySetResult(e);
            coordinator.TryEnqueue("batch-readiness-replay", ["task-original-pending"], isRetry: false);
            var result = await completion.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var diagnostic = result.Diagnostic;
            True(diagnostic is not null, "就绪失败必须携带结构化诊断");
            Equal(original.Code, diagnostic!.Code, "typed 错误码不得退化为进度枚举");
            Equal(original.Stage, diagnostic.Stage, "typed 失败阶段必须完整保留");
            Equal(original.ThreadId, diagnostic.ThreadId, "必须保留实际失败线程关联信息");
            Equal(original.StateSummary, diagnostic.ReadinessState, "必须保留初始化状态快照");
            Equal(original.OccurredAt, diagnostic.OccurredAt, "记录原始发生时间而非 UI 消息处理时间");
            Equal(original.OccurredAt, progress.Last().OccurredAt, "最终进度和诊断使用同一发生时间");
            True(ReferenceEquals(original, diagnostic.Exception), "原始异常对象不能在协调器被丢弃");
            False(diagnostic.TurnStarted, "就绪拒绝不得伪报 Turn 已开始");
            False(progress.Any(snapshot => snapshot.Stage == GenerationBatchDispatchStage.WaitingForTaskClaim),
                "准备或提交时被拒绝均不得制造等待认领阶段");
            Equal(duringPreparation ? 0 : 1, runCount, "准备失败不得调用 RunTurn 或自动重试");
        }
    }

    private static async Task TurnAcknowledgementRejectsStaleEventsAndDistinguishesExecutionFailureAsync()
    {
        var progress = new List<GenerationBatchDispatchProgressSnapshot>();
        var completion = new TaskCompletionSource<GenerationBatchDispatchCompletedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var original = new IOException("fixture response pipe broke after accepted Turn");
        GenerationBatchDispatchCoordinator? coordinator = null;
        await using var ownedCoordinator = coordinator = new GenerationBatchDispatchCoordinator(async (_, _) =>
        {
            False(coordinator!.NotifyTurnStarted("thread-old", "turn-old"),
                "旧线程的迟到 TurnStarted 不得推进当前任务");
            True(coordinator.NotifyTurnStarted("thread-current", "turn-current"),
                "只有当前线程真实的启动通知可推进等待领取阶段");
            False(coordinator.NotifyTurnStarted("thread-current", "turn-current"),
                "重复的启动通知只能处理一次");
            await Task.Yield();
            throw original;
        }, currentThreadIdProvider: () => "thread-current");
        coordinator.DispatchProgressChanged += (_, e) => progress.Add(e.Snapshot);
        coordinator.DispatchCompleted += (_, e) => completion.TrySetResult(e);
        coordinator.TryEnqueue("batch-accepted", ["task-original"], isRetry: false);
        var result = await completion.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Equal(1, progress.Count(snapshot => snapshot.Stage == GenerationBatchDispatchStage.WaitingForTaskClaim),
            "仅真实且匹配的启动确认生成一次等待认领进度");
        Equal("agent_turn_execution_failed", result.Diagnostic?.Code,
            "已收到启动确认后的异常应分类为执行错误，不是提交错误");
        Equal("turn-current", result.Diagnostic?.TurnId, "执行失败必须保留实际 Turn ID");
        True(result.Diagnostic?.TurnStarted == true, "实际启动事实必须传递到收束层");
        True(ReferenceEquals(original, result.Diagnostic?.Exception), "执行异常需保留堆栈源实例");
        False(coordinator.NotifyTurnStarted("thread-current", "turn-current"),
            "已结束生命周期不得再被迟到通知推进");
    }

    private static void PendingRecoveryOnlyRunsOnConnectedRisingEdge()
    {
        var wasConnected = false;
        False(
            ShellViewModel.ShouldRecoverPendingDispatches(
                ref wasConnected,
                propertyName: null,
                isConnected: true),
            "空 PropertyChanged 通知不得触发 Pending 恢复扫描");
        False(wasConnected,
            "非 IsConnected 通知不得改写连接边沿状态");
        True(
            ShellViewModel.ShouldRecoverPendingDispatches(
                ref wasConnected,
                nameof(AgentConnectionViewModel.IsConnected),
                isConnected: true),
            "首次 false→true 必须触发一次 Pending 恢复扫描");
        False(
            ShellViewModel.ShouldRecoverPendingDispatches(
                ref wasConnected,
                nameof(AgentConnectionViewModel.IsConnected),
                isConnected: true),
            "每秒重复的 IsConnected=true 通知不得重复扫描持久化批次");
        False(
            ShellViewModel.ShouldRecoverPendingDispatches(
                ref wasConnected,
                nameof(AgentConnectionViewModel.IsConnected),
                isConnected: false),
            "true→false 只更新边沿状态，不应在断线时恢复调度");
        True(
            ShellViewModel.ShouldRecoverPendingDispatches(
                ref wasConnected,
                nameof(AgentConnectionViewModel.IsConnected),
                isConnected: true),
            "重新连接后的下一次 false→true 应允许恢复原 Pending 任务");
    }

    private static async Task EnqueueResultsDistinguishNewDuplicateAndStoppedCoordinatorAsync()
    {
        var turnStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new GenerationBatchDispatchCoordinator(
            async (_, cancellationToken) =>
            {
                turnStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new AgentTurnResult(
                    ThreadId: null,
                    TurnId: null,
                    Status: "completed",
                    ErrorMessage: null);
            });
        try
        {
            Equal(
                GenerationBatchDispatchEnqueueResult.Enqueued,
                coordinator.TryEnqueue("pending-recovery", ["task-a"], isRetry: false),
                "首次 Pending 恢复扫描应真实进入调度队列");
            Equal(
                GenerationBatchDispatchEnqueueResult.AlreadyScheduled,
                coordinator.TryEnqueue("pending-recovery", ["task-a-duplicate"], isRetry: false),
                "同一 Pending 批次重复扫描应明确报告已在调度生命周期内");
            await turnStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await coordinator.DisposeAsync();
            Equal(
                GenerationBatchDispatchEnqueueResult.QueueStopped,
                coordinator.TryEnqueue("after-stop", ["task-b"], isRetry: false),
                "协调器停止后应返回 QueueStopped 而不是抛出或伪装成幂等成功");
        }
        finally
        {
            await coordinator.DisposeAsync();
        }
    }

    private static async Task BatchReleaseWaitersTrackEachSchedulingLifecycleAsync()
    {
        await using var coordinator = new GenerationBatchDispatchCoordinator((_, _) =>
            Task.FromResult(new AgentTurnResult(
                ThreadId: "thread-release-waiter",
                TurnId: "turn-release-waiter",
                Status: "completed",
                ErrorMessage: null)));

        const string batchId = "batch-release-waiter";
        Equal(
            GenerationBatchDispatchEnqueueResult.Enqueued,
            coordinator.TryEnqueue(batchId, ["task-release-first"], isRetry: false),
            "首次批次生命周期应进入调度队列");
        var firstRelease = coordinator.WaitForBatchReleasedAsync(batchId);
        False(firstRelease.IsCompleted,
            "ReleaseBatch 前批次释放等待必须保持阻塞");
        Equal(
            GenerationBatchDispatchEnqueueResult.AlreadyScheduled,
            coordinator.TryEnqueue(batchId, ["task-release-first-duplicate"], isRetry: false),
            "释放前重复续派同一批次必须保持幂等");

        coordinator.ReleaseBatch(batchId);
        await firstRelease.WaitAsync(TimeSpan.FromSeconds(3));
        True(firstRelease.IsCompletedSuccessfully,
            "ReleaseBatch 后当前批次生命周期的等待者必须完成");

        Equal(
            GenerationBatchDispatchEnqueueResult.Enqueued,
            coordinator.TryEnqueue(batchId, ["task-release-second"], isRetry: false),
            "释放后应允许同一 batchId 建立新的调度生命周期");
        var secondRelease = coordinator.WaitForBatchReleasedAsync(batchId);
        False(secondRelease.IsCompleted,
            "续派后的新批次生命周期必须使用新的未完成释放信号");
        False(ReferenceEquals(firstRelease, secondRelease),
            "同一 batchId 的不同调度生命周期不得复用已完成释放信号");
        Equal(
            GenerationBatchDispatchEnqueueResult.AlreadyScheduled,
            coordinator.TryEnqueue(batchId, ["task-release-second-duplicate"], isRetry: false),
            "续派生命周期内再次点击恢复仍只能保留一个调度请求");

        coordinator.ReleaseBatch(batchId);
        await secondRelease.WaitAsync(TimeSpan.FromSeconds(3));
        True(secondRelease.IsCompletedSuccessfully,
            "第二次 ReleaseBatch 必须只完成续派生命周期的等待者");
    }

    private static async Task DisposeCompletesBatchReleaseWaitersAsync()
    {
        var turnStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new GenerationBatchDispatchCoordinator(
            async (_, cancellationToken) =>
            {
                turnStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new AgentTurnResult(
                    ThreadId: null,
                    TurnId: null,
                    Status: "completed",
                    ErrorMessage: null);
            });
        try
        {
            Equal(
                GenerationBatchDispatchEnqueueResult.Enqueued,
                coordinator.TryEnqueue("batch-release-on-dispose", ["task-dispose"], isRetry: false),
                "关闭等待测试批次应进入调度队列");
            await turnStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var release = coordinator.WaitForBatchReleasedAsync("batch-release-on-dispose");
            False(release.IsCompleted,
                "协调器关闭前活动批次的释放等待不得提前完成");

            await coordinator.DisposeAsync();
            await release.WaitAsync(TimeSpan.FromSeconds(3));
            True(release.IsCompletedSuccessfully,
                "DisposeAsync 必须结束所有仍在等待的批次释放任务");
        }
        finally
        {
            await coordinator.DisposeAsync();
        }
    }

    private static async Task TargetedCancellationStopsOnlyCapturedLifecyclesAsync()
    {
        var activeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var activeCanceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowActiveExit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var remainingCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new List<string>();
        var invoked = new List<string>();
        await using var coordinator = new GenerationBatchDispatchCoordinator(async (prompt, token) =>
        {
            var taskId = ReadPromptTaskIds(prompt).Single();
            lock (invoked)
            {
                invoked.Add(taskId);
            }

            if (taskId == "target-active-old")
            {
                activeStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    activeCanceled.TrySetResult();
                    await allowActiveExit.Task;
                    throw;
                }
            }

            token.ThrowIfCancellationRequested();
            return new AgentTurnResult("targeted-thread", taskId, "completed", null);
        });
        coordinator.DispatchCompleted += (_, e) =>
        {
            lock (completed)
            {
                completed.Add(e.Request.TaskIds.Single());
                if (completed.Count == 3)
                {
                    remainingCompleted.TrySetResult();
                }
            }
        };

        coordinator.TryEnqueue("target-active", ["target-active-old"], isRetry: false);
        await activeStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var oldActiveRelease = coordinator.WaitForBatchReleasedAsync("target-active");
        coordinator.TryEnqueue("target-queued", ["target-queued-old"], isRetry: false);
        var oldQueuedRelease = coordinator.WaitForBatchReleasedAsync("target-queued");
        coordinator.TryEnqueue("target-unrelated", ["target-unrelated-task"], isRetry: false);

        // A queued cancellation must not wait for an unrelated active Agent turn to finish.
        await coordinator.CancelBatchesAsync(["target-queued"]).WaitAsync(TimeSpan.FromSeconds(3));
        True(oldQueuedRelease.IsCompletedSuccessfully,
            "定向取消排队批次必须释放该生命周期的等待信号");
        False(activeCanceled.Task.IsCompleted,
            "取消排队批次不得中断其它活动 Turn");
        Equal(GenerationBatchDispatchEnqueueResult.Enqueued,
            coordinator.TryEnqueue("target-queued", ["target-queued-new"], isRetry: true),
            "排队批次取消后，同一 batchId 应可重新入队");

        var cancelActive = coordinator.CancelBatchesAsync(["target-active", "missing-batch"]);
        await activeCanceled.Task.WaitAsync(TimeSpan.FromSeconds(3));
        False(cancelActive.IsCompleted,
            "停止活动批次必须等待 runtime 真正退出，不能在仅收到 token 时提前完成");
        True(oldActiveRelease.IsCompletedSuccessfully,
            "停止活动批次必须释放旧调度生命周期信号");
        coordinator.ReleaseBatch("target-active");
        Equal(GenerationBatchDispatchEnqueueResult.Enqueued,
            coordinator.TryEnqueue("target-active", ["target-active-new"], isRetry: true),
            "取消期间释放再重入同 id 必须获得独立的新生命周期");
        var newActiveRelease = coordinator.WaitForBatchReleasedAsync("target-active");

        allowActiveExit.TrySetResult();
        await cancelActive.WaitAsync(TimeSpan.FromSeconds(3));
        await remainingCompleted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        False(newActiveRelease.IsCompleted,
            "旧 Turn 退出不得释放同 id 新生命周期的信号");
        Equal(GenerationBatchDispatchEnqueueResult.AlreadyScheduled,
            coordinator.TryEnqueue("target-active", ["duplicate-new"], isRetry: false),
            "旧 Turn 的清理不得移除同 id 新生命周期的幂等门禁");
        lock (completed)
        {
            Equal("target-unrelated-task,target-queued-new,target-active-new", string.Join(',', completed),
                "取消不得发送普通完成事件；无关批次和重新入队的新生命周期应正常串行完成");
        }

        lock (invoked)
        {
            False(invoked.Contains("target-queued-old", StringComparer.Ordinal),
                "Channel 中遗留的已取消排队项必须跳过，不能被同 id 新任务复活");
        }
    }

    private static async Task TargetedCancellationReachesBatchPreparationAsync()
    {
        var preparationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var preparationCanceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowPreparationExit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var nextCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtimeCalls = 0;
        await using var coordinator = new GenerationBatchDispatchCoordinator(
            (_, token) =>
            {
                token.ThrowIfCancellationRequested();
                Interlocked.Increment(ref runtimeCalls);
                return Task.FromResult(new AgentTurnResult("prepare-thread", "prepare-turn", "completed", null));
            },
            prepareBatch: async (request, token) =>
            {
                if (request.BatchId != "prepare-canceled")
                {
                    return;
                }

                preparationStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    preparationCanceled.TrySetResult();
                    await allowPreparationExit.Task;
                    throw;
                }
            });
        coordinator.DispatchCompleted += (_, e) =>
        {
            Equal("prepare-next", e.Request.BatchId,
                "准备阶段被手动取消的批次不得触发普通 DispatchCompleted");
            nextCompleted.TrySetResult();
        };
        coordinator.TryEnqueue("prepare-canceled", ["prepare-canceled-task"], isRetry: false);
        await preparationStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        coordinator.TryEnqueue("prepare-next", ["prepare-next-task"], isRetry: false);
        var cancellation = coordinator.CancelBatchesAsync(["prepare-canceled"]);
        await preparationCanceled.Task.WaitAsync(TimeSpan.FromSeconds(3));
        False(cancellation.IsCompleted,
            "准备阶段的取消也必须等待准备函数真正退出");
        allowPreparationExit.TrySetResult();
        await cancellation.WaitAsync(TimeSpan.FromSeconds(3));
        await nextCompleted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Equal(1, runtimeCalls,
            "被取消的准备阶段不能继续启动 Agent Turn，无关下一批次应正常启动");
    }

    private static async Task ManagedCodexClaimResumesIdempotentlyAndForeignClaimIsNotStolenAsync()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "GAssetForge.ManagedClaimResumeSmokeTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var time = new LeaseRegressionTests.ManualTimeProvider(
                new DateTimeOffset(2026, 8, 27, 9, 0, 0, TimeSpan.Zero));
            var store = new JsonGenerationTaskStore(
                root,
                time,
                TimeSpan.FromMinutes(10));
            var managedBatchId = Guid.NewGuid().ToString("N");
            var managedTask = (await store.CreateBatchAsync(new CreateGenerationBatchRequest
            {
                BatchId = managedBatchId,
                DisplayName = "受管 Codex Claim 续作",
                DispatchRequested = true,
                Items =
                [
                    new CreateGenerationBatchItemRequest
                    {
                        ItemId = Guid.NewGuid().ToString("N"),
                        DisplayName = "保持原任务与路径",
                        Constraints = CreateClaimResumeConstraints("保持原任务与路径")
                    }
                ]
            })).Single();
            var managedAgentId = GenerationManagedAgentIdentity.ForTask(managedTask.Id);
            var firstClaim = await store.ClaimAsync(managedTask.Id, managedAgentId);
            var repeatedClaim = await store.ClaimAsync(managedTask.Id, managedAgentId);
            Equal(firstClaim.Id, repeatedClaim.Id,
                "同一受管身份重复 claim 必须复用原 taskId");
            Equal(firstClaim.Revision, repeatedClaim.Revision,
                "同一受管身份重复 claim 必须保持 revision 不变");
            Equal(firstClaim.RetryLineage, repeatedClaim.RetryLineage,
                "同一受管身份重复 claim 必须保持 retry lineage 不变");
            Equal(firstClaim.ClaimedBy, repeatedClaim.ClaimedBy,
                "同一受管身份重复 claim 必须保持冻结所有者不变");
            time.Advance(TimeSpan.FromMinutes(9));

            string? managedPrompt = null;
            var managedCompletion = new TaskCompletionSource<GenerationBatchDispatchCompletedEventArgs>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            await using (var coordinator = new GenerationBatchDispatchCoordinator(
                             (prompt, _) =>
                             {
                                 managedPrompt = prompt;
                                 return Task.FromResult(new AgentTurnResult(
                                     ThreadId: "managed-claim-thread",
                                     TurnId: "managed-claim-turn",
                                     Status: "completed",
                                     ErrorMessage: null));
                             },
                             store))
            {
                coordinator.DispatchCompleted += (_, eventArgs) =>
                    managedCompletion.TrySetResult(eventArgs);
                Equal(
                    GenerationBatchDispatchEnqueueResult.Enqueued,
                    coordinator.TryEnqueue(managedBatchId, [managedTask.Id], isRetry: false),
                    "Claimed-only 受管任务应进入重新调度生命周期");
                var completed = await managedCompletion.Task.WaitAsync(TimeSpan.FromSeconds(3));
                True(completed.IsSuccess,
                    $"同一 Codex Claim 的续作 Turn 应启动成功：{completed.FailureMessage}");
            }

            True(managedPrompt?.Contains(
                    $"{managedTask.Id} => `{managedAgentId}`",
                    StringComparison.Ordinal) == true,
                "续作 prompt 必须逐任务复用冻结 ClaimedBy");
            True(managedPrompt?.Contains(
                    "相同身份重复 claim 是断线后的幂等续作",
                    StringComparison.Ordinal) == true,
                "续作 prompt 必须明确同身份幂等语义");
            var afterManagedDispatch = await store.GetAsync(managedTask.Id)
                ?? throw new InvalidOperationException("受管续作任务丢失。");
            Equal(repeatedClaim.Revision + 1, afterManagedDispatch.Revision,
                "续作 Turn 启动前必须立即续租临近过期的 Claimed 任务");
            Equal(time.GetUtcNow(), afterManagedDispatch.UpdatedAtUtc,
                "立即续租必须把租约基准推进到续作启动时间");
            Equal(repeatedClaim.RetryLineage, afterManagedDispatch.RetryLineage,
                "立即续租不得改写 retry lineage");
            Equal(managedAgentId, afterManagedDispatch.ClaimedBy,
                "立即续租不得改写冻结所有者");
            time.Advance(TimeSpan.FromMinutes(2));
            var afterOriginalLeaseBoundary = await store.GetAsync(managedTask.Id)
                ?? throw new InvalidOperationException("受管续作租约边界任务丢失。");
            Equal(GenerationTaskStatus.Claimed, afterOriginalLeaseBoundary.Status,
                "续作前立即续租后，任务跨过原租约边界仍应保持 Claimed");

            var foreignBatchId = Guid.NewGuid().ToString("N");
            var foreignTask = (await store.CreateBatchAsync(new CreateGenerationBatchRequest
            {
                BatchId = foreignBatchId,
                DisplayName = "其它 Codex 会话 Claim 不得冒用",
                DispatchRequested = true,
                Items =
                [
                    new CreateGenerationBatchItemRequest
                    {
                        ItemId = Guid.NewGuid().ToString("N"),
                        DisplayName = "外部 Agent 所有权",
                        Constraints = CreateClaimResumeConstraints("外部 Agent 所有权")
                    }
                ]
            })).Single();
            const string foreignAgentId = "codex:external-session-owner";
            var foreignClaim = await store.ClaimAsync(foreignTask.Id, foreignAgentId);
            var foreignTurnCount = 0;
            var foreignCompletion = new TaskCompletionSource<GenerationBatchDispatchCompletedEventArgs>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            await using (var coordinator = new GenerationBatchDispatchCoordinator(
                             (_, _) =>
                             {
                                 Interlocked.Increment(ref foreignTurnCount);
                                 return Task.FromResult(new AgentTurnResult(
                                     ThreadId: "must-not-start",
                                     TurnId: "must-not-start",
                                     Status: "completed",
                                     ErrorMessage: null));
                             },
                             store))
            {
                coordinator.DispatchCompleted += (_, eventArgs) =>
                    foreignCompletion.TrySetResult(eventArgs);
                Equal(
                    GenerationBatchDispatchEnqueueResult.Enqueued,
                    coordinator.TryEnqueue(foreignBatchId, [foreignTask.Id], isRetry: false),
                    "外部 Claim 请求仍应进入协调器并得到结构化拒绝结果");
                var rejected = await foreignCompletion.Task.WaitAsync(TimeSpan.FromSeconds(3));
                False(rejected.IsSuccess,
                    "其它 Codex 会话的 Claimed-only 批次不得启动续作 Turn");
                True(rejected.FailureMessage?.Contains("不会抢占", StringComparison.Ordinal) == true,
                    "外部 Claim 拒绝必须明确说明不会抢占");
            }

            Equal(0, foreignTurnCount,
                "其它 Codex 会话所有者不得进入 Agent Turn");
            var afterForeignAttempt = await store.GetAsync(foreignTask.Id)
                ?? throw new InvalidOperationException("外部 Claim 任务丢失。");
            Equal(foreignClaim.Revision, afterForeignAttempt.Revision,
                "拒绝外部 Claim 续作不得推进 revision");
            Equal(foreignAgentId, afterForeignAttempt.ClaimedBy,
                "拒绝外部 Claim 续作不得改写原所有者");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static GenerationConstraintPackage CreateClaimResumeConstraints(string prompt) => new()
    {
        WorkflowId = "managed-claim-resume-smoke",
        PreferredSkill = "imagegen",
        Prompt = prompt,
        Output = new GenerationOutputSpec
        {
            Width = 64,
            Height = 64,
            Count = 1,
            MaxParallelism = 1,
            Format = "png"
        }
    };

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

            Equal(GenerationBatchDispatchEnqueueResult.Enqueued,
                coordinator.TryEnqueue("legacy-batch", [task.Id], isRetry: false),
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
                "真正以 MCP tool error 失败时才调用 fail_generation_task",
                StringComparison.Ordinal),
            "Agent 指令必须把失败登记限制在真实生成或提交失败");
        True(prompt.Contains(
                "outcome=retryable_validation_failure",
                StringComparison.Ordinal),
            "几何质量未通过必须作为已落盘 warning 处理而不是图片交付错误");
        True(prompt.Contains(
                "failureCode=image_delivery_fallback_failed",
                StringComparison.Ordinal) &&
             prompt.Contains(
                "failureCode=output_geometry_validation_failed",
                StringComparison.Ordinal),
            "Agent 指令必须区分图片交付失败与末端几何校验失败");
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
        GenerationBatchDispatchCoordinator? coordinator = null;
        await using var ownedCoordinator = coordinator = new GenerationBatchDispatchCoordinator(
            async (_, cancellationToken) =>
            {
                var turnId = Guid.NewGuid().ToString("N");
                coordinator!.NotifyTurnStarted("thread-progress-smoke", turnId);
                await Task.Delay(25, cancellationToken);
                return new AgentTurnResult(
                    ThreadId: "thread-progress-smoke",
                    TurnId: turnId,
                    Status: "completed",
                    ErrorMessage: null);
            }, currentThreadIdProvider: () => "thread-progress-smoke");
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

        Equal(GenerationBatchDispatchEnqueueResult.Enqueued,
            coordinator.TryEnqueue("batch-progress-a", ["task-a"], isRetry: false),
            "第一进度批次应进入调度队列");
        Equal(GenerationBatchDispatchEnqueueResult.Enqueued,
            coordinator.TryEnqueue("batch-progress-b", ["task-b"], isRetry: false),
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
            GenerationBatchDispatchStage.WaitingForTaskClaim,
            GenerationBatchDispatchStage.WaitingForTaskClaim
        };
        Equal(8, snapshots.Length, "两个批次应各报告提交、等待与 Turn 用时四个结构化阶段");
        Equal(
            "batch-progress-a,batch-progress-a,batch-progress-a,batch-progress-a," +
            "batch-progress-b,batch-progress-b,batch-progress-b,batch-progress-b",
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
            True(batchSnapshots.Last().ElapsedMilliseconds is >= 0,
                $"批次 {batchId} 的 Turn 完成快照必须记录持续时间");
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

        Equal(GenerationBatchDispatchEnqueueResult.Enqueued,
            coordinator.TryEnqueue("batch-1", ["task-a"], isRetry: false),
            "第一批次应进入 Agent 调度队列");
        Equal(GenerationBatchDispatchEnqueueResult.AlreadyScheduled,
            coordinator.TryEnqueue("batch-1", ["task-a-duplicate"], isRetry: false),
            "相同 batchId 的恢复入队应幂等成功而不是创建第二个 Turn");
        Equal(GenerationBatchDispatchEnqueueResult.Enqueued,
            coordinator.TryEnqueue("batch-2", ["task-b", "task-c"], isRetry: false),
            "第二批次应进入 Agent 调度队列");
        Equal(GenerationBatchDispatchEnqueueResult.Enqueued,
            coordinator.TryEnqueue("batch-3", ["task-d"], isRetry: true),
            "重试批次应进入 Agent 调度队列");

        await allCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Equal(1, maximumConcurrentTurns, "同一 Agent 会话的批次 Turn 必须严格串行");
        Equal(3, prompts.Count, "三个不超过五项的小批次应各生成一个 Agent Turn prompt");
        False(prompts.Any(prompt => prompt.Contains("task-a-duplicate", StringComparison.Ordinal)),
            "正常发布与恢复扫描并发时不得重复派发同一批次");
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

        var recoveredCompletion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.DispatchCompleted += (_, eventArgs) =>
        {
            if (string.Equals(eventArgs.Request.BatchId, "batch-1", StringComparison.Ordinal) &&
                eventArgs.Request.TaskIds.Contains("task-a-recovered", StringComparer.Ordinal))
            {
                recoveredCompletion.TrySetResult();
            }
        };
        coordinator.ReleaseBatch("batch-1");
        Equal(GenerationBatchDispatchEnqueueResult.Enqueued,
            coordinator.TryEnqueue("batch-1", ["task-a-recovered"], isRetry: false),
            "状态收束失败后释放 batch 生命周期应允许同一 Pending 批次再次派发");
        await recoveredCompletion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Equal(4, prompts.Count,
            "释放已完成调度生命周期后，恢复扫描应能为仍 Pending 的同一批次再启动一次 Turn");
    }

    private static async Task LargeBatchUsesFiveFiveTwoTurnsWithSinglePreparationAsync()
    {
        var taskIds = Enumerable.Range(1, 12)
            .Select(index => $"chunk-success-task-{index:D2}")
            .ToArray();
        var prompts = new List<string>();
        var prepareCount = 0;
        var completion = new TaskCompletionSource<GenerationBatchDispatchCompletedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var coordinator = new GenerationBatchDispatchCoordinator(
            (prompt, _) =>
            {
                prompts.Add(prompt);
                return Task.FromResult(new AgentTurnResult(
                    ThreadId: "thread-chunk-success",
                    TurnId: $"turn-chunk-success-{prompts.Count}",
                    Status: "completed",
                    ErrorMessage: null));
            },
            prepareBatch: (request, _) =>
            {
                Interlocked.Increment(ref prepareCount);
                Equal("batch-chunk-success", request.BatchId,
                    "批次准备 seam 必须收到稳定 batchId");
                Equal(string.Join(',', taskIds), string.Join(',', request.TaskIds),
                    "批次准备 seam 应只调用一次并接收完整有序任务列表");
                return Task.CompletedTask;
            });
        coordinator.DispatchCompleted += (_, eventArgs) => completion.TrySetResult(eventArgs);

        Equal(
            GenerationBatchDispatchEnqueueResult.Enqueued,
            coordinator.TryEnqueue("batch-chunk-success", taskIds, isRetry: false),
            "12 项批次应进入分块调度队列");
        var completed = await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));

        True(
            completed.IsSuccess,
            $"三个 Turn 分块全部成功时批次调度应成功：{completed.FailureMessage ?? completed.Result?.Status}");
        Equal(1, prepareCount, "同一批次的所有 Turn 分块必须只准备一次 Agent thread");
        Equal(3, prompts.Count, "12 项应固定拆成 5/5/2 三个串行 Turn");
        Equal(
            "5,5,2",
            string.Join(',', prompts.Select(prompt => ReadPromptTaskIds(prompt).Length)),
            "固定 Turn 分块大小");
        Equal(
            string.Join(',', taskIds),
            string.Join(',', prompts.SelectMany(ReadPromptTaskIds)),
            "分块不得遗漏、重复或改变原批次任务顺序");
        True(prompts.All(prompt =>
                ReadPromptTaskIds(prompt).Length <=
                GenerationBatchDispatchCoordinator.MaximumTasksPerTurn),
            "任何 Agent Turn 都不得超过固定五项上限");
    }

    private static async Task FailedChunkStopsLaterTurnsAsync()
    {
        var taskIds = Enumerable.Range(1, 12)
            .Select(index => $"chunk-failure-task-{index:D2}")
            .ToArray();
        var prompts = new List<string>();
        var progress = new List<GenerationBatchDispatchProgressSnapshot>();
        var prepareCount = 0;
        var completion = new TaskCompletionSource<GenerationBatchDispatchCompletedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var coordinator = new GenerationBatchDispatchCoordinator(
            (prompt, _) =>
            {
                prompts.Add(prompt);
                var turnNumber = prompts.Count;
                return Task.FromResult(new AgentTurnResult(
                    ThreadId: "thread-chunk-failure",
                    TurnId: $"turn-chunk-failure-{turnNumber}",
                    Status: turnNumber == 2 ? "failed" : "completed",
                    ErrorMessage: turnNumber == 2
                        ? "stream closed before response.completed"
                        : null));
            },
            prepareBatch: (_, _) =>
            {
                Interlocked.Increment(ref prepareCount);
                return Task.CompletedTask;
            });
        coordinator.DispatchProgressChanged += (_, eventArgs) => progress.Add(eventArgs.Snapshot);
        coordinator.DispatchCompleted += (_, eventArgs) => completion.TrySetResult(eventArgs);

        Equal(
            GenerationBatchDispatchEnqueueResult.Enqueued,
            coordinator.TryEnqueue("batch-chunk-failure", taskIds, isRetry: false),
            "失败分块场景应进入调度队列");
        var completed = await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));

        False(completed.IsSuccess, "第二个 Turn 断流必须让本次批次派发报告失败");
        Equal(1, prepareCount, "Turn 断流不得为同一批次重复准备 thread");
        Equal(2, prompts.Count, "第二个 Turn 失败后不得启动包含最后两项的第三个 Turn");
        Equal(
            string.Join(',', taskIds.Take(10)),
            string.Join(',', prompts.SelectMany(ReadPromptTaskIds)),
            "断流前只允许派发前两个五项分块");
        True(completed.FailureMessage?.Contains(
                "stream closed before response.completed",
                StringComparison.Ordinal) == true,
            "分块失败结果必须保留上游 Responses 流诊断");
        Equal(GenerationBatchDispatchStage.Interrupted, progress.Last().Stage,
            "Responses 流中断应显示 Interrupted，而不是美术生成 Failed");
        Equal<int?>(2, progress.Last().ChunkIndex,
            "中断诊断必须记录失败 Turn 分块序号");
        Equal<int?>(3, progress.Last().ChunkCount,
            "中断诊断必须记录批次 Turn 分块总数");
        Equal<int?>(5, progress.Last().ChunkTaskCount,
            "中断诊断必须记录当前分块任务数");
        True(progress.Last().ElapsedMilliseconds is >= 0,
            "中断诊断必须记录 Turn 持续时间");
        Equal(AgentTurnFailureClassifier.TransportStreamDisconnectedCode,
            progress.Last().FailureCode,
            "中断诊断必须暴露稳定传输错误码");
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

        Equal(GenerationBatchDispatchEnqueueResult.Enqueued,
            coordinator.TryEnqueue("batch-failure", ["task-pending"], isRetry: false),
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
                if (eventArgs.Snapshot.Stage == GenerationBatchDispatchStage.StartingAgentTurn)
                {
                    waitingForClaim.TrySetResult();
                }
            };
            coordinator.DispatchCompleted += (_, _) => Interlocked.Increment(ref completionCount);

            Equal(GenerationBatchDispatchEnqueueResult.Enqueued,
                coordinator.TryEnqueue("batch-canceled", ["task-canceled"], isRetry: false),
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

    private static string[] ReadPromptTaskIds(string prompt) => prompt
        .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .Where(line => line.StartsWith("- ", StringComparison.Ordinal))
        .Select(line => line[2..].Trim())
        .ToArray();

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
