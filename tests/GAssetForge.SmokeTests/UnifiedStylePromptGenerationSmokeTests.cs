using System.IO;
using System.Text.Json.Nodes;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using GAssetForge.App.Services;
using GAssetForge.App.ViewModels;
using GAssetForge.App.Views;
using GAssetForge.Core.ArtStyles;
using GAssetForge.Core.Generation;

namespace GAssetForge.SmokeTests;

internal static class UnifiedStylePromptGenerationSmokeTests
{
    public static void Run()
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            "GAssetForge.SmokeTests",
            "unified-style-prompt-generation",
            Guid.NewGuid().ToString("N"));
        var paths = new WorkspacePaths(testRoot);
        ShellViewModel? shell = null;

        try
        {
            True(
                new UserSettingsService(paths).Load().AssetGenerationPreferUnifiedStylePrompt,
                "用户设置默认应开启统一提示词优先");
            shell = CreateShell(paths);
            shell.SelectWorkflow(TilemapConstraintPackageBuilder.WorkflowId);
            var group = shell.ArtStyleLibrary.SelectedGroup
                ?? throw new InvalidOperationException("统一提示词生成 smoke 缺少默认风格组。");
            group.Description = "legacy description that must not enter prompt-only tasks";
            group.StylePrompt = "legacy manual prompt that must not enter prompt-only tasks";

            var sourceDirectory = Path.Combine(testRoot, "sources");
            Directory.CreateDirectory(sourceDirectory);
            var sourcePath = Path.Combine(sourceDirectory, "style-reference.png");
            File.WriteAllBytes(
                sourcePath,
                Convert.FromBase64String(
                    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="));
            shell.ArtStyleLibrary.ImportAssets([sourcePath]);
            Equal(1, group.Assets.Count, "统一提示词 smoke 风格参考图数量");

            var fingerprint = ArtStylePromptFingerprint.Compute(group.Model);
            var unifiedPrompt =
                "cohesive hand-painted game art, restrained sandstone palette, crisp readable silhouettes";
            False(group.ApplyUnifiedPromptSample(new ArtStylePromptSampleModel
            {
                Prompt = unifiedPrompt,
                Summary = "不完整覆盖样本",
                SourceFingerprint = fingerprint,
                GeneratedAtUtc = DateTimeOffset.UtcNow,
                AnalyzerId = "smoke-agent",
                AnalyzerVersion = "1.0",
                RequestId = Guid.NewGuid().ToString("N"),
                AnalyzedAssetIds = [],
                AnalyzedAssetCount = 0
            }), "统一提示词样本不得只分析当前风格组的部分素材");
            True(group.UnifiedPromptSample is null,
                "拒绝未完整覆盖素材的样本时不得污染当前缓存");
            True(group.ApplyUnifiedPromptSample(new ArtStylePromptSampleModel
            {
                Prompt = unifiedPrompt,
                Summary = "暖色手绘砂岩游戏素材风格",
                SourceFingerprint = fingerprint,
                GeneratedAtUtc = DateTimeOffset.UtcNow,
                AnalyzerId = "smoke-agent",
                AnalyzerVersion = "1.0",
                RequestId = Guid.NewGuid().ToString("N"),
                AnalyzedAssetIds = group.Assets.Select(asset => asset.Id).ToList(),
                AnalyzedAssetCount = group.Assets.Count
            }), "统一提示词样本应成功应用到当前风格组");
            True(group.IsUnifiedPromptFresh, "刚应用的统一提示词样本必须新鲜");
            var managedReferencePath = group.Assets.Single().FullPath;
            File.Delete(managedReferencePath);
            True(group.IsUnifiedPromptFresh,
                "缓存新鲜度不得因分析完成后参考文件缺失而失效；提示词模式不应重复读取图片");

            var generation = shell.AssetGeneration;
            True(generation.PreferUnifiedStylePrompt, "素材生成默认应优先统一提示词");
            True(generation.IsUsingUnifiedStylePrompt, "新鲜样本应命中统一提示词模式");
            Contains(generation.UnifiedStylePromptStatusText, "不会携带或读取参考图",
                "统一提示词状态应明确跳过参考图");
            Contains(generation.ConstraintPreview, "本批次携带 0 张",
                "约束预览应明确统一提示词批次不携带参考图");

            var promptOnlyTask = PublishSingleTask(generation, paths);
            var promptOnlyStyle = RequireStyleNode(promptOnlyTask);
            Equal(
                MaterialGenerationConstraintComposer.UnifiedPromptOnlySourceMode,
                promptOnlyStyle["sourceMode"]?.GetValue<string>(),
                "统一提示词任务 sourceMode");
            Equal(unifiedPrompt, promptOnlyStyle["prompt"]?.GetValue<string>(),
                "统一提示词任务冻结样本正文");
            Equal(string.Empty, promptOnlyStyle["description"]?.GetValue<string>(),
                "统一提示词任务不得混入旧描述");
            Equal(fingerprint, promptOnlyStyle["sourceFingerprint"]?.GetValue<string>(),
                "统一提示词任务冻结来源指纹");
            True(promptOnlyStyle["generatedAt"] is JsonValue,
                "统一提示词任务应冻结样本生成时间");
            Equal(0, (promptOnlyStyle["references"] as JsonArray)?.Count ?? -1,
                "统一提示词任务不得携带参考图合同");
            Contains(promptOnlyTask.Constraints.Prompt, unifiedPrompt,
                "工作流主提示必须使用统一提示词样本");
            DoesNotContain(promptOnlyTask.Constraints.Prompt, group.Description,
                "工作流主提示不得混入旧描述");
            DoesNotContain(promptOnlyTask.Constraints.Prompt, group.StylePrompt,
                "工作流主提示不得混入人工提示词");
            var promptOnlyBatchId = promptOnlyTask.BatchId
                ?? throw new InvalidOperationException("统一提示词任务缺少批次 ID");
            False(
                Directory.Exists(Path.Combine(paths.GenerationReferenceSnapshotDirectory, promptOnlyBatchId)),
                "统一提示词任务不应创建风格参考图快照目录");
            AssertEmptyRunReferenceManifest(promptOnlyTask);
            File.Copy(sourcePath, managedReferencePath);

            group.Description = "changed legacy description makes the cached sample stale";
            True(!group.IsUnifiedPromptFresh, "风格定义变化后统一提示词样本必须过期");
            True(!generation.IsUsingUnifiedStylePrompt, "过期样本必须自动退出纯提示词模式");
            Contains(generation.UnifiedStylePromptStatusText, "自动回退",
                "过期样本状态应明确自动回退旧链路");
            True(generation.CanCreateTask,
                $"过期样本回退后应允许继续创建任务。当前约束：{generation.ConstraintPreview}");
            var fallbackTask = PublishSingleTask(generation, paths);
            var fallbackStyle = RequireStyleNode(fallbackTask);
            True(fallbackStyle["sourceMode"] is null,
                "过期样本回退任务不得伪装为统一提示词合同");
            Equal(1, (fallbackStyle["references"] as JsonArray)?.Count ?? -1,
                "过期样本回退任务必须继续冻结参考图");
            Contains(fallbackTask.Constraints.Prompt, group.Description,
                "过期样本回退任务必须使用当前旧式风格描述");

            AssetGenerationSettingsChangedEventArgs? changedSettings = null;
            generation.SettingsChanged += (_, args) => changedSettings = args;
            generation.PreferUnifiedStylePrompt = false;
            True(changedSettings is { PreferUnifiedStylePrompt: false },
                "统一提示词优先开关必须进入素材生成设置变更合同");
            Contains(generation.UnifiedStylePromptStatusText, "保持原有",
                "关闭统一提示词优先应明确保持原链路");
            True(generation.CanCreateTask,
                $"关闭统一提示词优先后应允许继续创建任务。当前约束：{generation.ConstraintPreview}");
            var legacyTask = PublishSingleTask(generation, paths);
            var legacyStyle = RequireStyleNode(legacyTask);
            True(legacyStyle["sourceMode"] is null,
                "关闭统一提示词优先时必须保持旧合同");
            Equal(1, (legacyStyle["references"] as JsonArray)?.Count ?? -1,
                "关闭统一提示词优先时必须冻结参考图");

            // Creating a WPF view installs a thread-affine CollectionView for DraftRows.
            // Keep this binding-only assertion after all asynchronous task publication so
            // the test fixture itself cannot alter the collection-threading behavior under test.
            AssertViewBindings(generation);
        }
        finally
        {
            shell?.Shutdown();
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    private static GenerationTask PublishSingleTask(
        AssetGenerationViewModel generation,
        WorkspacePaths paths)
    {
        var store = new JsonGenerationTaskStore(paths.GenerationDirectory);
        var existingIds = store.ListAsync().GetAwaiter().GetResult()
            .Select(task => task.Id)
            .ToHashSet(StringComparer.Ordinal);
        WaitUntil(
            () => generation.CreateTaskCommand.CanExecute(null),
            TimeSpan.FromSeconds(8),
            "素材生成发布命令未恢复可执行状态");
        generation.CreateTaskCommand.Execute(null);
        GenerationTask? created = null;
        try
        {
            WaitUntil(
                () =>
                {
                    created = store.ListAsync().GetAwaiter().GetResult()
                        .SingleOrDefault(task => !existingIds.Contains(task.Id));
                    return created is not null;
                },
                TimeSpan.FromSeconds(8),
                "素材生成页未发布统一提示词 smoke 批次");
        }
        catch (InvalidOperationException exception)
        {
            throw new InvalidOperationException(
                $"{exception.Message}；状态：{generation.StatusText}；约束：{generation.ConstraintPreview}",
                exception);
        }

        WaitUntil(
            () => !generation.IsBusy &&
                  generation.CanCreateTask &&
                  generation.CreateTaskCommand.CanExecute(null),
            TimeSpan.FromSeconds(8),
            "素材生成页发布后未恢复可创建状态");
        return created!;
    }

    private static JsonObject RequireStyleNode(GenerationTask task) =>
        task.Constraints.Parameters["style"] as JsonObject
        ?? throw new InvalidOperationException("生成任务缺少 parameters.style。");

    private static void AssertViewBindings(AssetGenerationViewModel generation)
    {
        var view = new AssetGenerationView { DataContext = generation };
        view.ApplyTemplate();
        view.UpdateLayout();
        var preference = view.FindName("PreferUnifiedStylePromptCheckBox") as CheckBox
            ?? throw new InvalidOperationException("素材生成页缺少统一提示词优先开关。");
        var status = view.FindName("UnifiedStylePromptStatusText") as TextBlock
            ?? throw new InvalidOperationException("素材生成页缺少统一提示词状态文本。");
        Equal(
            nameof(AssetGenerationViewModel.PreferUnifiedStylePrompt),
            BindingOperations.GetBinding(preference, CheckBox.IsCheckedProperty)?.Path?.Path,
            "统一提示词优先开关绑定路径");
        Equal(
            nameof(AssetGenerationViewModel.UnifiedStylePromptStatusText),
            BindingOperations.GetBinding(status, TextBlock.TextProperty)?.Path?.Path,
            "统一提示词状态文本绑定路径");
    }

    private static void AssertEmptyRunReferenceManifest(GenerationTask task)
    {
        True(MaterialGenerationConstraintComposer.TryReadDelivery(task.Constraints, out var delivery) &&
             delivery?.Workspace is not null,
            "统一提示词任务缺少运行工作区交付合同");
        var referenceManifest = Path.Combine(
            delivery!.AssetRoot,
            delivery.Workspace!.RootPath.Replace('/', Path.DirectorySeparatorChar),
            "10-style-reference",
            "references.v1.json");
        True(File.Exists(referenceManifest), "统一提示词任务缺少空参考图清单");
        Equal("[]", File.ReadAllText(referenceManifest).Trim(),
            "统一提示词任务的项目运行工作区参考清单必须为空");
    }

    private static ShellViewModel CreateShell(WorkspacePaths paths)
    {
        var editor = new EditorWorkspaceViewModel(
            new JsonProjectRepository(paths),
            new DesktopFileDialogService(),
            new ProjectHistoryService());
        return new ShellViewModel(
            new ThemeService(),
            new UserSettingsService(paths),
            editor);
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

            Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.Background);
            Thread.Sleep(10);
        }

        throw new InvalidOperationException(message);
    }

    private static void Contains(string actual, string expected, string message)
    {
        if (!actual.Contains(expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{message}：缺少“{expected}”。实际：{actual}");
        }
    }

    private static void DoesNotContain(string actual, string expected, string message)
    {
        if (actual.Contains(expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{message}：不应包含“{expected}”。实际：{actual}");
        }
    }

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message}：预期 {expected}，实际 {actual}");
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
}
