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
        TilemapOutputCountBuildsVariantAwareConstraints();
        LegacyTasksRemainBatchNeutral();
    }

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

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message}：预期 {expected}，实际 {actual}。");
        }
    }
}
