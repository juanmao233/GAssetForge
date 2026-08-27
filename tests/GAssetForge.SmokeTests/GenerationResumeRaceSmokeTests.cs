using System.IO;
using System.Reflection;
using System.Windows.Threading;
using GAssetForge.App.Services;
using GAssetForge.App.Services.AgentRuntime;
using GAssetForge.App.ViewModels;
using GAssetForge.Core.Generation;

namespace GAssetForge.SmokeTests;

internal static class GenerationResumeRaceSmokeTests
{
    public static void Run()
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            "GAssetForge.GenerationResumeRaceSmokeTests",
            Guid.NewGuid().ToString("N"));
        var paths = new WorkspacePaths(testRoot);
        var editor = new EditorWorkspaceViewModel(
            new JsonProjectRepository(paths),
            new DesktopFileDialogService(),
            new ProjectHistoryService());
        var shell = new ShellViewModel(
            new ThemeService(),
            new UserSettingsService(paths),
            editor);

        const string requestedBatchId = "resume-race-original-batch";
        const string replacementBatchId = "resume-race-replacement-batch";
        var coordinatorField = RequireField(
            typeof(ShellViewModel),
            "_agentDispatchCoordinator");
        var recoveryGateField = RequireField(
            typeof(ShellViewModel),
            "_dispatchRecoveryGate");
        var currentBatchField = RequireField(
            typeof(AssetGenerationViewModel),
            "_currentBatchId");
        var resumeMethod = typeof(ShellViewModel).GetMethod(
                               "ReconnectAndContinueGenerationBatchAsync",
                               BindingFlags.Instance | BindingFlags.NonPublic)
                           ?? throw new InvalidOperationException(
                               "未找到 Shell 重新连接续派编排入口。");
        var originalCoordinator =
            (GenerationBatchDispatchCoordinator?)coordinatorField.GetValue(shell)
            ?? throw new InvalidOperationException("Shell 缺少原 Agent 调度器。");
        var blockingTurnStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var blockingCoordinator = new GenerationBatchDispatchCoordinator(
            async (_, cancellationToken) =>
            {
                blockingTurnStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
                    .ConfigureAwait(false);
                return new AgentTurnResult(
                    ThreadId: null,
                    TurnId: null,
                    Status: "completed",
                    ErrorMessage: null);
            });
        var recoveryGate = (SemaphoreSlim?)recoveryGateField.GetValue(shell)
                           ?? throw new InvalidOperationException("Shell 缺少调度恢复互斥锁。");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task? resumeTask = null;
        var recoveryGateHeld = false;

        try
        {
            coordinatorField.SetValue(shell, blockingCoordinator);
            Equal(
                GenerationBatchDispatchEnqueueResult.Enqueued,
                blockingCoordinator.TryEnqueue(
                    requestedBatchId,
                    ["resume-race-task"],
                    isRetry: false),
                "竞态测试批次应先占用旧调度生命周期");
            blockingTurnStarted.Task.WaitAsync(TimeSpan.FromSeconds(3))
                .GetAwaiter()
                .GetResult();

            currentBatchField.SetValue(shell.AssetGeneration, requestedBatchId);
            Equal(requestedBatchId, shell.AssetGeneration.CurrentBatchId,
                "竞态测试应从用户请求的原批次开始");

            recoveryGate.Wait(cancellation.Token);
            recoveryGateHeld = true;
            resumeTask = (Task?)resumeMethod.Invoke(
                             shell,
                             [
                                 new GenerationBatchResumeRequest(
                                     requestedBatchId,
                                     ["resume-race-task"],
                                     IsRetry: false),
                                 cancellation.Token
                             ])
                         ?? throw new InvalidOperationException(
                             "重新连接续派入口没有返回异步任务。");
            False(resumeTask.IsCompleted,
                "恢复互斥锁被占用时，显式续派请求应等待而不能越过互斥区");

            // Simulate the automatic recovery path switching CurrentBatchId while it owns
            // _dispatchRecoveryGate. Once the manual request acquires the gate, it must reject
            // the stale request before waiting on the old batch or rebuilding the Runtime.
            currentBatchField.SetValue(shell.AssetGeneration, replacementBatchId);
            recoveryGate.Release();
            recoveryGateHeld = false;

            PumpDispatcherUntil(
                () => resumeTask.IsCompleted,
                TimeSpan.FromSeconds(3),
                "当前批次在等待恢复锁期间切换后，旧批次续派没有立即终止");
            True(resumeTask.IsFaulted,
                "当前批次切换后的旧续派请求必须以明确错误结束");
            var failure = resumeTask.Exception?.GetBaseException();
            True(failure is InvalidOperationException &&
                 failure.Message.Contains("当前生成批次已经切换", StringComparison.Ordinal),
                $"旧批次续派应报告批次切换，而不是进入 Runtime 重连：{failure?.Message}");
            False(blockingCoordinator.WaitForBatchReleasedAsync(requestedBatchId).IsCompleted,
                "批次切换复核必须发生在等待旧调度生命周期之前");
            Equal(replacementBatchId, shell.AssetGeneration.CurrentBatchId,
                "拒绝旧续派请求不得覆盖自动恢复选中的新批次");
        }
        finally
        {
            if (recoveryGateHeld)
            {
                recoveryGate.Release();
            }

            cancellation.Cancel();
            blockingCoordinator.ReleaseBatch(requestedBatchId);
            if (resumeTask is { IsCompleted: false })
            {
                PumpDispatcherUntil(
                    () => resumeTask.IsCompleted,
                    TimeSpan.FromSeconds(3),
                    "清理竞态测试时旧续派请求仍未退出");
            }

            coordinatorField.SetValue(shell, originalCoordinator);
            blockingCoordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();
            shell.Shutdown();
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    private static FieldInfo RequireField(Type type, string fieldName) =>
        type.GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException($"{type.Name} 缺少测试所需字段 {fieldName}。");

    private static void PumpDispatcherUntil(
        Func<bool> condition,
        TimeSpan timeout,
        string message)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            Dispatcher.CurrentDispatcher.Invoke(
                static () => { },
                DispatcherPriority.ApplicationIdle);
            Thread.Sleep(10);
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
            throw new InvalidOperationException($"{message}：预期 {expected}，实际 {actual}。");
        }
    }
}
