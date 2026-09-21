using GAssetForge.Core.Generation;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GAssetForge.SmokeTests;

internal static class GenerationDispatchSuspensionSmokeTests
{
    public static async Task RunAsync(string temporaryRoot)
    {
        var storageRoot = Path.Combine(temporaryRoot, "generation-dispatch-suspension");
        IGenerationTaskStore store = new JsonGenerationTaskStore(storageRoot);
        var pending = await CreateTaskAsync(store, "pending", "project-a");
        var claimed = await CreateTaskAsync(store, "claimed", "project-a");
        claimed = await store.ClaimAsync(claimed.Id, "claim-owner");
        var failed = await CreateTaskAsync(store, "failed", "project-a");
        await store.ClaimAsync(failed.Id, "failure-owner");
        failed = await store.FailAsync(failed.Id, "failure-owner", new GenerationTaskFailure
        {
            Code = "quality-check-failed",
            Message = "fixture retryable failure",
            Retryable = true
        });
        var otherProject = await CreateTaskAsync(store, "untouched", "project-b");
        var before = new[] { pending, claimed, failed };
        const string reason = "用户清除美术对话，暂停历史任务自动调度。";
        var suspended = await store.SuspendAutomaticDispatchAsync(
            [pending.Id, claimed.Id, failed.Id, pending.Id], reason);

        Equal(3, suspended.Count, "重复任务 ID 必须幂等去重");
        for (var index = 0; index < before.Length; index++)
        {
            var original = before[index];
            var actual = suspended[index];
            Equal(false, actual.DispatchIntent?.Requested, "暂停后不得在重启时再次自动调度");
            Equal(original.Status, actual.Status, "暂停不得改变任务结果状态");
            Equal(original.ClaimedBy, actual.ClaimedBy, "暂停不得抢占或移除原认领者");
            Equal(original.ClaimedAtUtc, actual.ClaimedAtUtc, "暂停不得改变认领时间");
            Equal(original.BatchId, actual.BatchId, "暂停不得替换批次");
            Equal(JsonSerializer.Serialize(original.Constraints), JsonSerializer.Serialize(actual.Constraints),
                "暂停不得改写冻结约束或产物路径");
            Equal(original.Revision + 1, actual.Revision, "首次暂停只增加一次修订");
            if (original.Status == GenerationTaskStatus.Claimed)
            {
                Equal(original.UpdatedAtUtc, actual.UpdatedAtUtc, "暂停不得意外延长原认领租约");
            }
        }

        Equal(reason, suspended[2].AutomaticRetryBlockedReason, "可恢复失败须记录自动重试阻断原因");
        Equal(null, suspended[0].AutomaticRetryBlockedReason, "Pending 保持旧持久化模型的阻断字段合同");
        var repeated = await store.SuspendAutomaticDispatchAsync(
            before.Select(task => task.Id).ToArray(), reason);
        Equal(JsonSerializer.Serialize(suspended), JsonSerializer.Serialize(repeated),
            "重复暂停必须完全幂等，不增加修订或改写时间");

        IGenerationTaskStore reopened = new JsonGenerationTaskStore(storageRoot);
        foreach (var original in before)
        {
            var persisted = await reopened.GetAsync(original.Id)
                ?? throw new InvalidOperationException("暂停后原任务丢失。");
            Equal(false, persisted.DispatchIntent?.Requested, "重开 store 后必须保持暂停");
        }
        Equal(JsonSerializer.Serialize(otherProject), JsonSerializer.Serialize(await reopened.GetAsync(otherProject.Id)),
            "清除选定任务不得影响其它项目或未选中任务");

        var lateFailure = await reopened.FailAsync(claimed.Id, "claim-owner", new GenerationTaskFailure
        {
            Code = "quality-check-failed",
            Message = "late retryable failure after suspension",
            Retryable = true
        });
        var retry = new CreateGenerationRetryBatchRequest
        {
            BatchId = Guid.NewGuid().ToString("N"),
            DisplayName = "suspended retry",
            Items =
            [
                new CreateGenerationRetryBatchItemRequest
                {
                    ParentTaskId = lateFailure.Id,
                    ItemId = Guid.NewGuid().ToString("N"),
                    DisplayName = lateFailure.DisplayName,
                    Constraints = lateFailure.Constraints
                }
            ]
        };
        await ThrowsAsync<InvalidOperationException>(() => reopened.CreateRetryBatchAsync(retry),
            "暂停后晚到的失败也不得被另一条自动路径重试");
        var manual = await reopened.CreateRetryBatchAsync(retry with { ManualOverride = true });
        Equal(true, manual.Created, "暂停后仍须允许用户显式人工恢复原任务");

        var untouched = await CreateTaskAsync(store, "missing-id-atomicity", "project-a");
        await ThrowsAsync<KeyNotFoundException>(
            () => store.SuspendAutomaticDispatchAsync([untouched.Id, Guid.NewGuid().ToString("N")], reason),
            "找不到任一精确 ID 时必须拒绝整个暂停");
        Equal(JsonSerializer.Serialize(untouched), JsonSerializer.Serialize(await store.GetAsync(untouched.Id)),
            "部分 ID 无效不得先改写其它有效任务");
        await ThrowsAsync<ArgumentException>(
            () => store.SuspendAutomaticDispatchAsync([], reason), "不得接受空暂停范围");
        await ThrowsAsync<ArgumentException>(
            () => store.SuspendAutomaticDispatchAsync([untouched.Id], "  "), "暂停必须有可审计原因");
        await LegacyTaskSuspensionExplicitlyWithdrawsDispatchAsync(temporaryRoot);
    }

    private static async Task LegacyTaskSuspensionExplicitlyWithdrawsDispatchAsync(string temporaryRoot)
    {
        var storageRoot = Path.Combine(temporaryRoot, "legacy-generation-dispatch-suspension");
        var concreteStore = new JsonGenerationTaskStore(storageRoot);
        var selected = await CreateTaskAsync(concreteStore, "legacy-selected", "project-a");
        var unselected = await CreateTaskAsync(concreteStore, "legacy-unselected", "project-b");
        await concreteStore.ClaimAsync(selected.Id, "legacy-owner");
        await concreteStore.ClaimAsync(unselected.Id, "legacy-owner");
        var document = JsonNode.Parse(await File.ReadAllTextAsync(concreteStore.StoreFilePath))!.AsObject();
        foreach (var task in document["tasks"]!.AsArray())
        {
            task!["dispatchIntent"] = null;
        }
        await File.WriteAllTextAsync(concreteStore.StoreFilePath, document.ToJsonString());

        IGenerationTaskStore store = new JsonGenerationTaskStore(storageRoot);
        var suspended = (await store.SuspendAutomaticDispatchAsync([selected.Id], "用户暂停旧版任务")).Single();
        Equal(false, suspended.DispatchIntent?.Requested, "显式暂停旧版 null 意图也必须落盘为 false");
        Equal(null, (await store.GetAsync(unselected.Id))!.DispatchIntent,
            "未选中的 legacy 任务仍保留原有 null 兼容语义");
        var failure = new GenerationTaskFailure
        {
            Code = "quality-check-failed",
            Message = "legacy late retryable failure",
            Retryable = true
        };
        var selectedFailed = await store.FailAsync(selected.Id, "legacy-owner", failure);
        var unselectedFailed = await store.FailAsync(unselected.Id, "legacy-owner", failure);
        await ThrowsAsync<InvalidOperationException>(
            () => store.CreateRetryBatchAsync(CreateRetryRequest(selectedFailed)),
            "显式暂停的 legacy 任务晚到失败也不得自动重试");
        Equal(true, (await store.CreateRetryBatchAsync(CreateRetryRequest(unselectedFailed))).Created,
            "未暂停 legacy 任务的原有自动重试兼容行为不得被扩大改变");
    }

    private static CreateGenerationRetryBatchRequest CreateRetryRequest(GenerationTask parent) => new()
    {
        BatchId = Guid.NewGuid().ToString("N"),
        DisplayName = "legacy retry fixture",
        Items =
        [
            new CreateGenerationRetryBatchItemRequest
            {
                ParentTaskId = parent.Id,
                ItemId = Guid.NewGuid().ToString("N"),
                DisplayName = parent.DisplayName,
                Constraints = parent.Constraints
            }
        ]
    };

    private static Task<GenerationTask> CreateTaskAsync(
        IGenerationTaskStore store,
        string name,
        string project) => store.CreateAsync(new CreateGenerationTaskRequest
    {
        DisplayName = name,
        DispatchRequested = true,
        MaxAttempts = 3,
        Constraints = new GenerationConstraintPackage
        {
            WorkflowId = "art-workspace",
            Prompt = "fixture instruction",
            Parameters = new System.Text.Json.Nodes.JsonObject
            {
                ["frozenProject"] = project,
                ["frozenTarget"] = $"Assets/Final/{name}.png"
            }
        }
    });

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

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message} Expected: {expected}; Actual: {actual}");
        }
    }
}
