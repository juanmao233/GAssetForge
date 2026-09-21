using GAssetForge.App.Services;
using GAssetForge.App.Services.AgentRuntime;
using GAssetForge.App.ViewModels;
using GAssetForge.Core.ArtWorkspaces;
using GAssetForge.Core.Generation;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace GAssetForge.SmokeTests;

internal static class ArtWorkspaceConversationResetIntegrationSmokeTests
{
    public static void Run(string temporaryRoot)
    {
        var root = Path.Combine(temporaryRoot, "art-conversation-reset-integration");
        var paths = new WorkspacePaths(Path.Combine(root, "desktop-data"));
        var projects = new ProjectWorkspaceService();
        Directory.CreateDirectory(Path.Combine(root, "projects"));
        var projectA = projects.Create(Path.Combine(root, "projects"), "Current", "ui");
        var projectB = projects.Create(Path.Combine(root, "projects"), "Other", "ui");
        var settings = new UserSettingsService(paths);
        settings.Save(new UserSettingsModel
        {
            HasCompletedWorkflowSelection = true,
            ActiveWorkflowKey = "ui",
            LastProjectPath = projectA.Paths.RootDirectory
        });
        var editor = new EditorWorkspaceViewModel(
            new JsonProjectRepository(paths), new DesktopFileDialogService(), new ProjectHistoryService());
        var shell = new ShellViewModel(new ThemeService(), settings, editor);
        try
        {
            Equal(projectA.Paths.RootDirectory, shell.CurrentProjectPath,
                "Shell 必须真实激活隔离的当前项目");
            var store = GetField<IGenerationTaskStore>(shell, "_generationTaskStore");
            var pending = CreateTask(store, projectA.Paths.AssetDirectory, "pending");
            var claimed = CreateTask(store, projectA.Paths.AssetDirectory, "claimed");
            claimed = Await(store.ClaimAsync(claimed.Id, "reset-claimed-owner"));
            var failed = CreateTask(store, projectA.Paths.AssetDirectory, "failed");
            Await(store.ClaimAsync(failed.Id, "reset-failed-owner"));
            failed = Await(store.FailAsync(failed.Id, "reset-failed-owner", "fixture failed before clear"));
            var completed = CreateTask(store, projectA.Paths.AssetDirectory, "completed");
            Await(store.ClaimAsync(completed.Id, "reset-completed-owner"));
            var completedRelativePath = $"Final/ArtWorkspace/{completed.BatchId}/output.png";
            completed = Await(store.CompleteAsync(completed.Id, "reset-completed-owner", [completedRelativePath]));
            var otherProject = CreateTask(store, projectB.Paths.AssetDirectory, "other-project");
            var otherWorkflow = CreateTask(store, projectA.Paths.AssetDirectory, "other-workflow", "icon-design");
            var completedBefore = GenerationJson.SerializeTask(completed);
            var otherProjectBefore = GenerationJson.SerializeTask(otherProject);
            var otherWorkflowBefore = GenerationJson.SerializeTask(otherWorkflow);

            var finalPath = Path.Combine(projectA.Paths.AssetDirectory,
                completedRelativePath.Replace('/', Path.DirectorySeparatorChar));
            SaveImageFixture(finalPath);
            var finalBefore = File.ReadAllBytes(finalPath);
            var art = shell.ArtWorkspace;
            Equal(1, art.AddImagesAt([finalPath], 210d, 320d), "测试应先导入真实受管画布图片");
            var retainedImageId = art.Images.Single().Id;
            var retainedImagePath = art.Images.Single().AbsolutePath;
            art.OutputFrameName = "清除后仍保留的输出框";
            art.SetOutputFrame(450d, 550d, 320d, 240d, commit: true);
            art.InstructionDraft = "清除后准备重新发送的草稿";
            art.Conversation.Add(new ArtWorkspaceConversationMessageViewModel(new ArtWorkspaceConversationMessage
            {
                Id = Guid.NewGuid().ToString("N"),
                Role = ArtWorkspaceMessageRole.Agent,
                State = "generating",
                Text = "退出前未结束的旧对话",
                CreatedAtUtc = DateTimeOffset.UtcNow
            }));
            True(art.SaveNow(), "清除前历史与画布应真实保存");

            Await(GetField<GenerationBatchDispatchCoordinator>(shell, "_agentDispatchCoordinator").DisposeAsync().AsTask());
            var activeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var activeStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var unrelatedCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var invoked = new List<string>();
            var completionCount = 0;
            var coordinator = new GenerationBatchDispatchCoordinator(async (prompt, token) =>
            {
                var taskId = prompt.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    .First(line => line.StartsWith("- ", StringComparison.Ordinal))[2..];
                lock (invoked)
                {
                    invoked.Add(taskId);
                }

                if (taskId == completed.Id)
                {
                    activeStarted.TrySetResult();
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    }
                    finally
                    {
                        activeStopped.TrySetResult();
                    }
                }

                token.ThrowIfCancellationRequested();
                return new AgentTurnResult("reset-integration", taskId, "completed", null);
            });
            SetField(shell, "_agentDispatchCoordinator", coordinator);
            coordinator.DispatchCompleted += (_, _) =>
            {
                if (Interlocked.Increment(ref completionCount) == 2)
                {
                    unrelatedCompleted.TrySetResult();
                }
            };
            coordinator.TryEnqueue(completed.BatchId!, [completed.Id], isRetry: false);
            Await(activeStarted.Task);
            foreach (var task in new[] { pending, claimed, failed, otherProject, otherWorkflow })
            {
                coordinator.TryEnqueue(task.BatchId!, [task.Id], isRetry: false);
            }

            True(art.ClearConversationCommand.CanExecute(null), "旧任务阻塞时清除入口必须可执行");
            art.ClearConversationCommand.Execute(null);
            WaitUntil(() => !art.IsClearingConversation && art.Conversation.Count == 0,
                "真实 ClearConversationCommand 必须完成 Shell 的持久暂停和定向停止");
            Await(activeStopped.Task);
            Await(unrelatedCompleted.Task);

            foreach (var before in new[] { pending, claimed, failed })
            {
                var after = Await(store.GetAsync(before.Id))!;
                False(after.DispatchIntent?.Requested == true, "当前项目未完成美术任务必须撤回自动调度");
                Equal(before.Status, after.Status, "暂停不得改写当前项目任务状态");
                Equal(before.ClaimedBy, after.ClaimedBy, "暂停不得改写认领所有者");
                Equal(before.Constraints.Parameters.ToJsonString(), after.Constraints.Parameters.ToJsonString(),
                    "清除不得改写冻结交付合同");
            }

            Equal(completedBefore, GenerationJson.SerializeTask(Await(store.GetAsync(completed.Id))!),
                "虽然已完成批次的 Turn 需要停止，Completed 任务记录必须保持不变");
            Equal(otherProjectBefore, GenerationJson.SerializeTask(Await(store.GetAsync(otherProject.Id))!),
                "其它项目的美术任务必须逐字段保持不变");
            Equal(otherWorkflowBefore, GenerationJson.SerializeTask(Await(store.GetAsync(otherWorkflow.Id))!),
                "当前项目其它工作流任务必须逐字段保持不变");
            lock (invoked)
            {
                Equal(string.Join(',', new[] { completed.Id, otherProject.Id, otherWorkflow.Id }),
                    string.Join(',', invoked), "仅本项目美术排队项应被清除，其余工作流继续执行");
            }

            Equal(1, art.Images.Count, "清除对话不得清除画布图片");
            Equal(retainedImageId, art.Images.Single().Id, "清除后应保留原图片身份");
            True(File.Exists(retainedImagePath), "清除后受管图片文件应保留");
            True(finalBefore.SequenceEqual(File.ReadAllBytes(finalPath)), "清除不得更改 Final 原件");
            Equal("清除后仍保留的输出框", art.OutputFrameName, "清除不得更改输出框名称");
            Equal(450d, art.OutputFrameX, "清除不得更改输出框位置");
            Equal(320d, art.OutputFrameWidth, "清除不得更改输出框尺寸");
            Equal("清除后准备重新发送的草稿", art.InstructionDraft, "清除应保留当前草稿");
            var saved = new JsonArtWorkspaceRepository(paths).LoadOrCreate(projectA.Paths.RootDirectory).Document;
            Equal(0, saved.Conversation.Count, "清除必须实装到工作区持久文件，不能只清空视图");
            Equal(1, saved.Images.Count, "清除后的持久文件应保留画布图片");
        }
        finally
        {
            Await(shell.ShutdownAsync());
        }
    }

    private static GenerationTask CreateTask(
        IGenerationTaskStore store, string assetRoot, string name,
        string workflowId = ArtWorkspaceGenerationConstraintPackageBuilder.WorkflowId)
    {
        var batchId = Guid.NewGuid().ToString("N");
        var outputDirectory = $"Final/ArtWorkspace/{batchId}";
        var constraints = MaterialGenerationConstraintComposer.ReplaceDelivery(
            new GenerationConstraintPackage { WorkflowId = workflowId, Prompt = $"reset integration {name}" },
            new GenerationDeliveryPlan
            {
                AssetRoot = assetRoot,
                OutputDirectory = outputDirectory,
                SuggestedBaseName = "output",
                TargetPaths = [$"{outputDirectory}/output.png"]
            });
        return Await(store.CreateBatchAsync(new CreateGenerationBatchRequest
        {
            BatchId = batchId,
            DisplayName = name,
            DispatchRequested = true,
            Items = [new CreateGenerationBatchItemRequest
            {
                ItemId = Guid.NewGuid().ToString("N"), DisplayName = name, Constraints = constraints
            }]
        })).Single();
    }

    private static T GetField<T>(object owner, string name) =>
        (T)(owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(owner)
            ?? throw new InvalidOperationException($"缺少集成测试字段 {name}"));

    private static void SetField(object owner, string name, object value) =>
        (owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"缺少集成测试字段 {name}")).SetValue(owner, value);

    private static T Await<T>(Task<T> task)
    {
        Await((Task)task);
        return task.GetAwaiter().GetResult();
    }

    private static void Await(Task task)
    {
        WaitUntil(() => task.IsCompleted, "集成测试异步操作必须及时完成");
        task.GetAwaiter().GetResult();
    }

    private static void WaitUntil(Func<bool> predicate, string message)
    {
        var timer = Stopwatch.StartNew();
        while (!predicate())
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(10))
            {
                throw new InvalidOperationException(message);
            }

            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,
                new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(5);
        }
    }

    private static void SaveImageFixture(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var pixels = Enumerable.Repeat((byte)255, 4 * 4 * 4).ToArray();
        var bitmap = BitmapSource.Create(4, 4, 96d, 96d, PixelFormats.Bgra32, null, pixels, 16);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void False(bool condition, string message) => True(!condition, message);

    private static void Equal<T>(T expected, T actual, string message) =>
        True(EqualityComparer<T>.Default.Equals(expected, actual), $"{message}：预期 {expected}，实际 {actual}");
}
