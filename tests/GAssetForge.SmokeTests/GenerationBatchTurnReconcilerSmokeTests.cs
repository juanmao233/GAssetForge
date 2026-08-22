using System.IO;
using GAssetForge.App.Services;
using GAssetForge.App.Services.AgentRuntime;
using GAssetForge.Core.Generation;

namespace GAssetForge.SmokeTests;

internal static class GenerationBatchTurnReconcilerSmokeTests
{
    public static async Task RunAllAsync()
    {
        await CompletedTurnWithoutClaimsFailsPendingTasksForRetryAsync();
        await TerminalTasksStayImmutableAndActiveClaimsRemainUnresolvedAsync();
    }

    private static async Task CompletedTurnWithoutClaimsFailsPendingTasksForRetryAsync()
    {
        var testRoot = CreateTestRoot();
        try
        {
            var store = new JsonGenerationTaskStore(testRoot);
            var batchId = Guid.NewGuid().ToString("N");
            var tasks = await store.CreateBatchAsync(CreateBatchRequest(batchId, 2));
            var reconciler = new GenerationBatchTurnReconciler(store);

            var result = await reconciler.ReconcileAsync(
                batchId,
                new AgentTurnResult(
                    ThreadId: "thread-reconcile-smoke",
                    TurnId: "turn-completed-without-claims",
                    Status: "completed",
                    ErrorMessage: null),
                failureMessage: null);

            var persisted = await store.ListBatchAsync(batchId);
            Equal(2, result.TotalCount, "收束汇总任务总数");
            Equal(0, result.PendingCount, "Turn 结束后不得遗留 Pending 任务");
            Equal(0, result.ClaimedCount, "全 Pending 场景收束后不得遗留 Claimed 任务");
            Equal(0, result.CompletedCount, "未生成产物不得伪报 Completed");
            Equal(2, result.FailedCount, "未被领取的任务应转为可重试失败");
            Equal(2, result.AutoFailedCount, "应记录由收束服务自动失败的任务数");
            True(result.AllTerminal, "全部 Pending 已转失败后批次应处于终态");
            True(result.FailureDetail?.Contains("completed", StringComparison.OrdinalIgnoreCase) == true,
                "收束原因应保留 Turn completed 但未执行的事实");
            True(persisted.All(task => task.Status == GenerationTaskStatus.Failed),
                "所有未领取任务都应持久化为 Failed");
            True(persisted.All(task =>
                    string.Equals(
                        task.ClaimedBy,
                        GenerationBatchTurnReconciler.DispatchProxyAgentId,
                        StringComparison.Ordinal)),
                "自动失败任务必须由明确的 GAssetForge 调度代理认领");
            True(persisted.All(task => task.FailureMessage?.Contains(
                    "未被 Agent 领取或完成",
                    StringComparison.Ordinal) == true),
                "自动失败任务应记录可诊断、可重试原因");
            Equal(tasks.Select(task => task.Id).ToArray(), persisted.Select(task => task.Id).ToArray(),
                "收束不得替换原任务记录");
        }
        finally
        {
            DeleteTestRoot(testRoot);
        }
    }

    private static async Task TerminalTasksStayImmutableAndActiveClaimsRemainUnresolvedAsync()
    {
        var testRoot = CreateTestRoot();
        try
        {
            var store = new JsonGenerationTaskStore(testRoot);
            var batchId = Guid.NewGuid().ToString("N");
            var tasks = await store.CreateBatchAsync(CreateBatchRequest(batchId, 4));
            const string completingAgent = "codex:completed-fixture";
            const string failingAgent = "codex:failed-fixture";
            const string activeAgent = "codex:active-fixture";
            await store.ClaimAsync(tasks[0].Id, completingAgent);
            var completedBefore = await store.CompleteAsync(
                tasks[0].Id,
                completingAgent,
                ["Generated/completed-fixture.png"]);
            await store.ClaimAsync(tasks[1].Id, failingAgent);
            var failedBefore = await store.FailAsync(tasks[1].Id, failingAgent, "生成模型拒绝 fixture");
            var claimedBefore = await store.ClaimAsync(tasks[2].Id, activeAgent);

            var reconciler = new GenerationBatchTurnReconciler(store);
            var result = await reconciler.ReconcileAsync(
                batchId,
                new AgentTurnResult(
                    ThreadId: "thread-mixed-state",
                    TurnId: "turn-mixed-state",
                    Status: "completed",
                    ErrorMessage: null),
                failureMessage: null);

            var persisted = await store.ListBatchAsync(batchId);
            var completedAfter = persisted.Single(task => task.Id == completedBefore.Id);
            var failedAfter = persisted.Single(task => task.Id == failedBefore.Id);
            var claimedAfter = persisted.Single(task => task.Id == claimedBefore.Id);
            var pendingAfter = persisted.Single(task => task.Id == tasks[3].Id);
            Equal(4, result.TotalCount, "混合批次收束总数");
            Equal(0, result.PendingCount, "混合批次中的 Pending 应转为可重试失败");
            Equal(1, result.ClaimedCount, "外部 Agent 的在途认领必须保留");
            Equal(1, result.CompletedCount, "既有完成任务计数");
            Equal(2, result.FailedCount, "既有失败加自动失败任务计数");
            Equal(1, result.AutoFailedCount, "只应自动失败原 Pending 任务");
            False(result.AllTerminal, "仍有 Claimed 任务时批次不得宣称全部终态");
            True(result.FailureDetail?.Contains("仍有 1 个任务处于 Claimed", StringComparison.Ordinal) == true,
                "汇总必须明确报告未收束的在途认领");

            Equal(completedBefore.Revision, completedAfter.Revision, "收束不得改写 Completed 修订号");
            Equal(completedBefore.ClaimedBy, completedAfter.ClaimedBy, "收束不得改写 Completed 所有者");
            Equal(failedBefore.Revision, failedAfter.Revision, "收束不得改写 Failed 修订号");
            Equal(failedBefore.FailureMessage, failedAfter.FailureMessage, "收束不得覆盖既有失败原因");
            Equal(claimedBefore.Revision, claimedAfter.Revision, "收束不得改写在途 Claimed 修订号");
            Equal(activeAgent, claimedAfter.ClaimedBy, "收束不得抢占外部 Agent 所有权");
            Equal(GenerationTaskStatus.Failed, pendingAfter.Status, "原 Pending 应转换为 Failed");
            Equal(
                GenerationBatchTurnReconciler.DispatchProxyAgentId,
                pendingAfter.ClaimedBy,
                "仅原 Pending 应由调度代理认领后失败");
        }
        finally
        {
            DeleteTestRoot(testRoot);
        }
    }

    private static CreateGenerationBatchRequest CreateBatchRequest(string batchId, int count) => new()
    {
        BatchId = batchId,
        DisplayName = "Turn 收束 smoke 批次",
        RequestedBy = "GAssetForge SmokeTests",
        Items = Enumerable.Range(0, count)
            .Select(index => new CreateGenerationBatchItemRequest
            {
                ItemId = Guid.NewGuid().ToString("N"),
                DisplayName = $"收束任务 {index + 1}",
                Constraints = new GenerationConstraintPackage
                {
                    WorkflowId = "tilemap",
                    Prompt = $"生成收束测试瓦片 {index + 1}",
                    Output = new GenerationOutputSpec
                    {
                        Width = 64,
                        Height = 64,
                        Count = 1,
                        Format = "png",
                        TransparentBackground = true
                    }
                }
            })
            .ToArray()
    };

    private static string CreateTestRoot()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "GAssetForge.SmokeTests",
            "generation-turn-reconcile",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTestRoot(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
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
        if (expected is IEnumerable<string> expectedValues && actual is IEnumerable<string> actualValues)
        {
            if (!expectedValues.SequenceEqual(actualValues, StringComparer.Ordinal))
            {
                throw new InvalidOperationException(message);
            }

            return;
        }

        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message}：预期 {expected}，实际 {actual}。");
        }
    }
}
