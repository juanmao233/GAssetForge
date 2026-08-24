using GAssetForge.App.Services;
using GAssetForge.App.ViewModels;
using GAssetForge.Core.Blueprints;
using GAssetForge.Core.Workflows;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows.Threading;

namespace GAssetForge.SmokeTests;

internal static class BlueprintRegressionSmokeTests
{
    public static async Task RunAsync(string temporaryRoot)
    {
        await EditorStateAndProtocolRegressionsStayClosedAsync(temporaryRoot);
        RequiredPortsStayProtectedAcrossCaseAndKinds(temporaryRoot);
        StartupActivationFailureFallsBackAsOneContext(temporaryRoot);
        ShellActivationFailuresRemainTransactional(temporaryRoot);
    }

    private static void RequiredPortsStayProtectedAcrossCaseAndKinds(string temporaryRoot)
    {
        var root = Path.Combine(temporaryRoot, "blueprint-required-port-protection");
        var projectsRoot = Path.Combine(root, "Projects");
        Directory.CreateDirectory(projectsRoot);
        var project = new ProjectWorkspaceService().Create(projectsRoot, "PortRules", "tilemap");
        var paths = new WorkspacePaths(Path.Combine(root, "app-data"));
        using var console = new WorkbenchConsoleService(paths);
        var repository = new JsonBlueprintRepository(paths);

        var graph = BlueprintGraphFactory.CreateDefault("tilemap");
        var start = graph.Nodes.Single(node => node.Role == BlueprintNodeRole.Start);
        start.Ports.Add(BlueprintGraphFactory.CreatePort(
            "上下文",
            BlueprintPortDirection.Output,
            1,
            kind: BlueprintPortKind.Data,
            valueKind: BlueprintValueKind.Json));
        var logic = BlueprintGraphFactory.CreateNode(BlueprintNodeType.Logic, 420d, 220d);
        logic.Ports.Single(port => string.Equals(port.Name, "True", StringComparison.Ordinal)).Name = "true";
        logic.Ports.Single(port => string.Equals(port.Name, "False", StringComparison.Ordinal)).Name = "false";
        graph.Nodes.Add(logic);
        repository.Save(project.Paths.RootDirectory, "tilemap", graph);

        using var viewModel = new BlueprintViewModel(
            repository,
            new BlueprintRunWorkspaceService(paths),
            console);
        True(viewModel.Activate(project.Paths.RootDirectory, "tilemap"),
            "包含 Data 输出与小写 If 分支的合法蓝图应成功激活");

        var loadedStart = viewModel.Nodes.Single(node => node.IsStart);
        var flowOutput = loadedStart.OutputPorts.Single(port => port.Kind == BlueprintPortKind.Flow);
        var dataOutput = loadedStart.OutputPorts.Single(port => port.Kind == BlueprintPortKind.Data);
        True(!flowOutput.CanRemove, "Start 即使还有 Data 输出也不得删除唯一流程输出");
        True(dataOutput.CanRemove, "Start 的可选 Data 输出应允许删除");
        viewModel.RemovePort(flowOutput);
        Equal(2, loadedStart.OutputPorts.Count, "被保护的 Start 唯一流程输出不得被删除");
        viewModel.RemovePort(dataOutput);
        Equal(1, loadedStart.OutputPorts.Count, "Start 的可选 Data 输出应被正常删除");

        var loadedLogic = viewModel.Nodes.Single(node => node.IsLogic);
        var requiredBranches = loadedLogic.OutputPorts
            .Where(port => string.Equals(port.Name, "True", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(port.Name, "False", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Equal(2, requiredBranches.Length, "If 应识别大小写不敏感的 True/False 分支");
        foreach (var branch in requiredBranches)
        {
            True(!branch.CanRemove, $"If {branch.Name} 必需分支不可删除");
            viewModel.RemovePort(branch);
        }

        Equal(2, loadedLogic.OutputPorts.Count, "大小写变体的 If 必需分支不得被删除");
        True(viewModel.SaveNow(), "端点保护检查后蓝图仍应可保存");
    }

    private static async Task EditorStateAndProtocolRegressionsStayClosedAsync(string temporaryRoot)
    {
        var root = Path.Combine(temporaryRoot, "blueprint-regression-editor");
        var projectsRoot = Path.Combine(root, "Projects");
        Directory.CreateDirectory(projectsRoot);
        var projectService = new ProjectWorkspaceService();
        var projectA = projectService.Create(projectsRoot, "ProjectA", "tilemap");
        var projectB = projectService.Create(projectsRoot, "ProjectB", "tilemap");
        var paths = new WorkspacePaths(Path.Combine(root, "app-data"));
        using var console = new WorkbenchConsoleService(paths);
        var repository = new JsonBlueprintRepository(paths);
        using var viewModel = new BlueprintViewModel(
            repository,
            new BlueprintRunWorkspaceService(paths),
            console);

        True(viewModel.Activate(projectA.Paths.RootDirectory, "tilemap"),
            "项目 A 蓝图应成功激活");

        var initialNodeCount = viewModel.Nodes.Count;
        var persistenceBeforePreview = viewModel.PersistenceText;
        var descriptionType = FindType(viewModel, BlueprintNodeType.Description);
        viewModel.SelectAddNodeType(descriptionType);
        True(viewModel.IsAddTool,
            "从添加蓝图类型菜单选择类型后应自动激活添加工具");

        viewModel.BeginPlacementPreview(180.04d, 220.06d);
        True(viewModel.HasPlacementPreview && viewModel.PlacementPreview is not null,
            "首次左键应只创建蓝图放置预览");
        Equal(initialNodeCount, viewModel.Nodes.Count,
            "放置预览不得提前写入节点集合");
        Equal(persistenceBeforePreview, viewModel.PersistenceText,
            "放置预览不得将蓝图标记为待保存");
        Equal(descriptionType.Type, viewModel.PlacementPreview!.NodeType.Type,
            "放置预览应保留当前选择的蓝图类型");
        Equal(180d, viewModel.PlacementPreview.X, "放置预览初始 X 坐标");
        Equal(220.1d, viewModel.PlacementPreview.Y, "放置预览初始 Y 坐标");

        viewModel.UpdatePlacementPreview(342.24d, 418.76d);
        Equal(initialNodeCount, viewModel.Nodes.Count,
            "移动放置预览时不得新增节点");
        Equal(persistenceBeforePreview, viewModel.PersistenceText,
            "移动放置预览时不得改变持久化状态");
        Equal(342.2d, viewModel.PlacementPreview!.X, "放置预览更新后的 X 坐标");
        Equal(418.8d, viewModel.PlacementPreview.Y, "放置预览更新后的 Y 坐标");

        var placed = viewModel.CommitPlacementPreview(480.04d, 520.06d);
        True(placed is not null, "第二次左键应提交放置预览并创建节点");
        Equal(initialNodeCount + 1, viewModel.Nodes.Count,
            "提交放置预览应且只应新增一个节点");
        Equal(BlueprintNodeType.Description, placed!.Model.Type,
            "提交后的节点类型应与放置预览一致");
        Equal(480d, placed.X, "提交后的节点 X 坐标");
        Equal(520.1d, placed.Y, "提交后的节点 Y 坐标");
        True(!viewModel.HasPlacementPreview && viewModel.PlacementPreview is null,
            "提交后必须清除放置预览");
        True(viewModel.CommitPlacementPreview(600d, 640d) is null,
            "没有活动预览时重复提交应返回 null");
        Equal(initialNodeCount + 1, viewModel.Nodes.Count,
            "没有活动预览时重复提交不得再次新增节点");

        var source = viewModel.AddNode(720d, 160d);
        var targetFromInput = viewModel.AddNode(1_080d, 160d);
        var targetFromOutput = viewModel.AddNode(1_080d, 460d);
        var connectionCountBeforeInteraction = viewModel.Connections.Count;
        viewModel.SelectToolCommand.Execute("connect");

        var inputStartedPort = targetFromInput.InputPorts[0];
        var sourceOutput = source.OutputPorts[0];
        viewModel.HandlePortClicked(inputStartedPort);
        True(viewModel.HasPendingConnection && inputStartedPort.IsPendingConnection,
            "连线工具应允许从输入端点开始连线并高亮该端点");
        True(viewModel.ConnectionPreview is { Direction: BlueprintPortDirection.Input },
            "从输入端点起线时应创建输入方向的临时连线");
        viewModel.UpdatePendingConnectionPointer(900.5d, 280.5d);
        Equal(900.5d, viewModel.ConnectionPreview!.PointerX,
            "临时连线应跟随指针 X 坐标");
        Equal(280.5d, viewModel.ConnectionPreview.PointerY,
            "临时连线应跟随指针 Y 坐标");
        viewModel.HandlePortClicked(sourceOutput);
        Equal(connectionCountBeforeInteraction + 1, viewModel.Connections.Count,
            "输入端点起线并连接输出端点后应新增一条连线");
        var normalizedFromInput = viewModel.Connections.Single(connection =>
            string.Equals(connection.Model.TargetNodeId, targetFromInput.Id, StringComparison.Ordinal));
        Equal(source.Id, normalizedFromInput.Model.SourceNodeId,
            "输入端点起线也必须规范化为输出节点作为 Source");
        Equal(sourceOutput.Id, normalizedFromInput.Model.SourcePortId,
            "输入端点起线也必须规范化为输出端点作为 SourcePort");
        Equal(targetFromInput.Id, normalizedFromInput.Model.TargetNodeId,
            "输入端点起线的目标节点应保持输入节点");
        Equal(inputStartedPort.Id, normalizedFromInput.Model.TargetPortId,
            "输入端点起线的目标端点应保持输入端点");
        AssertTransientConnectionCleared(viewModel,
            "输入端点起线完成后");

        var outputStartedTarget = targetFromOutput.InputPorts[0];
        viewModel.HandlePortClicked(sourceOutput);
        True(viewModel.ConnectionPreview is { Direction: BlueprintPortDirection.Output },
            "从输出端点起线时应创建输出方向的临时连线");
        viewModel.HandlePortClicked(outputStartedTarget);
        Equal(connectionCountBeforeInteraction + 2, viewModel.Connections.Count,
            "输出端点起线并连接输入端点后应新增一条连线");
        var normalizedFromOutput = viewModel.Connections.Single(connection =>
            string.Equals(connection.Model.TargetNodeId, targetFromOutput.Id, StringComparison.Ordinal));
        Equal(source.Id, normalizedFromOutput.Model.SourceNodeId,
            "输出端点起线应保留输出节点作为 Source");
        Equal(sourceOutput.Id, normalizedFromOutput.Model.SourcePortId,
            "输出端点起线应保留输出端点作为 SourcePort");
        Equal(targetFromOutput.Id, normalizedFromOutput.Model.TargetNodeId,
            "输出端点起线应保留输入节点作为 Target");
        Equal(outputStartedTarget.Id, normalizedFromOutput.Model.TargetPortId,
            "输出端点起线应保留输入端点作为 TargetPort");
        AssertTransientConnectionCleared(viewModel,
            "输出端点起线完成后");

        var establishedConnectionCount = viewModel.Connections.Count;
        viewModel.HandlePortClicked(sourceOutput);
        True(viewModel.HasPendingConnection && sourceOutput.IsPendingConnection,
            "首次点击端点应进入待连接状态");
        viewModel.HandlePortClicked(sourceOutput);
        Equal(establishedConnectionCount, viewModel.Connections.Count,
            "连续点击同一端点应取消待连接且不得新增连线");
        AssertTransientConnectionCleared(viewModel,
            "重复点击同一端点取消后");

        viewModel.HandlePortClicked(inputStartedPort);
        viewModel.UpdatePendingConnectionPointer(760d, 360d);
        True(viewModel.CancelTransientInteraction(),
            "取消瞬态交互应报告已清除活动连线预览");
        Equal(establishedConnectionCount, viewModel.Connections.Count,
            "取消临时连线不得修改已建立的连接集合");
        AssertTransientConnectionCleared(viewModel,
            "主动取消临时连线后");

        viewModel.SetViewport(-315.5d, 228.25d, 1.75d, commit: true);
        viewModel.ResetViewportToOrigin();
        Equal(0d, viewModel.PanX, "视口复位后的 PanX");
        Equal(0d, viewModel.PanY, "视口复位后的 PanY");
        Equal(1d, viewModel.Zoom, "视口复位后的 Zoom");

        viewModel.RemoveNode(source);
        viewModel.RemoveNode(targetFromInput);
        viewModel.RemoveNode(targetFromOutput);
        viewModel.RemoveNode(placed);
        Equal(initialNodeCount, viewModel.Nodes.Count,
            "交互回归检查结束后应恢复初始节点集合，避免影响后续场景");
        Equal(connectionCountBeforeInteraction, viewModel.Connections.Count,
            "交互回归检查结束后应恢复初始连线集合");
        True(viewModel.SaveNow(), "交互回归检查清理后蓝图应可保存");

        viewModel.SelectToolCommand.Execute("connect");
        var repeatedSelectionNotifications = 0;
        PropertyChangedEventHandler selectionObserver = (_, eventArgs) =>
        {
            if (eventArgs.PropertyName == nameof(BlueprintViewModel.IsConnectTool))
            {
                repeatedSelectionNotifications++;
            }
        };
        viewModel.PropertyChanged += selectionObserver;
        viewModel.SelectToolCommand.Execute("connect");
        viewModel.PropertyChanged -= selectionObserver;
        True(viewModel.IsConnectTool && repeatedSelectionNotifications > 0,
            "重复点击当前工具后必须重新发布选中状态，避免 ToggleButton 与 VM 分裂");

        viewModel.SelectedAddNodeType = FindType(viewModel, BlueprintNodeType.Description);
        var bounded = viewModel.AddNode(-500d, -500d);
        Equal(0d, bounded.X, "新增节点 X 下界");
        Equal(0d, bounded.Y, "新增节点 Y 下界");
        viewModel.MoveNode(bounded, 50_000d, 50_000d, commit: true);
        Equal(BlueprintViewModel.CanvasWidth - bounded.Width, bounded.X, "移动节点 X 上界");
        Equal(BlueprintViewModel.CanvasHeight - BlueprintViewModel.MaximumEstimatedNodeHeight,
            bounded.Y,
            "移动节点 Y 上界");

        var start = viewModel.Nodes.Single(node => node.IsStart);
        start.AddOutputPortCommand.Execute(null);
        Equal(2, start.OutputPorts.Count, "起始节点可增加额外流程输出");
        Connect(viewModel, start.OutputPorts[0], bounded.InputPorts[0]);
        Equal(1, viewModel.Connections.Count, "起始端点删除回归应先建立关联连线");
        var connectedStartPort = start.OutputPorts[0];
        viewModel.RemovePort(connectedStartPort);
        Equal(1, start.OutputPorts.Count, "起始节点必须至少保留一个流程输出");
        Equal(0, viewModel.Connections.Count, "删除端点必须同步删除相关连线");
        True(!start.OutputPorts[0].CanRemove, "起始节点最后一个流程输出不可删除");

        viewModel.SelectedAddNodeType = FindType(viewModel, BlueprintNodeType.Logic);
        var logic = viewModel.AddNode(500d, 300d);
        var protectedLogicPorts = logic.OutputPorts
            .Where(port => port.Name is "True" or "False")
            .ToArray();
        Equal(2, protectedLogicPorts.Length, "If 节点必须公开 True/False 输出");
        foreach (var port in protectedLogicPorts)
        {
            True(!port.CanRemove, $"If {port.Name} 端点不可删除");
            viewModel.RemovePort(port);
        }
        Equal(2, logic.OutputPorts.Count, "删除请求不得移除 If True/False 端点");

        viewModel.SelectedAddNodeType = FindType(viewModel, BlueprintNodeType.Description);
        var terminal = viewModel.AddNode(900d, 300d);
        foreach (var output in terminal.OutputPorts.ToArray())
        {
            viewModel.RemovePort(output);
        }
        Equal(0, terminal.OutputPorts.Count, "普通节点允许删除全部输出并成为终点");

        var fanOut = viewModel.AddNode(900d, 700d);
        fanOut.AddOutputPortCommand.Execute(null);
        fanOut.AddOutputPortCommand.Execute(null);
        Equal(fanOut.OutputPorts.Count,
            fanOut.OutputPorts.Select(port => port.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            "同方向端点名称必须唯一");
        Equal(fanOut.OutputPorts.Count,
            fanOut.OutputPorts.Select(port => port.Order).Distinct().Count(),
            "同方向端点顺序必须唯一");

        var targetA = viewModel.AddNode(1_300d, 620d);
        var targetB = viewModel.AddNode(1_300d, 820d);
        var targetC = viewModel.AddNode(1_300d, 1_020d);
        Connect(viewModel, fanOut.OutputPorts[0], targetA.InputPorts[0]);
        Connect(viewModel, fanOut.OutputPorts[0], targetB.InputPorts[0]);
        var firstConnection = viewModel.Connections.Single(connection => connection.Model.Order == 0);
        viewModel.RemoveConnection(firstConnection);
        Connect(viewModel, fanOut.OutputPorts[0], targetC.InputPorts[0]);
        Equal(2,
            viewModel.Connections.Single(connection =>
                string.Equals(connection.Model.TargetNodeId, targetC.Id, StringComparison.Ordinal)).Model.Order,
            "删除早期连线后新增连线必须使用 max + 1，不能复用历史顺序");

        True(viewModel.SaveNow(), "有效蓝图应可保存到项目 A");
        var projectBGraphPath = repository.ResolveGraphPath(projectB.Paths.RootDirectory, "tilemap");
        Directory.CreateDirectory(projectBGraphPath);
        True(!viewModel.Activate(projectB.Paths.RootDirectory, "tilemap"),
            "目标图路径不可写时激活必须失败");
        bounded.Description = "激活失败后仍属于项目 A";
        True(viewModel.SaveNow(), "激活失败后应继续保存到旧项目上下文");
        var reloadedA = repository.LoadOrCreate(projectA.Paths.RootDirectory, "tilemap").Graph;
        Equal("激活失败后仍属于项目 A",
            reloadedA.Nodes.Single(node => node.Id == bounded.Id).Description,
            "失败切换后的编辑不得串写目标项目");
        True(Directory.Exists(projectBGraphPath), "失败目标应保留原始诊断目录且不被覆盖");

        await viewModel.ExecuteAsync("start");
        var timelinePath = Path.Combine(viewModel.LastRunDirectory, "timeline.v1.jsonl");
        var timelineText = File.ReadAllText(timelinePath);
        True(timelineText.EndsWith(Environment.NewLine, StringComparison.Ordinal),
            "JSONL 时间线最后一条记录必须以换行结束");
        var timelineLines = File.ReadAllLines(timelinePath);
        True(timelineLines.Length > 0, "JSONL 时间线至少包含一条记录");
        foreach (var line in timelineLines)
        {
            using var _ = JsonDocument.Parse(line);
        }
    }

    private static void ShellActivationFailuresRemainTransactional(string temporaryRoot)
    {
        var root = Path.Combine(temporaryRoot, "blueprint-regression-shell");
        var projectsRoot = Path.Combine(root, "Projects");
        Directory.CreateDirectory(projectsRoot);
        var projectService = new ProjectWorkspaceService();
        var originalProject = projectService.Create(projectsRoot, "Original", "tilemap");
        var paths = new WorkspacePaths(Path.Combine(root, "app-data"));
        var settings = new UserSettingsService(paths);
        settings.Save(new UserSettingsModel
        {
            HasCompletedWorkflowSelection = true,
            ActiveWorkflowKey = "tilemap",
            LastProjectPath = originalProject.Paths.RootDirectory,
            AssetRootPath = originalProject.Paths.AssetDirectory,
            AssetCurrentPath = originalProject.Paths.GeneratedAssetDirectory
        });
        var editor = new EditorWorkspaceViewModel(
            new JsonProjectRepository(paths),
            new DesktopFileDialogService(),
            new ProjectHistoryService());
        var shell = new ShellViewModel(new ThemeService(), settings, editor);

        try
        {
            using (File.Open(
                       originalProject.Paths.ManifestPath,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.None))
            {
                shell.SelectWorkflow("card");
            }

            Equal("card", shell.CurrentWorkflow.Key,
                "激活普通业务窗口不应依赖项目 manifest 可写状态");
            True(shell.Blueprint.PersistenceText.Contains("tilemap.gafgraph.json", StringComparison.OrdinalIgnoreCase),
                "普通业务窗口切换不得替换项目级蓝图文件");

            shell.Blueprint.SelectedAddNodeType = FindType(shell.Blueprint, BlueprintNodeType.Tool);
            var invalidTool = shell.Blueprint.AddNode(700d, 300d);
            invalidTool.TagsText = "force-pending-save";
            invalidTool.Model.ToolContractId = "gassetforge.invalid.contract";

            shell.SelectWorkflow("parallel");
            Equal("parallel", shell.CurrentWorkflow.Key,
                "业务窗口激活不得被当前蓝图保存状态阻断");
            True(projectService.TryLoad(
                    originalProject.Paths.RootDirectory,
                    out var unchangedManifest,
                    out _,
                    out var loadReason),
                $"失败切换后原项目 manifest 应保持可读：{loadReason}");
            Equal("tilemap", unchangedManifest.WorkflowKey,
                "普通工作流蓝图激活失败时不得改写项目 manifest");

            shell.OpenNewProjectCommand.Execute(null);
            shell.SelectedNewProjectWorkflow = WorkflowCatalog.Find("card");
            shell.NewProjectParentPath = projectsRoot;
            shell.NewProjectName = "RejectedCandidate";
            True(shell.CreateNewProjectCommand.CanExecute(null),
                $"新项目失败回归输入应合法：{shell.NewProjectValidationMessage}");
            shell.CreateNewProjectCommand.Execute(null);
            WaitUntil(
                () => !shell.IsCreatingNewProject,
                TimeSpan.FromSeconds(8),
                "蓝图激活失败的新建项目流程未及时收束");

            True(PathEquals(originalProject.Paths.RootDirectory, shell.CurrentProjectPath),
                "新项目蓝图激活失败时必须保留旧项目上下文");
            Equal("parallel", shell.CurrentWorkflow.Key,
                "新项目蓝图激活失败时必须保留原活动业务窗口");
            True(PathEquals(originalProject.Paths.RootDirectory, editor.ProjectRoot),
                "新项目蓝图激活失败时 EditorWorkspace 不得切到候选项目");
            True(PathEquals(originalProject.Paths.AssetDirectory, shell.ProjectBrowser.RootPath),
                "新项目蓝图激活失败时 Project 面板必须恢复旧 Assets 根");
            True(shell.NewProjectValidationMessage.Contains("保留原项目", StringComparison.Ordinal),
                "新项目失败状态应明确告知用户旧项目已保留");
            True(!Directory.Exists(Path.Combine(projectsRoot, "RejectedCandidate")),
                "当前蓝图已知无法保存时不得先创建候选项目目录");

            invalidTool.Model.ToolContractId = BlueprintToolCatalog.PixelAlignmentContractId;
            invalidTool.TagsText = "repaired";
            True(shell.Blueprint.SaveNow(), "测试结束前应修复并保存原项目蓝图");
        }
        finally
        {
            shell.Shutdown();
        }
    }

    private static void StartupActivationFailureFallsBackAsOneContext(string temporaryRoot)
    {
        var root = Path.Combine(temporaryRoot, "blueprint-regression-startup");
        var projectsRoot = Path.Combine(root, "Projects");
        Directory.CreateDirectory(projectsRoot);
        var projectService = new ProjectWorkspaceService();
        var rejectedProject = projectService.Create(projectsRoot, "RejectedStartup", "tilemap");
        var paths = new WorkspacePaths(Path.Combine(root, "app-data"));
        var repository = new JsonBlueprintRepository(paths);
        var invalidGraphPath = repository.ResolveGraphPath(
            rejectedProject.Paths.RootDirectory,
            "tilemap");
        Directory.CreateDirectory(invalidGraphPath);
        var settings = new UserSettingsService(paths);
        settings.Save(new UserSettingsModel
        {
            HasCompletedWorkflowSelection = true,
            ActiveWorkflowKey = "tilemap",
            LastProjectPath = rejectedProject.Paths.RootDirectory,
            AssetRootPath = rejectedProject.Paths.AssetDirectory,
            AssetCurrentPath = rejectedProject.Paths.GeneratedAssetDirectory
        });
        var editor = new EditorWorkspaceViewModel(
            new JsonProjectRepository(paths),
            new DesktopFileDialogService(),
            new ProjectHistoryService());
        var shell = new ShellViewModel(new ThemeService(), settings, editor);

        try
        {
            True(shell.CurrentProjectPath is null,
                "启动项目蓝图激活失败时不得提交 CurrentProject");
            True(!AssetRootService.IsWithinRoot(
                    rejectedProject.Paths.AssetDirectory,
                    shell.ProjectBrowser.RootPath),
                "启动项目失败后 Project 面板不得继续指向被拒项目 Assets");
            True(PathEquals(shell.ProjectBrowser.RootPath, shell.AgentConnection.WorkingDirectory),
                "启动项目失败后 Agent 与 Project 面板必须回到同一本地工作目录");
            True(shell.Blueprint.PersistenceText.Contains("BlueprintDrafts", StringComparison.OrdinalIgnoreCase),
                "启动项目失败后蓝图必须回退到本地 Draft 工作区");
        }
        finally
        {
            shell.Shutdown();
        }
    }

    private static BlueprintNodeTypeOption FindType(
        BlueprintViewModel viewModel,
        BlueprintNodeType type) =>
        viewModel.AddNodeTypes.Single(option => option.Type == type);

    private static void Connect(
        BlueprintViewModel viewModel,
        BlueprintPortViewModel output,
        BlueprintPortViewModel input)
    {
        viewModel.SelectToolCommand.Execute("connect");
        output.ClickCommand.Execute(null);
        input.ClickCommand.Execute(null);
    }

    private static void AssertTransientConnectionCleared(
        BlueprintViewModel viewModel,
        string context)
    {
        True(!viewModel.HasPendingConnection,
            $"{context}不应保留待连接状态");
        True(viewModel.ConnectionPreview is null,
            $"{context}不应保留临时连线几何");
        True(viewModel.Nodes
                .SelectMany(node => node.InputPorts.Concat(node.OutputPorts))
                .All(port => !port.IsPendingConnection),
            $"{context}不应保留任何端点高亮");
    }

    private static void WaitUntil(Func<bool> predicate, TimeSpan timeout, string message)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!predicate())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new InvalidOperationException(message);
            }

            Dispatcher.CurrentDispatcher.Invoke(
                DispatcherPriority.Background,
                new Action(static () => { }));
            Thread.Sleep(10);
        }
    }

    private static bool PathEquals(string expected, string? actual)
    {
        if (string.IsNullOrWhiteSpace(actual))
        {
            return false;
        }

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(expected)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(actual)),
            comparison);
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
