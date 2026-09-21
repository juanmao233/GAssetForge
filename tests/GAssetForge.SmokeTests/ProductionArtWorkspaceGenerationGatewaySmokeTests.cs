using GAssetForge.App.Services;
using GAssetForge.App.Services.AgentRuntime;
using GAssetForge.App.ViewModels;
using GAssetForge.Core.Generation;
using System.Collections.Concurrent;
using System.IO;
using System.Text.Json.Nodes;

namespace GAssetForge.SmokeTests;

internal static class ProductionArtWorkspaceGenerationGatewaySmokeTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public static void RunAll(string temporaryRoot)
    {
        ManifestFailureAndStoppedQueueKeepDurableOwnerAsync(temporaryRoot)
            .GetAwaiter()
            .GetResult();
        TransportInterruptionReleasesUiWhileSameTaskResumesInBackgroundAsync(temporaryRoot)
            .GetAwaiter()
            .GetResult();
        DisposeDuringActiveGenerationDoesNotRaceSemaphoreAsync(temporaryRoot)
            .GetAwaiter()
            .GetResult();
        ClearDrainsDurablePublicationBeforeSuspendingAsync(temporaryRoot)
            .GetAwaiter()
            .GetResult();
        SuccessfulTurnStillReportsPersistedImageGenerationFailureAsync(temporaryRoot)
            .GetAwaiter()
            .GetResult();
    }

    private static async Task SuccessfulTurnStillReportsPersistedImageGenerationFailureAsync(string temporaryRoot)
    {
        var fixture = CreateFixture(temporaryRoot, "completed-turn-failed-image");
        using var agentConnection = fixture.AgentConnection;
        using var console = fixture.Console;
        var published = new TaskCompletionSource<(string BatchId, string TaskId)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var enqueueCount = 0;
        using var gateway = new ProductionArtWorkspaceGenerationGateway(
            fixture.Paths, fixture.Store, agentConnection,
            (batchId, taskIds, _) =>
            {
                Interlocked.Increment(ref enqueueCount);
                published.TrySetResult((batchId, taskIds.Single()));
                return GenerationBatchDispatchEnqueueResult.Enqueued;
            }, console, isConnected: static () => true, isTurnRunning: static () => false);
        var progress = new RecordingProgress();
        var generation = gateway.GenerateAsync(CreateRequest(fixture.ProjectRoot, fixture.AssetRoot), progress);
        var queued = await published.Task.WaitAsync(Timeout).ConfigureAwait(false);
        const string originalError = "image generation failed: network error: error sending request for url " +
                                     "(https://chatgpt.com/backend-api/codex/images/generations)";
        var claimant = GenerationManagedAgentIdentity.ForTask(queued.TaskId);
        await fixture.Store.ClaimAsync(queued.TaskId, claimant).ConfigureAwait(false);
        var failed = await fixture.Store.FailAsync(queued.TaskId, claimant, new GenerationTaskFailure
        {
            Code = "image_generation_failed",
            Stage = "image_generation",
            Message = originalError,
            Retryable = true
        }).ConfigureAwait(false);
        await gateway.ReportDispatchCompletedAsync(
            new GenerationBatchDispatchCompletedEventArgs(
                new GenerationBatchDispatchRequest(queued.BatchId, [queued.TaskId], IsRetry: false),
                new AgentTurnResult("fixture-thread", "fixture-turn", "completed", null), null),
            new GenerationBatchTurnReconciliationResult(queued.BatchId,
                TotalCount: 1, PendingCount: 0, ClaimedCount: 0, CompletedCount: 0, FailedCount: 1,
                AutoFailedCount: 0, FailureDetail: null), preservePendingTasks: false).ConfigureAwait(false);
        var result = await generation.WaitAsync(Timeout).ConfigureAwait(false);
        False(result.IsSuccess, "Agent Turn completed 不能覆盖持久图片任务 Failed");
        False(result.IsDeferred, "图片任务明确失败应作为终态收束");
        True(result.ErrorMessage?.Contains("图片生成服务网络连接失败", StringComparison.Ordinal) == true,
            "网络失败应明确归属于图片模型请求阶段");
        True(result.ErrorMessage?.Contains("image_generation_failed", StringComparison.Ordinal) == true,
            "失败消息应包含持久分类而非仅模糊最终结果提示");
        True(result.ErrorMessage?.Contains("手动重新发送", StringComparison.Ordinal) == true,
            "失败消息应给出不自动收费重试的下一步");
        True(result.ErrorMessage?.Contains(originalError, StringComparison.Ordinal) == true,
            "用户应能复制完整原始错误供排查");
        False(result.ErrorMessage?.Contains("自动切换", StringComparison.Ordinal) == true,
            "没有可用性证据时不得声称已自动切换 Image API fallback");
        var terminal = progress.Snapshots.Last();
        Equal("failed", terminal.State, "终态进度不得停留在 WaitingForTaskClaim");
        Equal(100d, terminal.Percentage, "失败终态应结束进度等待");
        Equal(result.ErrorMessage, terminal.Message, "进度与对话失败原因应一致");
        False(gateway.IsBusy, "图片模型失败后必须释放新指令发送门禁");
        Equal(1, enqueueCount, "图片网络失败不得创建或自动派发新的收费尝试");
        Equal(1, (await fixture.Store.ListAsync().ConfigureAwait(false)).Count,
            "失败诊断不得创建替代任务");
        var persisted = await fixture.Store.GetAsync(queued.TaskId).ConfigureAwait(false);
        Equal(failed.Revision, persisted!.Revision, "可读失败说明不得改写持久任务");
        Equal(originalError, persisted.FailureMessage, "可读失败说明不得覆盖原始诊断");
        var missingKeyMessage = ProductionArtWorkspaceGenerationGateway.DescribeFailedTask(failed with
        {
            FailureCode = "image_delivery_fallback_failed",
            FailureStage = "image.cli_fallback",
            FailureMessage = "image_delivery_fallback_failed: Agent 子进程环境未配置 OPENAI_API_KEY"
        });
        True(missingKeyMessage.Contains("备用通道未配置 OPENAI_API_KEY，当前不可用", StringComparison.Ordinal),
            "缺少 API key 时必须明确备用通道不可用");
        False(missingKeyMessage.Contains("自动切换", StringComparison.Ordinal),
            "缺少 API key 不得承诺自动切换或执行付费备用请求");
    }

    private static async Task ClearDrainsDurablePublicationBeforeSuspendingAsync(string temporaryRoot)
    {
        var fixture = CreateFixture(temporaryRoot, "clear-drains-publication");
        using var agentConnection = fixture.AgentConnection;
        using var console = fixture.Console;
        var manifestEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var manifestRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var enqueued = 0;
        using var gateway = new ProductionArtWorkspaceGenerationGateway(
            fixture.Paths, fixture.Store, agentConnection,
            (_, _, _) =>
            {
                Interlocked.Increment(ref enqueued);
                return GenerationBatchDispatchEnqueueResult.Enqueued;
            },
            console, isConnected: static () => true, isTurnRunning: static () => false,
            writeManifestAsync: (_, _, _) =>
            {
                manifestEntered.TrySetResult();
                return manifestRelease.Task;
            });
        using var cancellation = new CancellationTokenSource();
        var generation = gateway.GenerateAsync(CreateRequest(fixture.ProjectRoot, fixture.AssetRoot),
            cancellationToken: cancellation.Token);
        await manifestEntered.Task.WaitAsync(Timeout).ConfigureAwait(false);
        cancellation.Cancel();
        gateway.CancelProjectWaiters(fixture.ProjectRoot, "用户清除");
        var drain = gateway.WaitForPublicationIdleAsync(CancellationToken.None);
        False(drain.IsCompleted, "清理必须等待已持久化但尚未写完 Manifest 的任务发布结束");
        manifestRelease.SetResult();
        await drain.WaitAsync(Timeout).ConfigureAwait(false);
        try
        {
            await generation.WaitAsync(Timeout).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // UI cancellation may win over the already completed reset result.
        }

        gateway.CancelProjectWaiters(fixture.ProjectRoot, "用户清除");
        var tasks = await fixture.Store.ListAsync().ConfigureAwait(false);
        Equal(1, tasks.Count, "发布 barrier 后快照必须完整包含刚创建的持久任务");
        Equal(1, enqueued, "发布 barrier 后不会再有旧任务从 Manifest 阶段漏入队列");
        await fixture.Store.SuspendAutomaticDispatchAsync([tasks.Single().Id], "用户清除").ConfigureAwait(false);
        False(gateway.OwnsBatch(tasks.Single().BatchId), "清除不能残留旧批次 owner");
        False(gateway.IsBusy, "清除后必须释放网关发布门禁");
        var persisted = await fixture.Store.GetAsync(tasks.Single().Id).ConfigureAwait(false);
        False(persisted!.DispatchIntent!.Requested, "发布中遇到清除的任务也必须持久化暂停");
    }

    private static async Task ManifestFailureAndStoppedQueueKeepDurableOwnerAsync(
        string temporaryRoot)
    {
        var fixture = CreateFixture(temporaryRoot, "manifest-failure-queue-stopped");
        using var agentConnection = fixture.AgentConnection;
        using var console = fixture.Console;
        var enqueueObserved = new TaskCompletionSource<(string BatchId, string TaskId)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var manifestTokenCanBeCanceled = true;
        using var gateway = new ProductionArtWorkspaceGenerationGateway(
            fixture.Paths,
            fixture.Store,
            agentConnection,
            (batchId, taskIds, _) =>
            {
                enqueueObserved.TrySetResult((batchId, taskIds.Single()));
                return GenerationBatchDispatchEnqueueResult.QueueStopped;
            },
            console,
            isConnected: static () => true,
            isTurnRunning: static () => false,
            agentDisplayName: static () => "Codex fixture",
            writeManifestAsync: (_, _, cancellationToken) =>
            {
                manifestTokenCanBeCanceled = cancellationToken.CanBeCanceled;
                return Task.FromException(new IOException("fixture manifest unavailable"));
            });
        var progress = new RecordingProgress();
        using var cancellation = new CancellationTokenSource();

        var firstRequest = CreateRequest(fixture.ProjectRoot, fixture.AssetRoot);
        var generation = gateway.GenerateAsync(
            firstRequest,
            progress,
            cancellation.Token);
        var published = await enqueueObserved.Task.WaitAsync(Timeout).ConfigureAwait(false);

        False(manifestTokenCanBeCanceled,
            "持久任务创建后 Manifest 写入不得再受 UI cancellation token 中断");
        True(gateway.OwnsBatch(published.BatchId),
            "Manifest 写入失败后必须先保留批次 owner 再继续派发");
        var stoppedResult = await generation.WaitAsync(Timeout).ConfigureAwait(false);
        False(stoppedResult.IsSuccess,
            "QueueStopped 不得误报生成成功");
        True(stoppedResult.IsDeferred,
            "QueueStopped 应释放 UI waiter，同时把持久任务标记为后台等待恢复");
        False(gateway.IsBusy,
            "QueueStopped 已结束当前派发后不得继续占用发送门禁");
        var persisted = await fixture.Store.GetAsync(published.TaskId).ConfigureAwait(false)
            ?? throw new InvalidOperationException("找不到 Manifest 失败后已经持久化的美术工作区任务。");
        Equal(GenerationTaskStatus.Pending, persisted.Status,
            "QueueStopped 后的持久任务状态");
        True(MaterialGenerationConstraintComposer.TryReadDelivery(
                persisted.Constraints,
                out var delivery) &&
             delivery?.Workspace is not null,
            "旧 Generated/ArtWorkspace 输入仍应冻结为完整的新目录交付合同");
        Equal($"Final/ArtWorkspace/{published.BatchId}", delivery!.OutputDirectory,
            "旧 Generated/ArtWorkspace 输入应归一到无尾随 Final 的新产物目录");
        Equal($"AI/ArtWorkspace/{published.BatchId}", delivery.Workspace!.RootPath,
            "Art Workspace 运行区应冻结在 Assets/AI 分类下");
        var targetRelativePath = ReadSingleTargetPath(persisted);
        Equal(delivery.OutputDirectory, targetRelativePath[..targetRelativePath.LastIndexOf('/')],
            "冻结目标应直接位于批次 ArtWorkspace 目录内");
        var artWorkspace = persisted.Constraints.Parameters["artWorkspace"] as JsonObject
            ?? throw new InvalidOperationException("美术工作区任务缺少 parameters.artWorkspace。");
        Equal("Final", artWorkspace["outputFrame"]?["outputRelativeDirectory"]?.GetValue<string>(),
            "约束包应记录已归一化的 Final 分类根");
        True(progress.Snapshots.Any(snapshot => snapshot.State == "publishing-warning"),
            "Manifest 写入失败应提供非终态警告进度");
        True(progress.Snapshots.Any(snapshot => snapshot.State == "interrupted"),
            "QueueStopped 应向 UI 报告可恢复中断状态");

        True(gateway.OwnsBatch(published.BatchId),
            "释放当前 UI 等待不得删除已持久化任务 owner");
    }

    private static async Task TransportInterruptionReleasesUiWhileSameTaskResumesInBackgroundAsync(
        string temporaryRoot)
    {
        var fixture = CreateFixture(temporaryRoot, "transport-interruption-resume");
        using var agentConnection = fixture.AgentConnection;
        using var console = fixture.Console;
        var enqueueObserved = new TaskCompletionSource<(string BatchId, string TaskId)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEnqueueObserved = new TaskCompletionSource<(string BatchId, string TaskId)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var enqueueCount = 0;
        using var gateway = new ProductionArtWorkspaceGenerationGateway(
            fixture.Paths,
            fixture.Store,
            agentConnection,
            (batchId, taskIds, _) =>
            {
                var publishedTask = (batchId, taskIds.Single());
                if (Interlocked.Increment(ref enqueueCount) == 1)
                {
                    enqueueObserved.TrySetResult(publishedTask);
                }
                else
                {
                    secondEnqueueObserved.TrySetResult(publishedTask);
                }
                return GenerationBatchDispatchEnqueueResult.Enqueued;
            },
            console,
            isConnected: static () => true,
            isTurnRunning: static () => false,
            agentDisplayName: static () => "Codex fixture");
        var progress = new RecordingProgress();
        var deferredCompletion =
            new TaskCompletionSource<ArtWorkspaceDeferredGenerationCompletedEventArgs>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        gateway.DeferredGenerationCompleted += (_, eventArgs) =>
            deferredCompletion.TrySetResult(eventArgs);
        using var cancellation = new CancellationTokenSource();
        var firstRequest = CreateRequest(fixture.ProjectRoot, fixture.AssetRoot);
        var generation = gateway.GenerateAsync(
            firstRequest,
            progress,
            cancellation.Token);
        var published = await enqueueObserved.Task.WaitAsync(Timeout).ConfigureAwait(false);
        var dispatchRequest = new GenerationBatchDispatchRequest(
            published.BatchId,
            [published.TaskId],
            IsRetry: false);
        var interruptedCompletion = new GenerationBatchDispatchCompletedEventArgs(
            dispatchRequest,
            new AgentTurnResult(
                "fixture-thread",
                "fixture-turn-interrupted",
                "failed",
                "stream closed before response.completed"),
            "stream closed before response.completed");
        var interruptedReconciliation = new GenerationBatchTurnReconciliationResult(
            published.BatchId,
            TotalCount: 1,
            PendingCount: 1,
            ClaimedCount: 0,
            CompletedCount: 0,
            FailedCount: 0,
            AutoFailedCount: 0,
            FailureDetail: "agent_transport_stream_disconnected");

        await gateway.ReportDispatchCompletedAsync(
                interruptedCompletion,
                interruptedReconciliation,
                preservePendingTasks: true)
            .ConfigureAwait(false);

        True(gateway.OwnsBatch(published.BatchId),
            "Responses/SSE 中断后必须保留原批次 owner");
        var interruptedResult = await generation.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        False(interruptedResult.IsSuccess,
            "Responses/SSE 中断后应释放本轮 UI waiter，让用户能够继续发送新指令");
        True(interruptedResult.IsDeferred,
            "Responses/SSE 中断结果应与真实任务 Failed 区分");
        False(gateway.IsBusy,
            "保留持久任务 owner 不得继续占用美术工作区发送门禁");
        True(progress.Snapshots.Any(snapshot => snapshot.State == "interrupted"),
            "传输中断应显示等待续派状态");
        False(progress.Snapshots
                .Where(snapshot => snapshot.State == "interrupted")
                .Any(snapshot => snapshot.AgentDescription?.Contains("完成单图生成", StringComparison.Ordinal) == true),
            "传输中断的本地兜底描述不得误报图片已经完成");
        False(deferredCompletion.Task.IsCompleted,
            "后台任务尚未达到真实终态时不得发送延迟完成通知");

        using var secondCancellation = new CancellationTokenSource();
        var secondGeneration = gateway.GenerateAsync(
            CreateRequest(fixture.ProjectRoot, fixture.AssetRoot),
            cancellationToken: secondCancellation.Token);
        var secondPublished = await secondEnqueueObserved.Task.WaitAsync(Timeout).ConfigureAwait(false);
        True(gateway.OwnsBatch(secondPublished.BatchId),
            "释放中断 waiter 后必须能立即发布第二条美术指令");
        secondCancellation.Cancel();
        await ExpectCanceledAsync(secondGeneration).ConfigureAwait(false);

        var pendingTask = await fixture.Store.GetAsync(published.TaskId).ConfigureAwait(false)
            ?? throw new InvalidOperationException("找不到等待续派的美术工作区任务。");
        var targetRelativePath = ReadSingleTargetPath(pendingTask);
        var targetFullPath = Path.Combine(
            fixture.AssetRoot,
            targetRelativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(targetFullPath)!);
        await File.WriteAllBytesAsync(targetFullPath, [0x47, 0x41, 0x46]).ConfigureAwait(false);
        var agentId = GenerationManagedAgentIdentity.ForTask(pendingTask.Id);
        await fixture.Store.ClaimAsync(pendingTask.Id, agentId).ConfigureAwait(false);
        await fixture.Store.CompleteAsync(
                pendingTask.Id,
                agentId,
                [targetRelativePath])
            .ConfigureAwait(false);
        var resumedCompletion = new GenerationBatchDispatchCompletedEventArgs(
            dispatchRequest,
            new AgentTurnResult(
                "fixture-thread",
                "fixture-turn-resumed",
                "completed",
                null),
            null);
        var completedReconciliation = interruptedReconciliation with
        {
            PendingCount = 0,
            CompletedCount = 1,
            FailureDetail = null
        };

        await gateway.ReportDispatchCompletedAsync(
                resumedCompletion,
                completedReconciliation,
                preservePendingTasks: false)
            .ConfigureAwait(false);
        var deferredEvent = await deferredCompletion.Task.WaitAsync(Timeout).ConfigureAwait(false);
        var result = deferredEvent.Result;

        True(result.IsSuccess,
            "续派同一持久任务完成后，已释放的 UI 应收到后台成功通知");
        Equal(firstRequest.RequestId, deferredEvent.Request.RequestId,
            "延迟完成通知必须保留原冻结请求上下文");
        Equal(published.BatchId, result.BatchId,
            "延迟完成通知必须保留原批次 ID");
        Equal(published.TaskId, result.TaskId,
            "续派完成必须沿用原 taskId");
        Equal(targetFullPath, result.OutputPaths.Single(),
            "续派完成必须交付原冻结目标");
        False(gateway.OwnsBatch(published.BatchId),
            "真实终态完成后才允许移除批次 owner");
    }

    private static async Task DisposeDuringActiveGenerationDoesNotRaceSemaphoreAsync(
        string temporaryRoot)
    {
        var fixture = CreateFixture(temporaryRoot, "dispose-active-generation");
        using var agentConnection = fixture.AgentConnection;
        using var console = fixture.Console;
        var enqueueObserved = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var gateway = new ProductionArtWorkspaceGenerationGateway(
            fixture.Paths,
            fixture.Store,
            agentConnection,
            (_, _, _) =>
            {
                enqueueObserved.TrySetResult(true);
                return GenerationBatchDispatchEnqueueResult.Enqueued;
            },
            console,
            isConnected: static () => true,
            isTurnRunning: static () => false,
            agentDisplayName: static () => "Codex fixture");
        var generation = gateway.GenerateAsync(
            CreateRequest(fixture.ProjectRoot, fixture.AssetRoot));
        await enqueueObserved.Task.WaitAsync(Timeout).ConfigureAwait(false);

        gateway.Dispose();
        var result = await generation.WaitAsync(Timeout).ConfigureAwait(false);

        False(result.IsSuccess,
            "应用关闭时活跃 UI waiter 应收到非成功终止结果");
        True(result.ErrorMessage?.Contains("应用正在关闭", StringComparison.Ordinal) == true,
            "关闭期结果应说明持久任务仍保留，而不是抛出 semaphore ObjectDisposedException");
    }

    private static GatewayFixture CreateFixture(string temporaryRoot, string testName)
    {
        var root = Path.Combine(temporaryRoot, testName, Guid.NewGuid().ToString("N"));
        var paths = new WorkspacePaths(Path.Combine(root, "app-data"));
        var projectRoot = Path.Combine(root, "project");
        var assetRoot = Path.Combine(projectRoot, "Assets");
        Directory.CreateDirectory(assetRoot);
        var store = new JsonGenerationTaskStore(paths.GenerationDirectory);
        var server = new McpServerProcessService(paths);
        var runtime = new CodexAgentRuntimeService("codex-fixture");
        var agentConnection = new AgentConnectionViewModel(
            server,
            runtime,
            selectedAgentKey: "codex",
            workingDirectory: projectRoot);
        var console = new WorkbenchConsoleService(paths);
        return new GatewayFixture(
            paths,
            projectRoot,
            assetRoot,
            store,
            agentConnection,
            console);
    }

    private static ArtWorkspaceGenerationRequest CreateRequest(
        string projectRoot,
        string assetRoot) =>
        new()
        {
            RequestId = Guid.NewGuid().ToString("N"),
            CreatedAtUtc = DateTimeOffset.UtcNow,
            ProjectRoot = projectRoot,
            AssetRoot = assetRoot,
            Instruction = "生成一个轴对齐的测试游戏美术资源。",
            OutputFrame = new ArtWorkspaceGenerationOutputFrameSnapshot
            {
                Name = "测试输出框",
                X = 40d,
                Y = 30d,
                Width = 256d,
                Height = 256d,
                PixelWidth = 256,
                PixelHeight = 256,
                BackgroundHex = "#20242C",
                // Deliberately exercise the compatibility normalization at the direct gateway boundary.
                OutputRelativeDirectory = "Generated/ArtWorkspace"
            }
        };

    private static string ReadSingleTargetPath(GenerationTask task)
    {
        var delivery = task.Constraints.Parameters["delivery"] as JsonObject
            ?? throw new InvalidOperationException("美术工作区任务缺少 parameters.delivery。");
        var targetPaths = delivery["targetPaths"] as JsonArray
            ?? throw new InvalidOperationException("美术工作区任务缺少 delivery.targetPaths。");
        return targetPaths.Single()?.GetValue<string>()
            ?? throw new InvalidOperationException("美术工作区任务没有唯一冻结目标路径。");
    }

    private static async Task ExpectCanceledAsync(Task task)
    {
        try
        {
            await task.WaitAsync(Timeout).ConfigureAwait(false);
            throw new InvalidOperationException("预期 UI waiter 被调用方取消，但任务正常完成。");
        }
        catch (OperationCanceledException)
        {
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

    private static void Equal<T>(T expected, T actual, string label)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException(
                $"{label}: expected '{expected}', actual '{actual}'.");
        }
    }

    private sealed record GatewayFixture(
        WorkspacePaths Paths,
        string ProjectRoot,
        string AssetRoot,
        JsonGenerationTaskStore Store,
        AgentConnectionViewModel AgentConnection,
        WorkbenchConsoleService Console);

    private sealed class RecordingProgress : IProgress<ArtWorkspaceGenerationProgress>
    {
        private readonly ConcurrentQueue<ArtWorkspaceGenerationProgress> _snapshots = new();

        public IReadOnlyList<ArtWorkspaceGenerationProgress> Snapshots => _snapshots.ToArray();

        public void Report(ArtWorkspaceGenerationProgress value) => _snapshots.Enqueue(value);
    }
}
