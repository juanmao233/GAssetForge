using System.IO;
using GAssetForge.App.Services;
using GAssetForge.App.Services.AgentRuntime;
using GAssetForge.Core.Generation;

namespace GAssetForge.SmokeTests;

internal static class GenerationBatchTurnReconcilerSmokeTests
{
    public static async Task RunAllAsync()
    {
        await TransportStreamDisconnectKeepsPendingTasksForRecoveryAsync();
        await CompletedTurnWithoutClaimsKeepsPendingTasksForRecoveryAsync();
        await TerminalTasksStayImmutableAndActiveClaimsRemainUnresolvedAsync();
    }

    private static async Task TransportStreamDisconnectKeepsPendingTasksForRecoveryAsync()
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
                    ThreadId: "thread-transport-disconnect",
                    TurnId: "turn-transport-disconnect",
                    Status: "failed",
                    ErrorMessage:
                        "stream disconnected before completion: stream closed before response.completed"),
                failureMessage: null);

            var persisted = await store.ListBatchAsync(batchId);
            Equal(2, result.PendingCount,
                "Responses 流中断后未开始的任务必须保持 Pending 以便恢复调度");
            Equal(0, result.AutoFailedCount,
                "Responses 流中断不得由收束服务认领并失败 Pending 任务");
            False(result.AllTerminal,
                "仍有 Pending 任务的传输中断批次不得伪报终态");
            True(result.FailureDetail?.Contains(
                    "agent_transport_stream_disconnected",
                    StringComparison.Ordinal) == true,
                "传输中断必须暴露稳定错误码而不是美术约束失败文案");
            True(persisted.All(task =>
                    task.Status == GenerationTaskStatus.Pending &&
                    string.IsNullOrWhiteSpace(task.ClaimedBy) &&
                    string.IsNullOrWhiteSpace(task.FailureMessage)),
                "传输中断后原任务状态和所有权必须保持不变");
            Equal(tasks.Select(task => task.Id).ToArray(), persisted.Select(task => task.Id).ToArray(),
                "恢复等待不得创建或替换 generation task");
        }
        finally
        {
            DeleteTestRoot(testRoot);
        }
    }

    private static async Task CompletedTurnWithoutClaimsKeepsPendingTasksForRecoveryAsync()
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
            Equal(2, result.PendingCount, "Turn 完成但未认领时原任务应保持 Pending");
            Equal(0, result.ClaimedCount, "全 Pending 场景收束后不得遗留 Claimed 任务");
            Equal(0, result.CompletedCount, "未生成产物不得伪报 Completed");
            Equal(0, result.FailedCount, "未被领取的任务不得伪装成美术生成失败");
            Equal(0, result.AutoFailedCount, "收束服务不得再代理认领并失败 Pending 任务");
            False(result.AllTerminal, "保留 Pending 后批次必须保持可恢复状态");
            True(result.FailureDetail?.Contains("completed", StringComparison.OrdinalIgnoreCase) == true,
                "收束原因应保留 Turn completed 但未执行的事实");
            True(persisted.All(task =>
                    task.Status == GenerationTaskStatus.Pending &&
                    string.IsNullOrWhiteSpace(task.ClaimedBy) &&
                    string.IsNullOrWhiteSpace(task.FailureMessage)),
                "未认领任务的状态、所有权和失败字段必须保持不变");
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
            Equal(1, result.PendingCount, "混合批次中的 Pending 应保持可恢复");
            Equal(1, result.ClaimedCount, "外部 Agent 的在途认领必须保留");
            Equal(1, result.CompletedCount, "既有完成任务计数");
            Equal(1, result.FailedCount, "只统计 MCP/Harness 已明确写入的既有失败");
            Equal(0, result.AutoFailedCount, "收束不得自动失败原 Pending 任务");
            False(result.AllTerminal, "仍有 Claimed 任务时批次不得宣称全部终态");
            True(result.FailureDetail?.Contains("仍有 1 个任务处于 Claimed", StringComparison.Ordinal) == true,
                "汇总必须明确报告未收束的在途认领");

            Equal(completedBefore.Revision, completedAfter.Revision, "收束不得改写 Completed 修订号");
            Equal(completedBefore.ClaimedBy, completedAfter.ClaimedBy, "收束不得改写 Completed 所有者");
            Equal(failedBefore.Revision, failedAfter.Revision, "收束不得改写 Failed 修订号");
            Equal(failedBefore.FailureMessage, failedAfter.FailureMessage, "收束不得覆盖既有失败原因");
            Equal(claimedBefore.Revision, claimedAfter.Revision, "收束不得改写在途 Claimed 修订号");
            Equal(activeAgent, claimedAfter.ClaimedBy, "收束不得抢占外部 Agent 所有权");
            Equal(GenerationTaskStatus.Pending, pendingAfter.Status, "原 Pending 应保持可恢复状态");
            True(string.IsNullOrWhiteSpace(pendingAfter.ClaimedBy),
                "收束服务不得取得原 Pending 的所有权");
            True(string.IsNullOrWhiteSpace(pendingAfter.FailureMessage),
                "收束服务不得为原 Pending 伪造失败原因");
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
