using System.IO;
using System.Windows.Threading;
using GAssetForge.App.Services;
using GAssetForge.App.ViewModels;
using GAssetForge.Core.Generation;
using GAssetForge.Core.Projects;

namespace GAssetForge.SmokeTests;

internal static class AssetGenerationBatchSmokeTests
{
    public static void Run()
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            "GAssetForge.SmokeTests",
            "asset-generation-batch",
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

        try
        {
            shell.SelectWorkflow(TilemapConstraintPackageBuilder.WorkflowId);
            var style = shell.ArtStyleLibrary.SelectedGroup
                ?? throw new InvalidOperationException("批量 smoke 缺少默认风格组。");
            style.Description = "低饱和手绘石材、清晰轮廓与统一光照";

            var firstOutput = Path.Combine(paths.FinalAssetDirectory, "Ground");
            var secondOutput = Path.Combine(paths.FinalAssetDirectory, "Props");
            Directory.CreateDirectory(firstOutput);
            Directory.CreateDirectory(secondOutput);

            var generation = shell.AssetGeneration;
            var first = generation.DraftRows.Single();
            first.Description = "苔藓砂岩地表";
            first.Quantity = 2;
            first.OutputDirectory = firstOutput;
            generation.AddDraftRowCommand.Execute(null);
            var second = generation.DraftRows.Last();
            second.Description = "破损石质路标";
            second.Quantity = 1;
            second.OutputDirectory = secondOutput;

            False(generation.IsAgentConnected, "离线发布测试不应依赖现有握手");
            True(generation.CanCreateTask, "无 Agent 握手时有效批次仍应可发布");

            var published = new List<GenerationBatchPublishedEventArgs>();
            generation.BatchPublished += (_, args) => published.Add(args);
            generation.CreateTaskCommand.Execute(null);
            WaitUntil(
                () => published.Count == 1 && generation.PublishedTasks.Count == 3,
                TimeSpan.FromSeconds(8),
                "离线批次未完成原子发布");

            var firstEvent = published[0];
            False(firstEvent.IsRetry, "首次发布不得标记为重试");
            False(firstEvent.DispatchRequested, "仅发布队列不得请求 Agent 调度");
            Equal(3, firstEvent.TaskIds.Count, "首次 BatchPublished 应按一图一任务发布");
            var store = new JsonGenerationTaskStore(paths.GenerationDirectory);
            var tasks = store.ListBatchAsync(firstEvent.BatchId).GetAwaiter().GetResult();
            Equal(3, tasks.Count, "批次持久化单图任务数");
            True(generation.StatusText.Contains("仅发布到本地队列", StringComparison.Ordinal),
                "离线发布应明确保持在本地队列而不请求 Agent 调度");
            True(tasks.All(task => task.Status == GenerationTaskStatus.Pending),
                "仅发布队列不得改写已持久化任务的 Pending 状态");
            True(tasks.All(task => task.DispatchIntent is { Requested: false, IsRetry: false }),
                "仅发布本地队列的任务不得在重启后被自动派发");
            False(generation.CanReconnectAndContinueCurrentBatch,
                "DispatchIntent.Requested=false 的仅发布批次不得启用重新连接并继续");
            False(generation.ReconnectAndContinueCurrentBatchCommand.CanExecute(null),
                "仅发布队列不得通过重新连接按钮意外转为自动调度");
            Equal(firstEvent.TaskIds[0], tasks[0].Id, "BatchPublished 第一任务顺序");
            Equal(firstEvent.TaskIds[1], tasks[1].Id, "BatchPublished 第二任务顺序");
            Equal(firstEvent.TaskIds[2], tasks[2].Id, "BatchPublished 第三任务顺序");
            True(tasks.All(task => task.Constraints.Output.Count == 1),
                "新批次必须一张图片一个 GenerationTask");
            True(tasks.All(task => task.Constraints.Output.MaxParallelism == 1),
                "单图任务仅保留 maxParallelism=1 的旧合同兼容值");
            True(tasks.All(task => task.RetryLineage is { AttemptNumber: 1, MaxAttempts: 4 }),
                "默认自动重做 3 次应冻结为初次加三次、共 4 次生成尝试");
            var retryingPresentation = new PublishedGenerationTaskViewModel(
                tasks[0] with
                {
                    Status = GenerationTaskStatus.Failed,
                    FailureMessage = "output_geometry_validation_failed: aspect_ratio_mismatch",
                    FailureRetryable = true
                },
                "自动重做展示");
            True(retryingPresentation.HasAutomaticRetryRemaining,
                "可恢复失败且仍有次数时应呈现为自动重做中");
            False(retryingPresentation.CanRetry,
                "自动重做尚未耗尽时不应同时开放人工重新生成按钮");
            True(retryingPresentation.StatusText.Contains("自动重做中", StringComparison.Ordinal),
                "可恢复失败不应立即显示为最终失败");
            var blockedPresentation = new PublishedGenerationTaskViewModel(
                retryingPresentation.Task with
                {
                    AutomaticRetryBlockedReason = "Run manifest 暂时不可写"
                },
                "人工接管展示");
            True(blockedPresentation.IsAutomaticRetryBlocked,
                "自动准备失败应与仍在自动重做中的状态分离");
            False(blockedPresentation.HasAutomaticRetryRemaining,
                "自动准备被阻断后不得继续显示为自动重做中");
            True(blockedPresentation.CanRetry,
                "自动准备被阻断后必须开放人工重新生成入口");
            True(blockedPresentation.StatusText.Contains("人工接管", StringComparison.Ordinal),
                "阻断状态应明确提示用户可以人工接管");

            var automaticParent = (store.CreateBatchAsync(new CreateGenerationBatchRequest
            {
                BatchId = Guid.NewGuid().ToString("N"),
                DisplayName = "自动重做协调 smoke",
                DispatchRequested = true,
                Items =
                [
                    new CreateGenerationBatchItemRequest
                    {
                        ItemId = Guid.NewGuid().ToString("N"),
                        DisplayName = "384x448 长方形错误",
                        MaxAttempts = 4,
                        Constraints = tasks[0].Constraints
                    }
                ]
            }).GetAwaiter().GetResult()).Single();
            store.ClaimAsync(automaticParent.Id, "auto-retry-smoke-agent").GetAwaiter().GetResult();
            var automaticFailed = store.FailAsync(
                    automaticParent.Id,
                    "auto-retry-smoke-agent",
                    new GenerationTaskFailure
                    {
                        Message =
                            "output_geometry_validation_failed: tilemap_geometry_aspect_ratio_mismatch; actualWidth=384; actualHeight=448",
                        Code = "output_geometry_validation_failed",
                        SubCode = "tilemap_geometry_aspect_ratio_mismatch",
                        Stage = "output.geometry_validation",
                        Retryable = true
                    })
                .GetAwaiter()
                .GetResult();
            True(MaterialGenerationConstraintComposer.TryReadDelivery(
                    automaticParent.Constraints,
                    out var automaticParentDelivery) &&
                 automaticParentDelivery?.Workspace is not null,
                "自动重做父任务缺少原始 Run。");
            using (var retryGeneration = new AssetGenerationViewModel(
                       paths,
                       store,
                       shell.ArtStyleLibrary,
                       shell.TilemapWorkspace,
                       shell.ProjectBrowser,
                       shell.AgentConnection,
                       new DesktopAssetGenerationOutputPicker(),
                       shell.BackgroundRemoval,
                       autoRetryEnabled: true,
                       autoRetryCount: 3))
            {
                GenerationBatchPublishedEventArgs? automaticPublished = null;
                retryGeneration.BatchPublished += (_, args) => automaticPublished = args;
                True(retryGeneration.GetRecoverableAutomaticRetrySourceBatchIdsAsync()
                        .GetAwaiter()
                        .GetResult()
                        .Contains(automaticFailed.BatchId!, StringComparer.Ordinal),
                    "重启恢复应识别尚未创建 child 的可恢复失败批次");
                var automaticResult = retryGeneration
                    .TryCreateAutomaticRetryBatchAsync(automaticFailed.BatchId!)
                    .GetAwaiter()
                    .GetResult();
                True(automaticResult.Scheduled, "可恢复失败应由桌面端自动创建下一次尝试");
                True(automaticPublished is { IsRetry: true, DispatchRequested: true },
                    "自动重做批次应立即请求新的 Agent Turn");
                var persistedParent = store.GetAsync(automaticFailed.Id).GetAwaiter().GetResult()
                    ?? throw new InvalidOperationException("自动重做父任务丢失。");
                True(!string.IsNullOrWhiteSpace(persistedParent.SupersededByTaskId),
                    "创建 child 时必须原子回写父任务 SupersededByTaskId");
                var automaticChild = store.GetAsync(persistedParent.SupersededByTaskId!)
                    .GetAwaiter()
                    .GetResult()
                    ?? throw new InvalidOperationException("自动重做 child 丢失。");
                Equal(2, automaticChild.RetryLineage?.AttemptNumber,
                    "首次自动重做应成为第 2/4 次尝试");
                True(automaticChild.DispatchIntent is { Requested: true, IsRetry: true },
                    "自动重做 child 必须持久化可恢复的重试调度意图");
                True(automaticChild.Constraints.Rules.Any(rule =>
                        rule.Key == "generation.retry.attempt-2" &&
                        rule.Instruction.Contains("384", StringComparison.Ordinal) &&
                        rule.Instruction.Contains("448", StringComparison.Ordinal) &&
                        rule.Instruction.Contains("外接包围盒", StringComparison.Ordinal)),
                    "自动重做提示必须注入上一轮尺寸诊断和定向比例修正");
                True(MaterialGenerationConstraintComposer.TryReadDelivery(
                        automaticChild.Constraints,
                        out var automaticDelivery) &&
                     automaticDelivery is not null &&
                     automaticDelivery.Workspace is not null &&
                     !string.Equals(
                         automaticDelivery.Workspace.RunId,
                         automaticParentDelivery!.Workspace!.RunId,
                         StringComparison.Ordinal),
                    "自动重做必须使用新 Run 和新中间路径，不得覆盖上一轮审计产物");
                var recoverableDispatches = retryGeneration
                    .GetRecoverablePendingDispatchesAsync()
                    .GetAwaiter()
                    .GetResult();
                var recoveredChildDispatch = recoverableDispatches.Single(dispatch =>
                    string.Equals(dispatch.BatchId, automaticChild.BatchId, StringComparison.Ordinal));
                Equal(automaticChild.Id, recoveredChildDispatch.TaskIds.Single(),
                    "重启恢复应重新派发同一个持久化 child，而不是创建新任务");
                True(recoveredChildDispatch.IsRetry,
                    "恢复后的自动 child 必须保留重试调度标记");

                var capturedResumeRequest = new TaskCompletionSource<GenerationBatchResumeRequest>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                var releaseResumeHandler = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                var resumeHandlerCallCount = 0;
                retryGeneration.ConfigureReconnectAndContinueHandler(async (request, cancellationToken) =>
                {
                    Interlocked.Increment(ref resumeHandlerCallCount);
                    capturedResumeRequest.TrySetResult(request);
                    await releaseResumeHandler.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                });
                True(retryGeneration.CanReconnectAndContinueCurrentBatch,
                    "当前自动重做批次存在可恢复 Pending 时应允许重新连接并继续");
                True(retryGeneration.ReconnectAndContinueCurrentBatchCommand.CanExecute(null),
                    "重新连接并继续命令应在 handler 与可恢复 Pending 均就绪时启用");
                var tasksBeforeResume = store.ListAsync().GetAwaiter().GetResult()
                    .OrderBy(task => task.Id, StringComparer.Ordinal)
                    .ToArray();
                retryGeneration.ReconnectAndContinueCurrentBatchCommand.Execute(null);
                retryGeneration.ReconnectAndContinueCurrentBatchCommand.Execute(null);
                WaitUntil(
                    () => capturedResumeRequest.Task.IsCompleted &&
                          retryGeneration.IsReconnectingAndContinuingCurrentBatch,
                    TimeSpan.FromSeconds(8),
                    "重新连接并继续命令未调用异步 handler");
                Equal(1, Volatile.Read(ref resumeHandlerCallCount),
                    "重新连接并继续命令执行期间必须防止重复点击重复派发");
                False(retryGeneration.ReconnectAndContinueCurrentBatchCommand.CanExecute(null),
                    "重新连接并继续 handler 执行期间命令必须禁用");
                Equal("正在重新连接…", retryGeneration.ReconnectAndContinueCurrentBatchButtonText,
                    "重新连接执行期间按钮文本");
                var resumeRequest = capturedResumeRequest.Task.GetAwaiter().GetResult();
                Equal(automaticChild.BatchId, resumeRequest.BatchId,
                    "重新连接请求必须使用当前批次 ID");
                Equal(automaticChild.Id, resumeRequest.TaskIds.Single(),
                    "重新连接请求必须复用持久化 Pending taskId");
                True(resumeRequest.IsRetry,
                    "重新连接请求必须保留当前批次的自动重做标记");
                releaseResumeHandler.TrySetResult();
                WaitUntil(
                    () => !retryGeneration.IsReconnectingAndContinuingCurrentBatch,
                    TimeSpan.FromSeconds(8),
                    "重新连接并继续 handler 完成后页面未解除忙碌状态");
                var tasksAfterResume = store.ListAsync().GetAwaiter().GetResult()
                    .OrderBy(task => task.Id, StringComparer.Ordinal)
                    .ToArray();
                AssertTaskStoreUnchanged(tasksBeforeResume, tasksAfterResume);
                True(retryGeneration.StatusText.Contains("不会创建新任务", StringComparison.Ordinal),
                    "重新连接完成提示应明确复用原任务且不覆盖已有产物");

                var managedResumeAgentId = $"codex:gassetforge:{automaticChild.Id}";
                store.ClaimAsync(automaticChild.Id, managedResumeAgentId)
                    .GetAwaiter()
                    .GetResult();
                var claimedTasksBeforeResume = store.ListAsync().GetAwaiter().GetResult()
                    .OrderBy(task => task.Id, StringComparer.Ordinal)
                    .ToArray();
                True(retryGeneration.ReconnectAndContinueCurrentBatchCommand.CanExecute(null),
                    "持久化状态变化尚未刷新到页面前命令可见状态允许保持旧快照");
                retryGeneration.ReconnectAndContinueCurrentBatchCommand.Execute(null);
                WaitUntil(
                    () => !retryGeneration.IsReconnectingAndContinuingCurrentBatch &&
                          Volatile.Read(ref resumeHandlerCallCount) == 2,
                    TimeSpan.FromSeconds(8),
                    "同一受管 agentId 已认领的任务未被重新连接 handler 继续派发");
                var claimedTasksAfterResume = store.ListAsync().GetAwaiter().GetResult()
                    .OrderBy(task => task.Id, StringComparer.Ordinal)
                    .ToArray();
                AssertTaskStoreUnchanged(claimedTasksBeforeResume, claimedTasksAfterResume);
                True(retryGeneration.ReconnectAndContinueCurrentBatchCommand.CanExecute(null),
                    "同一受管 agentId 的 Claimed-only 当前批次应允许再次安全续作");

                store.CompleteAsync(
                        automaticChild.Id,
                        managedResumeAgentId,
                        automaticDelivery!.TargetPaths)
                    .GetAwaiter()
                    .GetResult();
                retryGeneration.RefreshTaskCommand.Execute(null);
                WaitUntil(
                    () => retryGeneration.PublishedTasks.Any(task =>
                        string.Equals(task.TaskId, automaticChild.Id, StringComparison.Ordinal) &&
                        task.Status == GenerationTaskStatus.Completed),
                    TimeSpan.FromSeconds(8),
                    "重新连接测试未刷新出 Completed-only 当前批次");
                False(retryGeneration.CanReconnectAndContinueCurrentBatch,
                    "Completed-only 当前批次不得启用重新连接并继续");
                False(retryGeneration.ReconnectAndContinueCurrentBatchCommand.CanExecute(null),
                    "Completed-only 当前批次的重新连接命令应禁用");

                var manifestPath = Path.Combine(
                    automaticDelivery!.AssetRoot,
                    automaticDelivery.Workspace!.ManifestPath.Replace('/', Path.DirectorySeparatorChar));
                File.Delete(manifestPath);
                retryGeneration.RepairPersistedDispatchWorkspaceAsync(automaticChild.BatchId!)
                    .GetAwaiter()
                    .GetResult();
                True(File.Exists(manifestPath),
                    "持久化 child 缺失 manifest 时必须能从冻结任务合同重建");

                var legacyArtBatchId = Guid.NewGuid().ToString("N");
                var legacyArtItemId = Guid.NewGuid().ToString("N");
                const string legacyArtBaseName = "legacy_art_recovery";
                var legacyArtRunRoot =
                    $"{GenerationRunWorkspaceService.LegacyRunContainerRelativePath}/{legacyArtBatchId}";
                var runWorkspaceService = new GenerationRunWorkspaceService();
                var legacyArtRun = runWorkspaceService.OpenOrRepair(
                    legacyArtBatchId,
                    paths.AssetDirectory,
                    legacyArtRunRoot);
                var legacyArtWorkspace = runWorkspaceService.CreateOutputPlan(
                    legacyArtRun,
                    legacyArtItemId,
                    legacyArtBaseName);
                var legacyArtOutputDirectory =
                    $"Generated/ArtWorkspace/{legacyArtBatchId}/Final";
                var legacyArtTarget =
                    $"{legacyArtOutputDirectory}/{legacyArtBaseName}.png";
                var legacyArtConstraints = MaterialGenerationConstraintComposer.Compose(
                    new GenerationConstraintPackage
                    {
                        WorkflowId = ArtWorkspaceGenerationConstraintPackageBuilder.WorkflowId,
                        PreferredSkill = ArtWorkspaceGenerationConstraintPackageBuilder.PreferredSkill,
                        Prompt = "恢复没有 stagingPaths 的历史美术工作区任务。",
                        Output = new GenerationOutputSpec
                        {
                            Width = 1024,
                            Height = 1024,
                            Count = 1,
                            MaxParallelism = 1,
                            Format = "png",
                            TransparentBackground = false
                        }
                    },
                    new MaterialGenerationStyleSelection
                    {
                        GroupId = "legacy-art-recovery",
                        Name = "历史美术工作区恢复",
                        Description = "只验证冻结交付路径恢复。"
                    },
                    new GenerationDeliveryPlan
                    {
                        AssetRoot = paths.AssetDirectory,
                        OutputDirectory = legacyArtOutputDirectory,
                        TargetPaths = [legacyArtTarget],
                        StagingPaths = [],
                        SuggestedBaseName = legacyArtBaseName,
                        Workspace = legacyArtWorkspace
                    });
                var legacyArtTask = (store.CreateBatchAsync(new CreateGenerationBatchRequest
                {
                    BatchId = legacyArtBatchId,
                    DisplayName = "历史美术工作区恢复 smoke",
                    RequestedBy = "GAssetForge SmokeTests",
                    DispatchRequested = true,
                    Items =
                    [
                        new CreateGenerationBatchItemRequest
                        {
                            ItemId = legacyArtItemId,
                            DisplayName = "没有 stagingPaths 的美术工作区任务",
                            Constraints = legacyArtConstraints
                        }
                    ]
                }).GetAwaiter().GetResult()).Single();
                var legacyArtManifestPath = Path.Combine(
                    paths.AssetDirectory,
                    legacyArtWorkspace.ManifestPath.Replace('/', Path.DirectorySeparatorChar));
                var legacyArtPromptPath = Path.Combine(
                    paths.AssetDirectory,
                    legacyArtWorkspace.PromptPath.Replace('/', Path.DirectorySeparatorChar));
                False(File.Exists(legacyArtManifestPath),
                    "回归前置条件：历史美术任务的 manifest 应缺失");
                False(File.Exists(legacyArtPromptPath),
                    "回归前置条件：历史美术任务的 prompt 应缺失");
                var taskCountBeforeLegacyArtRepair = store.ListAsync().GetAwaiter().GetResult().Count;

                retryGeneration.RepairPersistedDispatchWorkspaceAsync(legacyArtBatchId)
                    .GetAwaiter()
                    .GetResult();

                True(File.Exists(legacyArtManifestPath) && new FileInfo(legacyArtManifestPath).Length > 0,
                    "stagingPaths=[] 的历史美术任务应能原地重建 manifest");
                True(File.Exists(legacyArtPromptPath) && new FileInfo(legacyArtPromptPath).Length > 0,
                    "stagingPaths=[] 的历史美术任务应能原地重建 prompt");
                var expectedNormalizedPath =
                    $"{legacyArtRunRoot}/35-normalized-input/{legacyArtItemId}/{legacyArtBaseName}.normalized.png";
                True(File.ReadAllText(legacyArtManifestPath).Contains(
                        expectedNormalizedPath,
                        StringComparison.Ordinal),
                    "没有 stagingPaths 时恢复 manifest 应使用冻结 Run 内的确定性 normalized 路径");
                var repairedLegacyArtTask = store.GetAsync(legacyArtTask.Id)
                    .GetAwaiter()
                    .GetResult()
                    ?? throw new InvalidOperationException("历史美术工作区恢复任务丢失。");
                True(MaterialGenerationConstraintComposer.TryReadDelivery(
                        repairedLegacyArtTask.Constraints,
                        out var repairedLegacyArtDelivery) &&
                     repairedLegacyArtDelivery is not null,
                    "恢复后的历史美术工作区交付合同应保持可读");
                Equal(legacyArtRunRoot, repairedLegacyArtDelivery!.Workspace?.RootPath,
                    "恢复不得把 legacy frozen root 迁移到当前 AI 分类目录");
                Equal(legacyArtTarget, repairedLegacyArtDelivery.TargetPaths.Single(),
                    "恢复不得改写历史美术工作区冻结 target");
                Equal(0, repairedLegacyArtDelivery.StagingPaths.Length,
                    "恢复不得给历史美术工作区任务补造 stagingPaths");
                Equal(taskCountBeforeLegacyArtRepair, store.ListAsync().GetAwaiter().GetResult().Count,
                    "恢复历史美术任务不得创建替代任务");
                var classifiedArtRunPath = Path.Combine(
                    paths.AssetDirectory,
                    ProjectAssetLayout.BuildAiRunRelativePath(
                            ProjectAssetLayout.ArtWorkspaceCategory,
                            legacyArtBatchId)
                        .Replace('/', Path.DirectorySeparatorChar));
                False(Directory.Exists(classifiedArtRunPath),
                    "恢复历史美术任务不得并行创建新的 AI/ArtWorkspace Run");
            }

            generation.ReportDispatchQueued(firstEvent.BatchId, "smoke 调度队列");
            Equal(35d, generation.DispatchProgress, "批次入队进度");
            Equal("等待调度", generation.CurrentDispatchStageText, "批次入队阶段文本");
            True(generation.IsDispatching, "批次入队后应保持调度中");
            var dispatchRequest = new GenerationBatchDispatchRequest(
                firstEvent.BatchId,
                firstEvent.TaskIds,
                IsRetry: false);
            generation.ReportDispatchProgress(new GenerationBatchDispatchProgressSnapshot(
                dispatchRequest,
                GenerationBatchDispatchStage.BuildingAgentInstruction,
                ProgressPercentage: 45d,
                Instruction: "GenerationAgentPromptBuilder.Build(taskIds)",
                Detail: "smoke 正在构建 Agent 指令"));
            Equal(45d, generation.DispatchProgress, "构建 Agent 指令进度");
            True(generation.CurrentInstruction.Contains("GenerationAgentPromptBuilder", StringComparison.Ordinal),
                "当前执行指令应显示真实调度调用");
            generation.ReportDispatchResult(firstEvent.BatchId, "smoke Turn 已结束，等待任务状态");
            True(generation.IsDispatching, "Turn 结束但任务未终态时不得提前结束批次进度");
            Equal("等待 MCP 认领", generation.CurrentDispatchStageText, "Turn 结束后的任务收束阶段");
            generation.ReportAgentActivity(
                DateTimeOffset.Now,
                "Agent",
                "正在等待图像模型返回" + new string('x', 2_100));
            Equal(1, generation.AgentFeedbackEntries.Count, "Agent 文本活动应实时进入当前批次反馈列表");
            True(generation.AgentFeedbackEntries[0].Message.Length <= 2_000,
                "Agent 回传文本必须限长，避免长消息拖慢停靠页");
            True(generation.AgentFeedbackStatusText.Contains("最近活动", StringComparison.Ordinal),
                "Agent 回传后应立即刷新最近活动状态");
            generation.ReportAgentActivity(
                DateTimeOffset.Now.AddMilliseconds(100),
                "Agent",
                "正在绘制第 1 张",
                coalesceKey: "smoke-turn/message-1/agent-message");
            generation.ReportAgentActivity(
                DateTimeOffset.Now.AddMilliseconds(200),
                "Agent",
                "正在绘制第 1 张，图片已经落盘",
                coalesceKey: "smoke-turn/message-1/agent-message");
            Equal(2, generation.AgentFeedbackEntries.Count,
                "同一 Agent 流式消息应原位更新，不能让每个增量刷满反馈列表");
            True(generation.AgentFeedbackEntries[0].Message.Contains("已经落盘", StringComparison.Ordinal),
                "同一 Agent 流式消息应保留最新累计文本");

            True(MaterialGenerationConstraintComposer.TryReadDelivery(tasks[0].Constraints, out var firstDelivery) &&
                 firstDelivery is not null,
                "第一行缺少 delivery");
            True(MaterialGenerationConstraintComposer.TryReadDelivery(tasks[1].Constraints, out var secondDelivery) &&
                 secondDelivery is not null,
                "第一行第二变体缺少 delivery");
            True(MaterialGenerationConstraintComposer.TryReadDelivery(tasks[2].Constraints, out var thirdDelivery) &&
                 thirdDelivery is not null,
                "第二行唯一变体缺少 delivery");
            Equal(1, firstDelivery!.TargetPaths.Length, "第一变体唯一目标数量");
            Equal(1, secondDelivery!.TargetPaths.Length, "第二变体唯一目标数量");
            Equal(1, thirdDelivery!.TargetPaths.Length, "第三变体唯一目标数量");
            var expectedRunRoot = ProjectAssetLayout.BuildAiRunRelativePath(
                ProjectAssetLayout.TilemapCategory,
                firstEvent.BatchId);
            var expectedGroundFinalDirectory = ProjectAssetLayout.BuildFinalBatchRelativePath(
                ProjectAssetLayout.TilemapCategory,
                firstEvent.BatchId,
                "Ground");
            var expectedPropsFinalDirectory = ProjectAssetLayout.BuildFinalBatchRelativePath(
                ProjectAssetLayout.TilemapCategory,
                firstEvent.BatchId,
                "Props");
            True(tasks.All(task =>
                    MaterialGenerationConstraintComposer.TryReadDelivery(task.Constraints, out var delivery) &&
                    string.Equals(delivery?.Workspace?.RootPath, expectedRunRoot, StringComparison.Ordinal)),
                "Tilemap 首次生成的全部 Run 应共享 AI/Tilemap/<batch> 根目录");
            Equal(expectedGroundFinalDirectory, firstDelivery.OutputDirectory,
                "第一行最终目录应落入 Final/Ground/Tilemap/<batch>");
            Equal(expectedGroundFinalDirectory, secondDelivery.OutputDirectory,
                "第一行第二变体应复用 Final/Ground/Tilemap/<batch>");
            Equal(expectedPropsFinalDirectory, thirdDelivery.OutputDirectory,
                "第二行最终目录应落入 Final/Props/Tilemap/<batch>");
            True(firstDelivery.TargetPaths[0].StartsWith(
                     $"{expectedGroundFinalDirectory}/",
                     StringComparison.Ordinal) &&
                 thirdDelivery.TargetPaths[0].StartsWith(
                     $"{expectedPropsFinalDirectory}/",
                     StringComparison.Ordinal),
                "最终 target 应直接位于 Final/[collection/]Tilemap/<batch> 内");
            True(firstDelivery.TargetPaths[0].Contains("_r01_v001.png", StringComparison.Ordinal),
                "第一行第一变体目标命名");
            True(secondDelivery.TargetPaths[0].Contains("_r01_v002.png", StringComparison.Ordinal),
                "第一行第二变体目标命名");
            True(thirdDelivery.TargetPaths[0].Contains("_r02_v001.png", StringComparison.Ordinal),
                "第二行目标命名");
            True(firstDelivery.Workspace is not null &&
                 firstDelivery.Workspace.PreserveIntermediates &&
                 File.Exists(Path.Combine(
                     firstDelivery.AssetRoot,
                     firstDelivery.Workspace.ManifestPath.Replace('/', Path.DirectorySeparatorChar))),
                "软件应在任务发布前创建项目内运行工作区与 manifest");
            var firstWorkspace = firstDelivery.Workspace
                ?? throw new InvalidOperationException("批量 smoke 缺少项目内运行工作区。");
            True(generation.AutoRetryEnabled, "素材生成默认应启用自动重做");
            Equal(3, generation.AutoRetryCount, "素材生成默认最多重做 3 次");

            var modelOutputFullPath = ResolveAssetPath(
                firstDelivery.AssetRoot,
                firstWorkspace.ModelOutputPaths.Single());
            var normalizedOutputFullPath = ResolveAssetPath(
                firstDelivery.AssetRoot,
                firstDelivery.StagingPaths.Single());
            var processedOutputFullPath = ResolveAssetPath(
                firstDelivery.AssetRoot,
                firstWorkspace.ProcessedPaths.Single());
            File.WriteAllBytes(modelOutputFullPath, [0x50, 0x46]);
            File.WriteAllBytes(normalizedOutputFullPath, [0x50, 0x46]);
            File.WriteAllBytes(processedOutputFullPath, [0x50, 0x46]);
            generation.RefreshTaskCommand.Execute(null);
            WaitUntil(
                () => generation.GeneratedAssets.Count == 3,
                TimeSpan.FromSeconds(8),
                "模型原图、归一化图和背景处理图落盘后未即时出现在生成产物区");
            True(
                new[] { "模型原图", "画布归一化", "背景处理" }.SequenceEqual(
                    generation.GeneratedAssets.Select(asset => asset.StageText)),
                "中间产物应按稳定工序顺序显示");
            True(generation.GeneratedAssets.All(asset =>
                    asset.VisualState == GeneratedAssetVisualState.Processing &&
                    asset.AvailabilityText.Contains("后续步骤", StringComparison.Ordinal)),
                "任务进行中的中间产物应使用处理中状态并说明后续步骤仍在执行");

            store.ClaimAsync(tasks[0].Id, "batch-smoke-agent").GetAwaiter().GetResult();
            var partialOutputRelativePath = firstDelivery.TargetPaths[0];
            var partialOutputFullPath = Path.GetFullPath(Path.Combine(
                firstDelivery.AssetRoot,
                partialOutputRelativePath.Replace('/', Path.DirectorySeparatorChar)));
            Directory.CreateDirectory(Path.GetDirectoryName(partialOutputFullPath)!);
            File.WriteAllBytes(partialOutputFullPath, [0x50, 0x46]);
            generation.RefreshTaskCommand.Execute(null);
            WaitUntil(
                () => generation.CurrentDispatchStage == GenerationBatchDispatchStage.GeneratingAssets,
                TimeSpan.FromSeconds(8),
                "任务被认领后批次进度未进入生成资产阶段");
            True(generation.DispatchProgress >= 65d, "生成资产阶段进度不得回退");
            var claimedPublishedTask = generation.PublishedTasks.Single(item => item.TaskId == tasks[0].Id);
            True(claimedPublishedTask.DetailText.Contains(tasks[0].Id[..8], StringComparison.Ordinal) &&
                 claimedPublishedTask.DetailText.Contains("batch-smoke-agent", StringComparison.Ordinal),
                "进行中任务应公开 taskId 与认领 Agent，便于追溯磁盘产物归属");
            WaitUntil(
                () => generation.GeneratedAssets.Any(asset =>
                    string.Equals(asset.FullPath, partialOutputFullPath, StringComparison.OrdinalIgnoreCase)),
                TimeSpan.FromSeconds(8),
                "任务完成回写前已经落盘的预分配产物未出现在生成产物区");
            Equal(4, generation.GeneratedAssets.Count,
                "进行中任务应同时保留三类中间产物和已经落盘的最终产物");
            var firstFinalAsset = generation.GeneratedAssets.Single(asset =>
                asset.Stage == GeneratedAssetStage.Final);
            Equal("最终文件已落盘 · 等待任务完成登记与校验", firstFinalAsset.AvailabilityText,
                "进行中产物不得在 complete_generation_task 成功前伪报已完成");
            Equal(1, generation.GeneratedImageCount, "第一张目标落盘后图片级进度应为 1");
            Equal(3, generation.RequestedImageCount, "图片级进度分母应取整批预分配目标总数");
            True(generation.ImageProgress > 33d && generation.ImageProgress < 34d,
                "第一张目标落盘后图片级进度应约为 1/3");
            True(generation.ImageProgressText.Contains("1 / 3", StringComparison.Ordinal),
                "图片级进度文本应显示 1 / 3");
            True(generation.CurrentImageText.Contains("2 / 3", StringComparison.Ordinal) &&
                 generation.CurrentImageText.Contains(
                     Path.GetFileName(secondDelivery.TargetPaths[0]),
                     StringComparison.Ordinal),
                "当前图片说明应指向稳定顺序中的下一张缺失目标");
            var firstImageDispatchProgress = generation.DispatchProgress;

            var unrelatedOutput = Path.Combine(firstDelivery.AssetRoot, "unrelated-output.png");
            File.WriteAllBytes(unrelatedOutput, [0x50, 0x46]);
            generation.RefreshTaskCommand.Execute(null);
            WaitUntil(
                () => generation.GeneratedImageCount == 1,
                TimeSpan.FromSeconds(8),
                "非 delivery.targetPaths 文件不应计入图片进度");

            var secondOutputFullPath = Path.GetFullPath(Path.Combine(
                secondDelivery.AssetRoot,
                secondDelivery.TargetPaths[0].Replace('/', Path.DirectorySeparatorChar)));
            store.ClaimAsync(tasks[1].Id, "batch-smoke-agent").GetAwaiter().GetResult();
            File.WriteAllBytes(secondOutputFullPath, [0x50, 0x46]);
            generation.RefreshTaskCommand.Execute(null);
            WaitUntil(
                () => generation.GeneratedImageCount == 2,
                TimeSpan.FromSeconds(8),
                "第二张目标落盘后图片级进度未推进到 2/3");
            True(generation.ImageProgress > 66d && generation.ImageProgress < 67d,
                "第二张目标落盘后图片级进度应约为 2/3");
            True(generation.DispatchProgress > firstImageDispatchProgress,
                "新增一张已落盘图片后调度进度必须严格前进");
            var secondImageDispatchProgress = generation.DispatchProgress;

            store.CompleteAsync(
                    tasks[0].Id,
                    "batch-smoke-agent",
                    firstDelivery.TargetPaths)
                .GetAwaiter().GetResult();
            store.CompleteAsync(
                    tasks[1].Id,
                    "batch-smoke-agent",
                    secondDelivery.TargetPaths)
                .GetAwaiter().GetResult();
            store.ClaimAsync(tasks[2].Id, "batch-smoke-agent").GetAwaiter().GetResult();
            var thirdOutputFullPath = Path.GetFullPath(Path.Combine(
                thirdDelivery.AssetRoot,
                thirdDelivery.TargetPaths[0].Replace('/', Path.DirectorySeparatorChar)));
            Directory.CreateDirectory(Path.GetDirectoryName(thirdOutputFullPath)!);
            File.WriteAllBytes(thirdOutputFullPath, [0x50, 0x46]);
            generation.RefreshTaskCommand.Execute(null);
            WaitUntil(
                () => generation.GeneratedImageCount == 3,
                TimeSpan.FromSeconds(8),
                "第三张目标落盘后图片级进度未推进到 3/3");
            Equal(100d, generation.ImageProgress, "全部预分配目标落盘后图片级进度应为 100%");
            True(generation.DispatchProgress > secondImageDispatchProgress && generation.DispatchProgress < 100d,
                "全部图片落盘但任务尚未终态时，步骤进度应继续前进但不得伪报完成");
            store.FailAsync(tasks[2].Id, "batch-smoke-agent", "模拟单行失败")
                .GetAwaiter().GetResult();
            generation.RefreshTaskCommand.Execute(null);
            WaitUntil(
                () => generation.PublishedTasks.Any(item => item.CanRetry),
                TimeSpan.FromSeconds(8),
                "部分失败后未暴露单行重试");
            var failedAsset = generation.GeneratedAssets.Single(asset =>
                string.Equals(asset.FullPath, thirdOutputFullPath, StringComparison.OrdinalIgnoreCase));
            Equal(GeneratedAssetVisualState.FinalFailure, failedAsset.VisualState,
                "自动重做尚未创建后继任务时，当前失败叶子应明确显示最终失败状态");
            True(failedAsset.AvailabilityText.Contains("可重新生成", StringComparison.Ordinal),
                "失败产物卡片应说明该项可重新生成");
            Equal(GenerationBatchDispatchStage.Failed, generation.CurrentDispatchStage,
                "存在失败任务时批次应进入失败终态");
            Equal(100d, generation.DispatchProgress, "失败批次调度步骤应完整收束到 100%");
            Equal("生成美术资产", generation.FailureStageText, "失败批次应保留真实失败步骤");
            True(generation.DispatchFailureText.Contains("模拟单行失败", StringComparison.Ordinal),
                "失败步骤应显示任务的真实失败原因");
            False(generation.IsDispatching, "批次进入失败终态后应解除调度占用");

            var failed = generation.PublishedTasks.Single(item => item.CanRetry);
            var originalFailedTaskId = failed.TaskId;
            var retryCanExecuteChanged = 0;
            generation.RetryFailedTaskCommand.CanExecuteChanged += (_, _) => retryCanExecuteChanged++;

            False(generation.IsAgentConnected, "离线重试保护测试不应具备可执行 Agent 链路");
            False(generation.RetryFailedTaskCommand.CanExecute(failed),
                "Agent 链路未 Ready 时失败项重试命令必须禁用");
            var taskCountBeforeOfflineRetry = store.ListAsync().GetAwaiter().GetResult().Count;
            var publishedCountBeforeOfflineRetry = published.Count;
            generation.RetryFailedTaskCommand.Execute(failed);
            Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.Background);
            Equal(taskCountBeforeOfflineRetry, store.ListAsync().GetAwaiter().GetResult().Count,
                "离线调用重试命令不得创建新的 Pending 任务");
            Equal(publishedCountBeforeOfflineRetry, published.Count,
                "离线调用重试命令不得发布新的批次事件");
            Equal(originalFailedTaskId, failed.TaskId, "离线重试保护不得替换原失败任务");
            False(failed.IsSuperseded, "无法调度时不得消耗原失败项的重试入口");

            var previousPort = shell.AgentConnection.Port;
            shell.AgentConnection.Port = previousPort == AgentConnectionViewModel.DefaultPort
                ? previousPort + 1
                : AgentConnectionViewModel.DefaultPort;
            Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.Background);
            True(retryCanExecuteChanged > 0,
                "Agent 状态或端点变化后失败项重试命令必须刷新 CanExecute");
            False(generation.RetryFailedTaskCommand.CanExecute(failed),
                "仅刷新 Agent 状态不得在链路未 Ready 时误启用重试");

            generation.CreateTaskCommand.Execute(null);
            WaitUntil(
                () => published.Count == 2 &&
                      generation.PublishedTasks.Count == 3 &&
                      generation.PublishedTasks.All(item => item.Status == GenerationTaskStatus.Pending),
                TimeSpan.FromSeconds(8),
                "全失败人工接力场景未发布新的 Pending 批次");
            var failedBatchEvent = published[1];
            var failedBatchTasks = store.ListBatchAsync(failedBatchEvent.BatchId).GetAwaiter().GetResult();
            foreach (var task in failedBatchTasks)
            {
                store.ClaimAsync(task.Id, "manual-fallback-smoke-agent").GetAwaiter().GetResult();
                store.FailAsync(task.Id, "manual-fallback-smoke-agent", "模拟自动 Turn 未完成")
                    .GetAwaiter().GetResult();
            }

            generation.RefreshTaskCommand.Execute(null);
            WaitUntil(
                () => generation.CanCreateManualRetryBatch &&
                      generation.PublishedTasks.Count == failedBatchTasks.Count &&
                      generation.PublishedTasks.All(item => item.Status == GenerationTaskStatus.Failed),
                TimeSpan.FromSeconds(8),
                "当前批次全 Failed 后未提供手动重试批次入口");
            False(generation.CanCopyCurrentBatchPrompt,
                "全 Failed 批次不得继续复制不可认领的旧 taskId");
            Equal(string.Empty, generation.CurrentBatchManualPrompt,
                "无 Pending 任务时批次提示必须为空");
            True(generation.ManualFallbackAvailabilityText.Contains("旧 Failed taskId 不会被复制", StringComparison.Ordinal),
                "全 Failed 批次应明确说明不会复制旧 taskId");
            True(generation.CreateManualRetryBatchCommand.CanExecute(null),
                "即使自动 Agent 链路未 Ready，也应允许创建手工接力重试批次");

            const string tokenVariableName = McpServerProcessService.DefaultBearerTokenEnvironmentVariableName;
            const string secretTokenValue = "asset-generation-manual-fallback-secret";
            var originalTokenValue = Environment.GetEnvironmentVariable(tokenVariableName);
            Environment.SetEnvironmentVariable(tokenVariableName, secretTokenValue);
            try
            {
                var connectionPrompt = generation.CodexConnectionPrompt;
                True(connectionPrompt.Contains(shell.AgentConnection.WorkingDirectory, StringComparison.Ordinal),
                    "素材生成页连接提示必须使用当前 Agent 项目工作目录");
                True(connectionPrompt.Contains(shell.AgentConnection.Endpoint, StringComparison.Ordinal),
                    "素材生成页连接提示必须包含当前 MCP Endpoint");
                False(connectionPrompt.Contains(secretTokenValue, StringComparison.Ordinal),
                    "素材生成页连接提示不得泄露 Bearer Token 明文");

                generation.CreateManualRetryBatchCommand.Execute(null);
                WaitUntil(
                    () => published.Count == 3 && generation.CanCopyCurrentBatchPrompt,
                    TimeSpan.FromSeconds(8),
                    "手动重试批次未创建新的可认领 Pending 任务");

                var manualRetryEvent = published[2];
                True(manualRetryEvent.IsRetry, "手动重试批次必须标记为重试");
                False(manualRetryEvent.DispatchRequested,
                    "手动重试批次不得自动请求 Agent Turn");
                False(string.Equals(failedBatchEvent.BatchId, manualRetryEvent.BatchId, StringComparison.Ordinal),
                    "手动重试必须创建新的 batchId");
                Equal(manualRetryEvent.BatchId, generation.CurrentBatchId,
                    "手动重试后当前批次应切换到新 Pending 批次");
                Equal(GenerationBatchDispatchStage.Idle, generation.CurrentDispatchStage,
                    "手动重试批次不应进入自动调度阶段");
                False(generation.IsDispatching, "手动重试批次不得占用自动调度器");

                var manualRetryTasks = store.ListBatchAsync(manualRetryEvent.BatchId).GetAwaiter().GetResult();
                Equal(failedBatchTasks.Count, manualRetryTasks.Count,
                    "手动重试应为每个失败项创建一个新任务");
                True(manualRetryTasks.All(task => task.Status == GenerationTaskStatus.Pending),
                    "手动重试任务必须保持 Pending 等待聊天中的 Agent 认领");
                for (var index = 0; index < manualRetryTasks.Count; index++)
                {
                    Equal(manualRetryEvent.TaskIds[index], manualRetryTasks[index].Id,
                        $"手动重试第 {index + 1} 个 taskId 稳定顺序");
                    True(MaterialGenerationConstraintComposer.TryReadDelivery(
                            manualRetryTasks[index].Constraints,
                            out var retryDelivery) &&
                         retryDelivery is not null &&
                         string.Equals(
                             retryDelivery.Workspace?.RootPath,
                             ProjectAssetLayout.BuildAiRunRelativePath(
                                 ProjectAssetLayout.TilemapCategory,
                                 manualRetryEvent.BatchId),
                             StringComparison.Ordinal) &&
                         retryDelivery.OutputDirectory.StartsWith(
                             $"{ProjectAssetLayout.FinalDirectoryName}/",
                             StringComparison.Ordinal) &&
                         retryDelivery.OutputDirectory.EndsWith(
                             $"/{ProjectAssetLayout.TilemapCategory}/{manualRetryEvent.BatchId}",
                             StringComparison.Ordinal) &&
                         retryDelivery.TargetPaths.All(path =>
                             path.StartsWith($"{retryDelivery.OutputDirectory}/", StringComparison.Ordinal) &&
                             path.Contains("_manual_r", StringComparison.Ordinal) &&
                             !path.Contains(
                                 $"/{manualRetryEvent.BatchId}/Final/",
                                 StringComparison.Ordinal)),
                        $"手动重试第 {index + 1} 项应分配新的人工接力交付路径");
                }

                var batchPrompt = generation.CurrentBatchManualPrompt;
                False(batchPrompt.Contains(secretTokenValue, StringComparison.Ordinal),
                    "手动批次提示不得泄露 Bearer Token 明文");
                foreach (var oldTask in failedBatchTasks)
                {
                    False(batchPrompt.Contains(oldTask.Id, StringComparison.Ordinal),
                        "手动批次提示不得包含旧 Failed taskId");
                }

                var previousTaskIndex = -1;
                foreach (var taskId in manualRetryEvent.TaskIds)
                {
                    var taskIndex = batchPrompt.IndexOf($"- {taskId}", StringComparison.Ordinal);
                    True(taskIndex > previousTaskIndex,
                        "手动批次提示中的 taskId 必须保持批次稳定顺序且准确出现");
                    previousTaskIndex = taskIndex;
                }

                var view = new GAssetForge.App.Views.AssetGenerationView
                {
                    DataContext = generation
                };
                view.ApplyTemplate();
                view.UpdateLayout();
                var promptPreview = view.FindName("CodexManualFallbackPromptPreviewTextBox") as System.Windows.Controls.TextBox
                    ?? throw new InvalidOperationException("素材生成页缺少 Codex 人工接力只读提示预览。");
                var copyConnectionButton = view.FindName("CopyCodexConnectionPromptButton") as System.Windows.Controls.Button
                    ?? throw new InvalidOperationException("素材生成页缺少复制 Codex 连接指令按钮。");
                var copyBatchButton = view.FindName("CopyCurrentBatchPromptButton") as System.Windows.Controls.Button
                    ?? throw new InvalidOperationException("素材生成页缺少复制当前批次任务指令按钮。");
                var createManualRetryButton = view.FindName("CreateManualRetryBatchButton") as System.Windows.Controls.Button
                    ?? throw new InvalidOperationException("素材生成页缺少创建手动重试批次按钮。");
                var autoRetryToggle = view.FindName("AutoRetryEnabledCheckBox") as System.Windows.Controls.CheckBox
                    ?? throw new InvalidOperationException("素材生成页缺少自动重做开关。");
                var autoRetryCount = view.FindName("AutoRetryCountComboBox") as System.Windows.Controls.ComboBox
                    ?? throw new InvalidOperationException("素材生成页缺少自动重做次数设置。");
                createManualRetryButton.GetBindingExpression(
                    System.Windows.Controls.Button.CommandProperty)?.UpdateTarget();
                True(promptPreview.IsReadOnly && promptPreview.AcceptsReturn,
                    "人工接力提示预览必须是可滚动的只读多行文本");
                Equal(
                    nameof(AssetGenerationViewModel.ManualFallbackPromptPreview),
                    System.Windows.Data.BindingOperations.GetBinding(
                        promptPreview,
                        System.Windows.Controls.TextBox.TextProperty)?.Path?.Path,
                    "人工接力提示预览绑定路径");
                True(copyConnectionButton.IsEnabled,
                    "项目目录和 Endpoint 有效时应允许复制 Codex 连接指令");
                True(copyBatchButton.IsEnabled,
                    "存在 Pending 手动重试任务时应允许复制当前批次指令");
                Equal(
                    nameof(AssetGenerationViewModel.CreateManualRetryBatchCommand),
                    System.Windows.Data.BindingOperations.GetBinding(
                        createManualRetryButton,
                        System.Windows.Controls.Button.CommandProperty)?.Path?.Path,
                    "创建手动重试批次按钮命令绑定路径");
                Equal(
                    nameof(AssetGenerationViewModel.AutoRetryEnabled),
                    System.Windows.Data.BindingOperations.GetBinding(
                        autoRetryToggle,
                        System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty)?.Path?.Path,
                    "自动重做开关绑定路径");
                Equal(
                    nameof(AssetGenerationViewModel.AutoRetryCount),
                    System.Windows.Data.BindingOperations.GetBinding(
                        autoRetryCount,
                        System.Windows.Controls.Primitives.Selector.SelectedItemProperty)?.Path?.Path,
                    "自动重做次数绑定路径");

                generation.AutoRetryEnabled = false;
                generation.AutoRetryCount = 5;
                var persistedSettings = new UserSettingsService(paths).Load();
                False(persistedSettings.AssetGenerationAutoRetryEnabled,
                    "自动重做开关应即时持久化");
                Equal(5, persistedSettings.AssetGenerationAutoRetryCount,
                    "自动重做次数应即时持久化");
            }
            finally
            {
                Environment.SetEnvironmentVariable(tokenVariableName, originalTokenValue);
            }
        }
        finally
        {
            shell.Shutdown();
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    private static void WaitUntil(Func<bool> condition, TimeSpan timeout, string message)
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
                DispatcherPriority.Background);
            Thread.Sleep(10);
        }

        throw new InvalidOperationException(message);
    }

    private static void AssertTaskStoreUnchanged(
        IReadOnlyList<GenerationTask> expected,
        IReadOnlyList<GenerationTask> actual)
    {
        Equal(expected.Count, actual.Count,
            "重新连接 handler 完成后任务库数量不得变化");
        True(expected.Select(task => task.Id).SequenceEqual(actual.Select(task => task.Id), StringComparer.Ordinal),
            "重新连接 handler 完成后任务 ID 集合与顺序不得变化");
        for (var index = 0; index < expected.Count; index++)
        {
            var before = expected[index];
            var after = actual[index];
            Equal(before.Revision, after.Revision,
                $"重新连接不得推进任务 {before.Id} 的 revision");
            Equal(before.RetryLineage, after.RetryLineage,
                $"重新连接不得改写任务 {before.Id} 的 RetryLineage");
            var beforeHasDelivery = MaterialGenerationConstraintComposer.TryReadDelivery(
                before.Constraints,
                out var beforeDelivery);
            var afterHasDelivery = MaterialGenerationConstraintComposer.TryReadDelivery(
                after.Constraints,
                out var afterDelivery);
            Equal(beforeHasDelivery, afterHasDelivery,
                $"重新连接不得改变任务 {before.Id} 是否具有 delivery");
            if (!beforeHasDelivery || beforeDelivery is null || afterDelivery is null)
            {
                continue;
            }

            Equal(beforeDelivery.AssetRoot, afterDelivery.AssetRoot,
                $"重新连接不得改写任务 {before.Id} 的 delivery.AssetRoot");
            Equal(beforeDelivery.OutputDirectory, afterDelivery.OutputDirectory,
                $"重新连接不得改写任务 {before.Id} 的 delivery.OutputDirectory");
            Equal(beforeDelivery.SuggestedBaseName, afterDelivery.SuggestedBaseName,
                $"重新连接不得改写任务 {before.Id} 的 delivery.SuggestedBaseName");
            True(beforeDelivery.TargetPaths.SequenceEqual(afterDelivery.TargetPaths, StringComparer.Ordinal),
                $"重新连接不得改写任务 {before.Id} 的 delivery.TargetPaths");
            True(beforeDelivery.StagingPaths.SequenceEqual(afterDelivery.StagingPaths, StringComparer.Ordinal),
                $"重新连接不得改写任务 {before.Id} 的 delivery.StagingPaths");
            Equal(beforeDelivery.Workspace?.RunId, afterDelivery.Workspace?.RunId,
                $"重新连接不得改写任务 {before.Id} 的 workspace.RunId");
            Equal(beforeDelivery.Workspace?.RootPath, afterDelivery.Workspace?.RootPath,
                $"重新连接不得改写任务 {before.Id} 的 workspace.RootPath");
            Equal(beforeDelivery.Workspace?.ManifestPath, afterDelivery.Workspace?.ManifestPath,
                $"重新连接不得改写任务 {before.Id} 的 workspace.ManifestPath");
            Equal(beforeDelivery.Workspace?.PromptPath, afterDelivery.Workspace?.PromptPath,
                $"重新连接不得改写任务 {before.Id} 的 workspace.PromptPath");
            Equal(beforeDelivery.Workspace?.DiagnosticsPath, afterDelivery.Workspace?.DiagnosticsPath,
                $"重新连接不得改写任务 {before.Id} 的 workspace.DiagnosticsPath");
            True((beforeDelivery.Workspace?.ModelOutputPaths ?? [])
                    .SequenceEqual(afterDelivery.Workspace?.ModelOutputPaths ?? [], StringComparer.Ordinal),
                $"重新连接不得改写任务 {before.Id} 的 workspace.ModelOutputPaths");
            True((beforeDelivery.Workspace?.ProcessedPaths ?? [])
                    .SequenceEqual(afterDelivery.Workspace?.ProcessedPaths ?? [], StringComparer.Ordinal),
                $"重新连接不得改写任务 {before.Id} 的 workspace.ProcessedPaths");
        }
    }

    private static string ResolveAssetPath(string assetRoot, string relativePath) =>
        Path.GetFullPath(Path.Combine(
            assetRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));

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
