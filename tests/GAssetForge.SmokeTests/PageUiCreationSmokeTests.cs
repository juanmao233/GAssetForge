using AvalonDock;
using AvalonDock.Layout;
using GAssetForge.App;
using GAssetForge.App.Services;
using GAssetForge.App.Services.AgentRuntime;
using GAssetForge.App.ViewModels;
using GAssetForge.App.Views;
using GAssetForge.Core.Projects;
using GAssetForge.Core.Workflows;
using System.IO;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;

namespace GAssetForge.SmokeTests;

internal static class PageUiCreationSmokeTests
{
    public static void Run(string temporaryRoot)
    {
        DisplayNameAndStableIdentityStayCompatible(temporaryRoot);
        CanvasDocumentUsesConversationLeftAndCanvasRight(temporaryRoot);
        ConversationSubmissionAndPersistenceRoundTrip(temporaryRoot);
        ProductionGatewayReturnsTurnMatchedAgentText();
        ConversationResultSurvivesProjectModelReplacement(temporaryRoot);
        ProjectSwitchCancelsAndFinalizesActiveConversation(temporaryRoot);
    }

    private static void DisplayNameAndStableIdentityStayCompatible(string temporaryRoot)
    {
        Equal("页面UI制作", WorkflowCatalog.UiDesign.Label,
            "页面 UI 工作流正式显示名");
        Equal("ui", WorkflowCatalog.UiDesign.Key,
            "重命名不得改变项目 workflow key");
        Equal("ui-canvas", MainWindow.CanvasContentId,
            "重命名不得改变 AvalonDock ContentId");

        var paths = new WorkspacePaths(Path.Combine(temporaryRoot, "page-ui-display-name"));
        var settings = new UserSettingsService(paths);
        settings.Save(new UserSettingsModel
        {
            HasCompletedWorkflowSelection = true,
            ActiveWorkflowKey = "ui"
        });
        var editor = CreateEditor(paths);
        var shell = new ShellViewModel(new ThemeService(), settings, editor);
        var window = new MainWindow(shell, new AvalonDockLayoutService(paths));

        try
        {
            window.Show();
            window.UpdateLayout();
            PumpDispatcher();

            var manager = window.FindName("DockManager") as DockingManager
                ?? throw new InvalidOperationException("页面UI制作显示名测试未找到 DockManager。");
            var document = manager.Layout.Descendents()
                               .OfType<LayoutDocument>()
                               .SingleOrDefault(candidate =>
                                   string.Equals(
                                       candidate.ContentId,
                                       MainWindow.CanvasContentId,
                                       StringComparison.Ordinal))
                           ?? throw new InvalidOperationException("默认布局缺少 ui-canvas 页面文档。");
            Equal("页面UI制作", document.Title,
                "页面 UI 功能页标签应使用正式名称；当前设计文档名在画布标题栏显示");

            var menuItem = window.FindName("ShowUiWorkspaceMenuItem") as MenuItem
                ?? throw new InvalidOperationException("视图菜单缺少页面 UI 工作流入口。");
            Equal("页面UI制作", menuItem.Header?.ToString(),
                "视图菜单页面 UI 入口显示名");
        }
        finally
        {
            window.Close();
            shell.Shutdown();
        }
    }

    private static void CanvasDocumentUsesConversationLeftAndCanvasRight(string temporaryRoot)
    {
        var paths = new WorkspacePaths(Path.Combine(temporaryRoot, "page-ui-layout"));
        var editor = CreateEditor(paths);
        var view = new CanvasDocumentView
        {
            DataContext = editor
        };
        var host = new Window
        {
            Width = 1180,
            Height = 760,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None,
            Content = view
        };

        try
        {
            host.Show();
            host.UpdateLayout();
            PumpDispatcher();

            var root = view.Content as Grid
                ?? throw new InvalidOperationException("CanvasDocumentView 根元素必须是双区 Grid。");
            Equal(3, root.ColumnDefinitions.Count,
                "页面UI制作应由左对话、分隔条和右画布三列构成");
            True(root.ColumnDefinitions[0].MinWidth >= 220d,
                "左侧 Agent 对话区必须保留低分辨率下可输入的最小宽度");
            True(root.ColumnDefinitions[0].MaxWidth <= 560d,
                "左侧 Agent 对话区不得无限侵占画布空间");
            Equal(GridUnitType.Pixel, root.ColumnDefinitions[1].Width.GridUnitType,
                "中间停靠分隔条应使用稳定像素宽度");
            Equal(GridUnitType.Star, root.ColumnDefinitions[2].Width.GridUnitType,
                "右侧画布应占用剩余工作区宽度");

            var conversationPanel = RequireNamedElement<FrameworkElement>(
                view,
                "PageUiConversationPanel");
            var conversationList = RequireNamedElement<ItemsControl>(
                view,
                "PageUiConversationList");
            var instructionTextBox = RequireNamedElement<TextBox>(
                view,
                "PageUiInstructionTextBox");
            var submitButton = RequireNamedElement<Button>(
                view,
                "PageUiSubmitInstructionButton");
            var canvasPanel = RequireNamedElement<FrameworkElement>(
                view,
                "PageUiCanvasPanel");

            Equal(0, Grid.GetColumn(conversationPanel),
                "Agent 对话区应位于左列");
            Equal(2, Grid.GetColumn(canvasPanel),
                "画布区域应位于右列");
            True(conversationPanel.ActualWidth > 0d,
                "左侧 Agent 对话区应完成真实布局");
            True(canvasPanel.ActualWidth > conversationPanel.ActualWidth,
                "常规窗口宽度下右侧画布应拥有更大的工作空间");

            AssertBindingPath(
                conversationList,
                ItemsControl.ItemsSourceProperty,
                nameof(EditorWorkspaceViewModel.PageUiConversation),
                "对话列表 ItemsSource");
            AssertBindingPath(
                instructionTextBox,
                TextBox.TextProperty,
                nameof(EditorWorkspaceViewModel.PageUiInstructionDraft),
                "Agent 指令输入框 Text");
            var textBinding = BindingOperations.GetBinding(
                instructionTextBox,
                TextBox.TextProperty);
            Equal(UpdateSourceTrigger.PropertyChanged, textBinding?.UpdateSourceTrigger,
                "Agent 指令输入应即时同步到可持久化草稿");
            AssertBindingPath(
                submitButton,
                Button.CommandProperty,
                nameof(EditorWorkspaceViewModel.SubmitPageUiInstructionCommand),
                "发送按钮 Command");
        }
        finally
        {
            host.Close();
            editor.Shutdown();
        }
    }

    private static void ConversationSubmissionAndPersistenceRoundTrip(string temporaryRoot)
    {
        var paths = new WorkspacePaths(Path.Combine(temporaryRoot, "page-ui-conversation"));
        var editor = CreateEditor(paths);
        var gateway = new StubPageUiAgentConversationGateway();
        editor.ConfigurePageUiAgentConversationGateway(gateway);

        try
        {
            editor.PageUiInstructionDraft = "将当前战斗准备页面调整为更清晰的三栏布局。";
            False(editor.SubmitPageUiInstructionCommand.CanExecute(null),
                "Agent 未连接时不得提交页面 UI 对话");

            gateway.SetState(new PageUiAgentConversationState(
                IsConnected: true,
                IsBusy: false,
                AgentDisplayName: "Codex fixture",
                StatusText: "Agent 已接入可用",
                StatusDetail: "测试会话已就绪"));
            True(editor.IsPageUiAgentConnected,
                "网关状态事件应刷新页面 UI Agent 连接状态");
            True(editor.SubmitPageUiInstructionCommand.CanExecute(null),
                "Agent 已连接且存在指令时应允许提交");

            var completion = new TaskCompletionSource<PageUiAgentConversationResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            gateway.RunHandler = (prompt, reportAgentText, _) =>
            {
                gateway.CapturedPrompt = prompt;
                reportAgentText("正在分析当前页面层级…");
                gateway.TurnStarted.TrySetResult(true);
                return completion.Task;
            };

            editor.SubmitPageUiInstructionCommand.Execute(null);
            WaitUntilWithDispatcher(
                () => gateway.TurnStarted.Task.IsCompleted &&
                      editor.PageUiConversation.Count == 2 &&
                      editor.IsPageUiAgentBusy,
                TimeSpan.FromSeconds(5),
                "提交页面 UI 指令后应创建 user/agent 对话并进入忙碌状态");

            True(gateway.CapturedPrompt.Contains(
                    "将当前战斗准备页面调整为更清晰的三栏布局。",
                    StringComparison.Ordinal),
                "Agent prompt 必须包含用户原始页面描述");
            True(gateway.CapturedPrompt.Contains(editor.DocumentTitle, StringComparison.Ordinal),
                "Agent prompt 应冻结当前 UI 文档身份");
            True(gateway.CapturedPrompt.Contains(
                    $"{editor.CanvasWidth:0.#} × {editor.CanvasHeight:0.#}",
                    StringComparison.Ordinal),
                "Agent prompt 应包含当前画布尺寸上下文");
            True(editor.PageUiConversation[0].IsUser,
                "提交后第一条记录应为用户消息");
            Equal("将当前战斗准备页面调整为更清晰的三栏布局。",
                editor.PageUiConversation[0].Text,
                "持久化用户消息正文");
            True(editor.PageUiConversation[1].IsAgent,
                "提交后第二条记录应为 Agent 占位消息");
            Equal("streaming", editor.PageUiConversation[1].State,
                "实时回传应把 Agent 消息标记为 streaming");

            completion.SetResult(new PageUiAgentConversationResult(
                Succeeded: true,
                AgentText: "建议将主信息、操作区和状态提示分为三栏，并保持主按钮层级最高。",
                ErrorMessage: null));
            WaitUntilWithDispatcher(
                () => !editor.IsPageUiAgentBusy &&
                      editor.PageUiConversation.Count == 2 &&
                      string.Equals(
                          editor.PageUiConversation[1].State,
                          "completed",
                          StringComparison.Ordinal),
                TimeSpan.FromSeconds(5),
                "Agent 成功结果应完成当前对话并释放单飞状态");

            Equal(
                "建议将主信息、操作区和状态提示分为三栏，并保持主按钮层级最高。",
                editor.PageUiConversation[1].Text,
                "Agent 最终回复正文");
            Equal(string.Empty, editor.PageUiInstructionDraft,
                "成功提交后应清空已发送草稿");

            editor.PageUiInstructionDraft = "这是一条尚未发送、需要跨重启保留的草稿。";
            editor.SaveImmediately();
            True(File.Exists(paths.AutosaveProjectPath),
                "页面 UI 对话保存后应写入 UI 项目 JSON");
        }
        finally
        {
            editor.Shutdown();
        }

        var reloaded = CreateEditor(paths);
        try
        {
            Equal(2, reloaded.PageUiConversation.Count,
                "重载后应恢复 user/agent 对话历史");
            Equal("将当前战斗准备页面调整为更清晰的三栏布局。",
                reloaded.PageUiConversation[0].Text,
                "重载后的用户消息");
            Equal(
                "建议将主信息、操作区和状态提示分为三栏，并保持主按钮层级最高。",
                reloaded.PageUiConversation[1].Text,
                "重载后的 Agent 消息");
            Equal("这是一条尚未发送、需要跨重启保留的草稿。",
                reloaded.PageUiInstructionDraft,
                "未发送页面 UI Agent 草稿应完成持久化往返");
        }
        finally
        {
            reloaded.Shutdown();
        }
    }

    private static void ConversationResultSurvivesProjectModelReplacement(string temporaryRoot)
    {
        var paths = new WorkspacePaths(Path.Combine(temporaryRoot, "page-ui-conversation-replacement"));
        var editor = CreateEditor(paths);
        var gateway = new StubPageUiAgentConversationGateway();
        editor.ConfigurePageUiAgentConversationGateway(gateway);
        gateway.SetState(new PageUiAgentConversationState(
            IsConnected: true,
            IsBusy: false,
            AgentDisplayName: "Codex fixture",
            StatusText: "Agent 已接入可用",
            StatusDetail: "测试会话已就绪"));
        var completion = new TaskCompletionSource<PageUiAgentConversationResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        gateway.RunHandler = (_, _, _) =>
        {
            gateway.TurnStarted.TrySetResult(true);
            return completion.Task;
        };

        try
        {
            editor.PageUiInstructionDraft = "请检查模型替换后的消息回写。";
            editor.SubmitPageUiInstructionCommand.Execute(null);
            WaitUntilWithDispatcher(
                () => gateway.TurnStarted.Task.IsCompleted && editor.PageUiConversation.Count == 2,
                TimeSpan.FromSeconds(5),
                "页面 UI Turn 未进入可复现状态");
            var agentMessageId = editor.PageUiConversation.Single(message => message.IsAgent).Id;

            editor.CreateTextAt(120d, 140d);
            editor.UndoCommand.Execute(null);
            editor.RedoCommand.Execute(null);
            True(editor.PageUiConversation.Any(message => message.Id == agentMessageId),
                "模型替换后应保留同一 Agent 消息稳定标识");

            completion.SetResult(new PageUiAgentConversationResult(
                Succeeded: true,
                AgentText: "模型替换后仍应写回当前项目中的同一条消息。",
                ErrorMessage: null));
            WaitUntilWithDispatcher(
                () => !editor.IsPageUiAgentBusy &&
                      editor.PageUiConversation.Any(message =>
                          message.Id == agentMessageId &&
                          string.Equals(message.State, "completed", StringComparison.Ordinal)),
                TimeSpan.FromSeconds(5),
                "模型替换后 Agent 最终结果未写回当前项目消息");

            var currentMessage = editor.Project.EditorSession.AgentConversation
                .Single(message => string.Equals(message.Id, agentMessageId, StringComparison.Ordinal));
            Equal("completed", currentMessage.State,
                "模型替换后的持久化 Agent 消息状态");
            Equal("模型替换后仍应写回当前项目中的同一条消息。", currentMessage.Text,
                "模型替换后的持久化 Agent 消息正文");
        }
        finally
        {
            completion.TrySetResult(new PageUiAgentConversationResult(
                Succeeded: false,
                AgentText: string.Empty,
                ErrorMessage: "测试清理"));
            editor.Shutdown();
        }
    }

    private static void ProductionGatewayReturnsTurnMatchedAgentText()
    {
        var runtime = new StubPageUiAgentRuntimeConnection();
        using var gateway = new ProductionPageUiAgentConversationGateway(runtime);
        var reportedTexts = new List<string>();
        var turn = gateway.RunTurnAsync(
            "请分析当前页面。",
            reportedTexts.Add,
            CancellationToken.None);

        WaitUntilWithContextIdleDispatcher(
            () => turn.IsCompleted,
            TimeSpan.FromSeconds(5),
            "Production Page UI gateway 未完成 fixture Turn");
        var result = turn.GetAwaiter().GetResult();

        True(result.Succeeded,
            "Production Page UI gateway 应接受精确 thread/turn 匹配的成功结果");
        Equal("Production gateway 正文", result.AgentText,
            "Production Page UI gateway 最终正文");
        Equal("Production gateway 正文", reportedTexts.LastOrDefault(),
            "Production Page UI gateway 文本回调");
    }

    private static void ProjectSwitchCancelsAndFinalizesActiveConversation(string temporaryRoot)
    {
        var parent = Path.Combine(
            temporaryRoot,
            "page-ui-project-switch",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(parent);
        var workspaceService = new ProjectWorkspaceService();
        var firstProject = workspaceService.Create(parent, "page-ui-a", WorkflowCatalog.UiDesign.Key);
        var secondProject = workspaceService.Create(parent, "page-ui-b", WorkflowCatalog.UiDesign.Key);
        var paths = new WorkspacePaths(Path.Combine(parent, "app-data"));
        var editor = new EditorWorkspaceViewModel(
            new JsonProjectRepository(paths, firstProject.Paths.RootDirectory),
            new DesktopFileDialogService(),
            new ProjectHistoryService());
        var gateway = new StubPageUiAgentConversationGateway();
        editor.ConfigurePageUiAgentConversationGateway(gateway);
        gateway.SetState(new PageUiAgentConversationState(
            IsConnected: true,
            IsBusy: false,
            AgentDisplayName: "Codex fixture",
            StatusText: "Agent 已接入可用",
            StatusDetail: "测试会话已就绪"));
        var completion = new TaskCompletionSource<PageUiAgentConversationResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken observedCancellation = default;
        gateway.RunHandler = (_, _, cancellationToken) =>
        {
            observedCancellation = cancellationToken;
            cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
            gateway.TurnStarted.TrySetResult(true);
            return completion.Task;
        };

        try
        {
            editor.PageUiInstructionDraft = "这轮对话应在项目切换时安全取消。";
            editor.SubmitPageUiInstructionCommand.Execute(null);
            WaitUntilWithDispatcher(
                () => gateway.TurnStarted.Task.IsCompleted && editor.PageUiConversation.Count == 2,
                TimeSpan.FromSeconds(5),
                "项目切换测试未进入活跃页面 UI Turn");

            editor.SwitchProject(secondProject.Paths.RootDirectory);
            True(observedCancellation.IsCancellationRequested,
                "项目切换必须取消旧项目的页面 UI Agent Turn");
            WaitUntilWithDispatcher(
                () => !editor.IsPageUiAgentBusy,
                TimeSpan.FromSeconds(5),
                "项目切换后旧页面 UI Turn 未释放忙碌状态");

            var savedFirstProject = ProjectJson.Deserialize(
                File.ReadAllText(firstProject.Paths.UiProjectPath));
            False(savedFirstProject.EditorSession.AgentConversation.Any(message =>
                    string.Equals(message.State, "pending", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(message.State, "streaming", StringComparison.OrdinalIgnoreCase)),
                "项目切换不得把 pending/streaming Agent 消息永久保存到旧项目");
            var finalizedMessage = savedFirstProject.EditorSession.AgentConversation
                .Last(message => string.Equals(message.Role, "agent", StringComparison.OrdinalIgnoreCase));
            Equal("failed", finalizedMessage.State,
                "项目切换应把旧项目活跃 Agent 消息收束为 failed");
            True(finalizedMessage.Text.Contains("项目切换", StringComparison.Ordinal),
                "项目切换终态应明确说明取消原因");
            Equal(0, editor.PageUiConversation.Count,
                "旧项目迟到结果不得进入新项目对话");
        }
        finally
        {
            completion.TrySetResult(new PageUiAgentConversationResult(
                Succeeded: false,
                AgentText: string.Empty,
                ErrorMessage: "测试清理"));
            editor.Shutdown();
        }
    }

    private static EditorWorkspaceViewModel CreateEditor(WorkspacePaths paths) =>
        new(
            new JsonProjectRepository(paths),
            new DesktopFileDialogService(),
            new ProjectHistoryService());

    private static T RequireNamedElement<T>(FrameworkElement owner, string name)
        where T : FrameworkElement =>
        owner.FindName(name) as T
        ?? throw new InvalidOperationException($"页面UI制作缺少命名元素：{name}。");

    private static void AssertBindingPath(
        DependencyObject target,
        DependencyProperty property,
        string expectedPath,
        string label)
    {
        var binding = BindingOperations.GetBinding(target, property);
        Equal(expectedPath, binding?.Path?.Path, label);
    }

    private static void WaitUntilWithDispatcher(
        Func<bool> predicate,
        TimeSpan timeout,
        string failureMessage)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (!predicate())
        {
            if (stopwatch.Elapsed >= timeout)
            {
                throw new InvalidOperationException(failureMessage);
            }

            PumpDispatcher();
            Thread.Sleep(10);
        }

        PumpDispatcher();
    }

    private static void WaitUntilWithContextIdleDispatcher(
        Func<bool> predicate,
        TimeSpan timeout,
        string failureMessage)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (!predicate())
        {
            if (stopwatch.Elapsed >= timeout)
            {
                throw new InvalidOperationException(failureMessage);
            }

            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(
                DispatcherPriority.SystemIdle,
                new DispatcherOperationCallback(_ =>
                {
                    frame.Continue = false;
                    return null;
                }),
                null);
            Dispatcher.PushFrame(frame);
            Thread.Sleep(10);
        }

        PumpDispatcher();
    }

    private static void PumpDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new DispatcherOperationCallback(_ =>
            {
                frame.Continue = false;
                return null;
            }),
            null);
        Dispatcher.PushFrame(frame);
    }

    private static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void False(bool condition, string message) => True(!condition, message);

    private static void Equal<T>(T expected, T actual, string label)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException(
                $"{label}: expected '{expected}', actual '{actual}'.");
        }
    }

    private sealed class StubPageUiAgentConversationGateway : IPageUiAgentConversationGateway
    {
        private PageUiAgentConversationState _state = new(
            IsConnected: false,
            IsBusy: false,
            AgentDisplayName: "Codex fixture",
            StatusText: "Agent 尚未接入",
            StatusDetail: "测试连接尚未启动");

        public event EventHandler? StateChanged;

        public Func<
            string,
            Action<string>,
            CancellationToken,
            Task<PageUiAgentConversationResult>>? RunHandler { get; set; }

        public TaskCompletionSource<bool> TurnStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public string CapturedPrompt { get; set; } = string.Empty;

        public PageUiAgentConversationState GetState() => _state;

        public Task<PageUiAgentConversationResult> RunTurnAsync(
            string prompt,
            Action<string> reportAgentText,
            CancellationToken cancellationToken = default) =>
            RunHandler?.Invoke(prompt, reportAgentText, cancellationToken)
            ?? Task.FromResult(new PageUiAgentConversationResult(
                Succeeded: false,
                AgentText: string.Empty,
                ErrorMessage: "测试网关未配置。"));

        public void SetState(PageUiAgentConversationState state)
        {
            _state = state;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed class StubPageUiAgentRuntimeConnection : IPageUiAgentRuntimeConnection
    {
        public event EventHandler<AgentActivityEventArgs>? AgentActivityReceived;
        public event PropertyChangedEventHandler? PropertyChanged;

        public string AgentDisplayName => "Codex fixture";
        public bool IsConnected => true;
        public bool IsTurnRunning { get; private set; }
        public string StatusText => "Agent 已接入";
        public string TurnStatusText => IsTurnRunning ? "Turn 执行中" : "空闲";
        public string RuntimeModeText => "Codex exec";
        public string McpToolStatusText => "MCP 已就绪";

        public Task<AgentTurnResult> RunTurnAsync(
            string prompt,
            CancellationToken cancellationToken) => Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            IsTurnRunning = true;
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(AgentConnectionViewModel.IsTurnRunning)));
            AgentActivityReceived?.Invoke(
                this,
                new AgentActivityEventArgs(
                    DateTimeOffset.Now,
                    AgentActivityCategory.AgentMessage,
                    "其他 Turn 正文",
                    isWarning: false,
                    threadId: "thread-other",
                    turnId: "turn-other",
                    itemId: "message-other",
                    itemType: "agentMessage",
                    eventName: "item/completed",
                    isTerminal: true));
            AgentActivityReceived?.Invoke(
                this,
                new AgentActivityEventArgs(
                    DateTimeOffset.Now,
                    AgentActivityCategory.AgentMessage,
                    "Production gateway 正文",
                    isWarning: false,
                    threadId: "thread-page-ui",
                    turnId: "turn-page-ui",
                    itemId: "message-page-ui",
                    itemType: "agentMessage",
                    eventName: "item/completed",
                    isTerminal: true));
            IsTurnRunning = false;
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(AgentConnectionViewModel.IsTurnRunning)));
            return new AgentTurnResult(
                "thread-page-ui",
                "turn-page-ui",
                "completed",
                null);
        }, cancellationToken);
    }
}
