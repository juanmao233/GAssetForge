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
        TransportInterruptionKeepsSameWaiterUntilResumedTaskCompletesAsync(temporaryRoot)
            .GetAwaiter()
            .GetResult();
        DisposeDuringActiveGenerationDoesNotRaceSemaphoreAsync(temporaryRoot)
            .GetAwaiter()
            .GetResult();
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
            (batchId, taskIds) =>
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

        var generation = gateway.GenerateAsync(
            CreateRequest(fixture.ProjectRoot, fixture.AssetRoot),
            progress,
            cancellation.Token);
        var published = await enqueueObserved.Task.WaitAsync(Timeout).ConfigureAwait(false);

        False(manifestTokenCanBeCanceled,
            "持久任务创建后 Manifest 写入不得再受 UI cancellation token 中断");
        True(gateway.OwnsBatch(published.BatchId),
            "Manifest 写入失败后必须先保留批次 owner 再继续派发");
        False(generation.IsCompleted,
            "QueueStopped 只能暂停自动派发，不能完成 UI waiter 或报告任务失败");
        var persisted = await fixture.Store.GetAsync(published.TaskId).ConfigureAwait(false)
            ?? throw new InvalidOperationException("找不到 Manifest 失败后已经持久化的美术工作区任务。");
        Equal(GenerationTaskStatus.Pending, persisted.Status,
            "QueueStopped 后的持久任务状态");
        True(progress.Snapshots.Any(snapshot => snapshot.State == "publishing-warning"),
            "Manifest 写入失败应提供非终态警告进度");
        True(progress.Snapshots.Any(snapshot => snapshot.State == "interrupted"),
            "QueueStopped 应向 UI 报告可恢复中断状态");

        cancellation.Cancel();
        await ExpectCanceledAsync(generation).ConfigureAwait(false);
        True(gateway.OwnsBatch(published.BatchId),
            "仅取消当前 UI 等待不得删除已持久化任务 owner");
    }

    private static async Task TransportInterruptionKeepsSameWaiterUntilResumedTaskCompletesAsync(
        string temporaryRoot)
    {
        var fixture = CreateFixture(temporaryRoot, "transport-interruption-resume");
        using var agentConnection = fixture.AgentConnection;
        using var console = fixture.Console;
        var enqueueObserved = new TaskCompletionSource<(string BatchId, string TaskId)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var gateway = new ProductionArtWorkspaceGenerationGateway(
            fixture.Paths,
            fixture.Store,
            agentConnection,
            (batchId, taskIds) =>
            {
                enqueueObserved.TrySetResult((batchId, taskIds.Single()));
                return GenerationBatchDispatchEnqueueResult.Enqueued;
            },
            console,
            isConnected: static () => true,
            isTurnRunning: static () => false,
            agentDisplayName: static () => "Codex fixture");
        var progress = new RecordingProgress();
        using var cancellation = new CancellationTokenSource();
        var generation = gateway.GenerateAsync(
            CreateRequest(fixture.ProjectRoot, fixture.AssetRoot),
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
        False(generation.IsCompleted,
            "Responses/SSE 中断后不得完成原 UI waiter 或显示生成失败");
        True(progress.Snapshots.Any(snapshot => snapshot.State == "interrupted"),
            "传输中断应显示等待续派状态");
        False(progress.Snapshots
                .Where(snapshot => snapshot.State == "interrupted")
                .Any(snapshot => snapshot.AgentDescription?.Contains("完成单图生成", StringComparison.Ordinal) == true),
            "传输中断的本地兜底描述不得误报图片已经完成");

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
        var result = await generation.WaitAsync(Timeout).ConfigureAwait(false);

        True(result.IsSuccess,
            "续派同一持久任务完成后，原 UI waiter 应收到成功结果");
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
            (_, _) =>
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
