using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.IO;
using GAssetForge.Core.Generation;
using GAssetForge.McpServer;

namespace GAssetForge.SmokeTests;

internal static class DirectToolInvocationSmokeTests
{
    public static async Task RunAllAsync()
    {
        var dataRoot = Path.Combine(
            Path.GetTempPath(),
            "GAssetForge.DirectToolInvocationSmokeTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataRoot);
        try
        {
            var store = new JsonGenerationTaskStore(Path.Combine(dataRoot, "Generation"));
            var task = await store.CreateAsync(new CreateGenerationTaskRequest
            {
                DisplayName = "manual bridge smoke",
                RequestedBy = "smoke-tests",
                Constraints = new GenerationConstraintPackage
                {
                    WorkflowId = "tilemap",
                    PreferredSkill = "imagegen",
                    Prompt = "manual bridge smoke",
                    Output = new GenerationOutputSpec
                    {
                        Width = 64,
                        Height = 64,
                        Count = 1,
                        Format = "png",
                        TransparentBackground = true
                    },
                    Rules = [],
                    Parameters = new JsonObject()
                }
            });

            var serverInfo = await InvokeAsync(dataRoot, "server_info", new { });
            Equal(0, serverInfo.ExitCode, "本地桥接 server_info 应成功");
            using (var serverInfoDocument = JsonDocument.Parse(serverInfo.Output))
            {
                var returnedDataRoot = serverInfoDocument.RootElement
                    .GetProperty("structuredContent")
                    .GetProperty("dataRoot")
                    .GetString();
                Equal(Path.GetFullPath(dataRoot), returnedDataRoot, "server_info 应使用隔离数据根");
            }

            var claimed = await InvokeAsync(
                dataRoot,
                "claim_generation_task",
                new { agentId = "codex-manual", taskId = task.Id });
            Equal(0, claimed.ExitCode, "本地桥接应可认领已知任务");
            var claimedTask = await store.GetAsync(task.Id);
            Equal(GenerationTaskStatus.Claimed, claimedTask?.Status, "桥接认领必须写入真实任务存储");
            Equal("codex-manual", claimedTask?.ClaimedBy, "桥接认领者必须保留");

            var failed = await InvokeAsync(
                dataRoot,
                "fail_generation_task",
                new
                {
                    agentId = "codex-manual",
                    taskId = task.Id,
                    failureMessage = "smoke-test cleanup"
                });
            Equal(0, failed.ExitCode, "本地桥接应可按同一状态机写入失败终态");
            var failedTask = await store.GetAsync(task.Id);
            Equal(GenerationTaskStatus.Failed, failedTask?.Status, "桥接失败回写必须进入真实终态");

            Throws<ArgumentException>(
                () => DirectToolInvocationExecutor.Parse(
                [
                    DirectToolInvocationExecutor.InvokeToolOption,
                    "create_generation_task",
                    DirectToolInvocationExecutor.ArgumentsBase64Option,
                    Encode(new { })
                ]),
                "本地桥接不得开放创建任意任务能力");

            await TilemapThreeOutputCompletionAcceptsEveryExactDeliveryTargetAsync(
                dataRoot,
                store);
        }
        finally
        {
            if (Directory.Exists(dataRoot))
            {
                Directory.Delete(dataRoot, recursive: true);
            }
        }
    }

    private static async Task TilemapThreeOutputCompletionAcceptsEveryExactDeliveryTargetAsync(
        string dataRoot,
        JsonGenerationTaskStore store)
    {
        const int imageSize = 64;
        const double diamondAngle = 53.13010235415598d;
        const string agentId = "codex-three-output-smoke";
        var assetRoot = Path.Combine(dataRoot, "Assets");
        Directory.CreateDirectory(assetRoot);
        string[] targetPaths =
        [
            "Generated/TilemapWorkflow/three-output-001.png",
            "Generated/TilemapWorkflow/three-output-002.png",
            "Generated/TilemapWorkflow/three-output-003.png"
        ];
        var constraints = MaterialGenerationConstraintComposer.Compose(
            TilemapConstraintPackageBuilder.Build(new TilemapGenerationConstraintInput
            {
                ArtWidth = imageSize,
                ArtHeight = imageSize,
                DiamondAngle = diamondAngle,
                OutputCount = targetPaths.Length,
                ContentPrompt = "三张可辨识的石质地表瓦片"
            }),
            new MaterialGenerationStyleSelection
            {
                GroupId = "three-output-style",
                Name = "三产物回归风格",
                Description = "用于验证 Tilemap 多产物完成链路。",
                Prompt = "readable hand-painted stone tile variants"
            },
            new GenerationDeliveryPlan
            {
                AssetRoot = assetRoot,
                OutputDirectory = "Generated/TilemapWorkflow",
                TargetPaths = targetPaths,
                SuggestedBaseName = "three-output"
            });
        var task = await store.CreateAsync(new CreateGenerationTaskRequest
        {
            DisplayName = "Tilemap 三产物完成回归",
            RequestedBy = "smoke-tests",
            Constraints = constraints
        });

        var claimed = await InvokeAsync(
            dataRoot,
            "claim_generation_task",
            new { agentId, taskId = task.Id });
        Equal(0, claimed.ExitCode, "三产物 Tilemap 任务应能被本地桥接认领");

        foreach (var targetPath in targetPaths)
        {
            var fullPath = Path.GetFullPath(Path.Combine(
                assetRoot,
                targetPath.Replace('/', Path.DirectorySeparatorChar)));
            Program.WritePngFixture(
                fullPath,
                imageSize,
                imageSize,
                includeAlpha: true,
                diamondAngle: diamondAngle,
                rotationAngle: 0d);
        }

        var completed = await InvokeAsync(
            dataRoot,
            "complete_generation_task",
            new { taskId = task.Id, agentId, assetPaths = targetPaths });
        Equal(
            0,
            completed.ExitCode,
            $"Tilemap output.count=3 且 assetPaths 与 delivery.targetPaths 精确一致时必须完成，不能报必须单张。桥接输出：{completed.Output}");

        var persisted = await store.GetAsync(task.Id);
        Equal(GenerationTaskStatus.Completed, persisted?.Status,
            "三个合规 Tilemap PNG 应共同写入 Completed 终态");
        Equal(targetPaths.Length, persisted?.RelativeAssetPaths.Length ?? 0,
            "Completed 任务应保留全部三个产物路径");
        for (var index = 0; index < targetPaths.Length; index++)
        {
            Equal(targetPaths[index], persisted!.RelativeAssetPaths[index],
                $"第 {index + 1} 个完成路径应保持 delivery.targetPaths 稳定顺序");
        }
    }

    private static Task<DirectToolInvocationResult> InvokeAsync(
        string dataRoot,
        string toolName,
        object arguments) =>
        DirectToolInvocationExecutor.ExecuteAsync(
        [
            "--data-root",
            dataRoot,
            DirectToolInvocationExecutor.InvokeToolOption,
            toolName,
            DirectToolInvocationExecutor.ArgumentsBase64Option,
            Encode(arguments)
        ]);

    private static string Encode(object value) => Convert.ToBase64String(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value)));

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message}。期望：{expected}，实际：{actual}");
        }
    }

    private static void Throws<TException>(Action action, string message) where TException : Exception
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
}
