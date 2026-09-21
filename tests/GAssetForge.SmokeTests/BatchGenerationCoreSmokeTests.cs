using System.IO;
using System.Text.Json.Nodes;
using GAssetForge.Core.Generation;

namespace GAssetForge.SmokeTests;

internal static class BatchGenerationCoreSmokeTests
{
    public static async Task RunAllAsync()
    {
        await BatchCreationIsAtomicAndKeepsStableOrderAsync();
        await BatchBoundariesRejectWithoutPartialWritesAsync();
        await RetryLineageIsAtomicIdempotentAndStopsAtFrozenLimitAsync();
        await AutomaticRetryBlockAndDispatchIntentPersistAsync();
        await AgentInfrastructureFailuresNeverCreateAutomaticRetryChildrenAsync();
        TilemapOutputCountBuildsVariantAwareConstraints();
        LegacyTasksRemainBatchNeutral();
    }

    private static async Task AgentInfrastructureFailuresNeverCreateAutomaticRetryChildrenAsync()
    {
        var testRoot = CreateTestRoot("agent-infrastructure-no-auto-retry");
        try
        {
            var store = new JsonGenerationTaskStore(testRoot);
            var parent = (await store.CreateBatchAsync(new CreateGenerationBatchRequest
            {
                BatchId = Guid.NewGuid().ToString("N"),
                DisplayName = "传输中断不得新建任务",
                DispatchRequested = true,
                Items =
                [
                    new CreateGenerationBatchItemRequest
                    {
                        ItemId = Guid.NewGuid().ToString("N"),
                        DisplayName = "保留原任务",
                        MaxAttempts = 4,
                        Constraints = CreateConstraints("保留原任务", 1)
                    }
                ]
            })).Single();
            const string agentId = "infrastructure-failure-fixture";
            await store.ClaimAsync(parent.Id, agentId);
            var failed = await store.FailAsync(
                parent.Id,
                agentId,
                new GenerationTaskFailure
                {
                    Message = "Agent Turn 已结束，但任务未被 Agent 领取或完成。",
                    Code = "agent_turn_incomplete",
                    Stage = "agent.dispatch",
                    Retryable = true
                });

            True(failed.FailureRetryable == true,
                "fixture 应模拟旧版本已经错误持久化为 retryable 的基础设施失败");
            True(GenerationFailureClassifier.IsAgentInfrastructureFailureCode(failed.FailureCode),
                "旧 agent_turn_incomplete 必须被识别为基础设施失败");
            await ThrowsAsync<InvalidOperationException>(
                () => store.CreateRetryBatchAsync(CreateRetryRequest(failed)),
                "即使旧记录带 retryable=true，也不得创建新的 generation task");
            Equal(1, (await store.ListAsync()).Count,
                "基础设施失败必须保留单一原任务，不得膨胀任务库");

            var classified = GenerationFailureClassifier.Classify(
                "Agent Turn 已结束，但任务未被 Agent 领取或完成。");
            False(classified.Retryable,
                "新版本不得再把 Agent 未认领分类成可自动重做的美术失败");
        }
        finally
        {
            DeleteTestRoot(testRoot);
        }
    }

    private static async Task AutomaticRetryBlockAndDispatchIntentPersistAsync()
    {
        var testRoot = CreateTestRoot("retry-dispatch-intent");
        try
        {
            var store = new JsonGenerationTaskStore(testRoot);
            var parent = (await store.CreateBatchAsync(new CreateGenerationBatchRequest
            {
                BatchId = Guid.NewGuid().ToString("N"),
                DisplayName = "持久化自动调度与人工接管",
                DispatchRequested = true,
                Items =
                [
                    new CreateGenerationBatchItemRequest
                    {
                        ItemId = Guid.NewGuid().ToString("N"),
                        DisplayName = "需要重做的瓦片",
                        MaxAttempts = 4,
                        Constraints = CreateConstraints("需要重做的瓦片", 1)
                    }
                ]
            })).Single();
            True(parent.DispatchIntent is { Requested: true, IsRetry: false },
                "首次批次必须持久化应用内调度意图");

            var failed = await FailGeometryAsync(store, parent, "retry-block-smoke-agent");
            var reason = "自动重做准备失败：Run manifest 目录暂时不可写";
            var blocked = (await store.BlockAutomaticRetryAsync([failed.Id], reason)).Single();
            Equal(reason, blocked.AutomaticRetryBlockedReason,
                "自动重做准备失败必须持久化人工接管原因");
            await ThrowsAsync<InvalidOperationException>(
                () => store.CreateRetryBatchAsync(CreateRetryRequest(blocked)),
                "被阻断的任务不得被另一条自动路径继续创建 child");

            var manualRequest = CreateRetryRequest(blocked) with
            {
                ManualOverride = true,
                DispatchRequested = false
            };
            var manual = await store.CreateRetryBatchAsync(manualRequest);
            True(manual.Created, "人工接管应能原子创建唯一后继任务");
            True(manual.Tasks.Single().DispatchIntent is { Requested: false, IsRetry: true },
                "人工接力 child 不得在重启后自动拉起 Agent Turn");
            var persistedParent = await store.GetAsync(blocked.Id)
                ?? throw new InvalidOperationException("人工接管父任务丢失。");
            True(!string.IsNullOrWhiteSpace(persistedParent.SupersededByTaskId),
                "人工接管必须原子回写父任务后继关系");
            True(string.IsNullOrWhiteSpace(persistedParent.AutomaticRetryBlockedReason),
                "父任务被人工 child 接替后应清除活动阻断标记");
        }
        finally
        {
            DeleteTestRoot(testRoot);
        }
    }

    private static async Task RetryLineageIsAtomicIdempotentAndStopsAtFrozenLimitAsync()
    {
        var testRoot = CreateTestRoot("retry-lineage");
        try
        {
            var store = new JsonGenerationTaskStore(testRoot);
            var initial = (await store.CreateBatchAsync(new CreateGenerationBatchRequest
            {
                BatchId = Guid.NewGuid().ToString("N"),
                DisplayName = "自动重做谱系",
                DispatchRequested = true,
                Items =
                [
                    new CreateGenerationBatchItemRequest
                    {
                        ItemId = Guid.NewGuid().ToString("N"),
                        DisplayName = "轴对齐方形瓦片",
                        MaxAttempts = 4,
                        Constraints = CreateConstraints("轴对齐方形瓦片", 1)
                    }
                ]
            })).Single();
            Equal(1, initial.RetryLineage?.AttemptNumber, "首次尝试编号");
            Equal(4, initial.RetryLineage?.MaxAttempts, "默认三次重做应冻结为四次总尝试");
            Equal(initial.Id, initial.RetryLineage?.RootTaskId, "首次尝试应成为谱系根");

            var agentId = "retry-smoke-agent";
            var failed = await FailGeometryAsync(store, initial, agentId);
            var firstRetryRequest = CreateRetryRequest(failed);
            var concurrent = await Task.WhenAll(
                Enumerable.Range(0, 20)
                    .Select(_ => store.CreateRetryBatchAsync(firstRetryRequest)));
            var childIds = concurrent
                .SelectMany(result => result.Tasks)
                .Select(task => task.Id)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            Equal(1, childIds.Length, "并发重试必须只创建一个后继任务");
            Equal(1, concurrent.Count(result => result.Created), "并发重试必须只有一次原子创建成功");
            Equal(2, (await store.ListAsync()).Count, "并发重试不得产生重复 child");

            var attempt = await store.GetAsync(childIds[0])
                ?? throw new InvalidOperationException("找不到第二次尝试。");
            for (var expectedAttempt = 2; expectedAttempt <= 4; expectedAttempt++)
            {
                Equal(expectedAttempt, attempt.RetryLineage?.AttemptNumber, $"第 {expectedAttempt} 次尝试编号");
                Equal(initial.Id, attempt.RetryLineage?.RootTaskId, "后继任务必须保留根任务 ID");
                if (expectedAttempt == 4)
                {
                    break;
                }

                var failedAttempt = await FailGeometryAsync(store, attempt, agentId);
                var retry = await store.CreateRetryBatchAsync(CreateRetryRequest(failedAttempt));
                True(retry.Created, $"第 {expectedAttempt + 1} 次尝试应成功创建");
                attempt = retry.Tasks.Single();
            }

            var exhausted = await FailGeometryAsync(store, attempt, agentId);
            await ThrowsAsync<InvalidOperationException>(
                () => store.CreateRetryBatchAsync(CreateRetryRequest(exhausted)),
                "第四次失败后必须停止自动重做");
            Equal(4, (await store.ListAsync()).Count, "三次额外重做应最多保留四个审计任务");
        }
        finally
        {
            DeleteTestRoot(testRoot);
        }
    }

    private static async Task<GenerationTask> FailGeometryAsync(
        JsonGenerationTaskStore store,
        GenerationTask task,
        string agentId)
    {
        var claimed = await store.ClaimAsync(task.Id, agentId);
        return await store.FailAsync(
            claimed.Id,
            agentId,
            new GenerationTaskFailure
            {
                Message =
                    "output_geometry_validation_failed: tilemap_geometry_aspect_ratio_mismatch; actualWidth=384; actualHeight=448",
                Code = "output_geometry_validation_failed",
                SubCode = "tilemap_geometry_aspect_ratio_mismatch",
                Stage = "output.geometry_validation",
                Retryable = true
            });
    }

    private static CreateGenerationRetryBatchRequest CreateRetryRequest(GenerationTask parent) => new()
    {
        BatchId = Guid.NewGuid().ToString("N"),
        DisplayName = $"重做 {parent.RetryLineage?.AttemptNumber + 1}",
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

    private static async Task BatchCreationIsAtomicAndKeepsStableOrderAsync()
    {
        var testRoot = CreateTestRoot("atomic");
        try
        {
            var store = new JsonGenerationTaskStore(testRoot);
            var batchId = Guid.NewGuid().ToString("N");
            var firstItemId = Guid.NewGuid().ToString("N");
            var secondItemId = Guid.NewGuid().ToString("N");
            var created = await store.CreateBatchAsync(new CreateGenerationBatchRequest
            {
                BatchId = batchId,
                DisplayName = "Tilemap 批量生成",
                RequestedBy = "batch-smoke-tests",
                Items =
                [
                    new CreateGenerationBatchItemRequest
                    {
                        ItemId = firstItemId,
                        DisplayName = "石质地表 × 2",
                        Constraints = CreateConstraints("石质地表", 2)
                    },
                    new CreateGenerationBatchItemRequest
                    {
                        ItemId = secondItemId,
                        DisplayName = "苔藓地表 × 1",
                        Constraints = CreateConstraints("苔藓地表", 1)
                    }
                ]
            });

            Equal(2, created.Count, "批次创建任务数");
            Equal(batchId, created[0].BatchId, "第一行批次 ID");
            Equal(firstItemId, created[0].BatchItemId, "第一行项目 ID");
            Equal(0, created[0].BatchItemIndex, "第一行稳定索引");
            Equal(2, created[0].Constraints.Output.Count, "第一行输出数量");
            Equal(secondItemId, created[1].BatchItemId, "第二行项目 ID");
            Equal(1, created[1].BatchItemIndex, "第二行稳定索引");

            var listed = await store.ListBatchAsync(batchId);
            Equal(2, listed.Count, "按批次读取任务数");
            Equal(firstItemId, listed[0].BatchItemId, "按批次读取保持表格顺序");
            Equal(secondItemId, listed[1].BatchItemId, "按批次读取第二行顺序");

            await ThrowsAsync<InvalidOperationException>(
                () => store.CreateBatchAsync(new CreateGenerationBatchRequest
                {
                    BatchId = batchId,
                    DisplayName = "重复批次",
                    Items = [NewBatchItem("不得追加", 1)]
                }),
                "同一批次 ID 不得被二次发布");
            Equal(2, (await store.ListBatchAsync(batchId)).Count, "重复发布不得追加半批任务");
        }
        finally
        {
            DeleteTestRoot(testRoot);
        }
    }

    private static async Task BatchBoundariesRejectWithoutPartialWritesAsync()
    {
        var testRoot = CreateTestRoot("boundaries");
        try
        {
            var store = new JsonGenerationTaskStore(testRoot);
            var baseline = await store.CreateAsync(new CreateGenerationTaskRequest
            {
                DisplayName = "已有任务",
                Constraints = CreateConstraints("已有任务", 1)
            });

            await ThrowsAsync<ArgumentException>(
                () => store.CreateBatchAsync(new CreateGenerationBatchRequest
                {
                    BatchId = Guid.NewGuid().ToString("N"),
                    DisplayName = "包含无效行",
                    Items =
                    [
                        NewBatchItem("有效行", 1),
                        NewBatchItem("超出单行上限", GenerationInputValidator.MaximumOutputCount + 1)
                    ]
                }),
                "批次包含无效行时必须拒绝");

            var afterInvalidRow = await store.ListAsync();
            Equal(1, afterInvalidRow.Count, "无效批次不得留下部分任务");
            Equal(baseline.Id, afterInvalidRow.Single().Id, "无效批次不得改写已有任务");

            var tooManyOutputs = Enumerable.Range(
                    0,
                    GenerationInputValidator.MaximumBatchOutputCount /
                    GenerationInputValidator.MaximumOutputCount + 1)
                .Select(index => NewBatchItem(
                    $"批次总量边界 {index}",
                    GenerationInputValidator.MaximumOutputCount))
                .ToArray();
            await ThrowsAsync<ArgumentException>(
                () => store.CreateBatchAsync(new CreateGenerationBatchRequest
                {
                    BatchId = Guid.NewGuid().ToString("N"),
                    DisplayName = "批次总量过大",
                    Items = tooManyOutputs
                }),
                "批次总产物超过上限时必须拒绝");

            Equal(1, (await store.ListAsync()).Count, "总量越界批次不得写入任何任务");
        }
        finally
        {
            DeleteTestRoot(testRoot);
        }
    }

    private static void TilemapOutputCountBuildsVariantAwareConstraints()
    {
        var package = TilemapConstraintPackageBuilder.Build(new TilemapGenerationConstraintInput
        {
            ContentPrompt = "同一套古代砂岩地表",
            OutputCount = 3
        });

        Equal(3, package.Output.Count, "Tilemap 输出数量");
        True(package.Prompt.Contains("3 个同类但可辨识", StringComparison.Ordinal),
            "多产物提示必须明确变体语义");
        True(package.Rules.Any(rule =>
                rule.Key == "tilemap.canvas.exact-size" &&
                rule.Instruction.Contains("不得复制同一图片", StringComparison.Ordinal)),
            "多产物精确画幅规则必须禁止复制冒充");
    }

    private static void LegacyTasksRemainBatchNeutral()
    {
        var legacy = new GenerationTask
        {
            Id = Guid.NewGuid().ToString("N"),
            Revision = 1,
            DisplayName = "旧任务",
            Status = GenerationTaskStatus.Pending,
            Constraints = CreateConstraints("旧任务", 1),
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        var json = GenerationJson.SerializeTask(legacy);
        var restored = GenerationJson.DeserializeTask(json);

        Equal<string?>(null, restored.BatchId, "旧任务 BatchId");
        Equal<string?>(null, restored.BatchItemId, "旧任务 BatchItemId");
        Equal<int?>(null, restored.BatchItemIndex, "旧任务 BatchItemIndex");
    }

    private static CreateGenerationBatchItemRequest NewBatchItem(string displayName, int count) => new()
    {
        ItemId = Guid.NewGuid().ToString("N"),
        DisplayName = displayName,
        Constraints = CreateConstraints(displayName, count)
    };

    private static GenerationConstraintPackage CreateConstraints(string prompt, int count) => new()
    {
        WorkflowId = TilemapConstraintPackageBuilder.WorkflowId,
        Prompt = prompt,
        Output = new GenerationOutputSpec
        {
            Width = 512,
            Height = 256,
            Count = count,
            Format = "png",
            TransparentBackground = true
        },
        Parameters = new JsonObject()
    };

    private static string CreateTestRoot(string suffix) => Path.Combine(
        Path.GetTempPath(),
        "GAssetForge.SmokeTests",
        "batch-generation-core",
        suffix,
        Guid.NewGuid().ToString("N"));

    private static void DeleteTestRoot(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

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
