using GAssetForge.App.Services;
using GAssetForge.App.ViewModels;
using GAssetForge.Core.Blueprints;
using GAssetForge.Core.Workflows;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GAssetForge.SmokeTests;

internal static class BlueprintWorkflowSmokeTests
{
    public static async Task RunAsync(string temporaryRoot)
    {
        ValidatorRejectsNullRequiredStructuresAsInvalidData();
        DeserializerRequiresExplicitSchemaAndNamedEnums();
        DefaultGraphsAndProtocolAreVersionedAndDeterministic();
        ExecutionPlansFreezeTypedRoutesAndRejectTampering();
        FunctionCollectionMaximumMemberListCompiles();
        ConnectionsRejectInvalidDirectionsDuplicatesAndCycles();
        RepositoryEnforcesWriteBudgetAndFormalProjectRoots(temporaryRoot);
        await EditorPersistsPerProjectAndPublishesFrozenRunAsync(temporaryRoot);
    }

    private static void ValidatorRejectsNullRequiredStructuresAsInvalidData()
    {
        Throws<InvalidDataException>(() => BlueprintGraphValidator.Validate(null!),
            "null graph 必须以 InvalidDataException 拒绝");

        var missingViewport = BlueprintGraphFactory.CreateDefault("tilemap");
        missingViewport.Viewport = null!;
        Throws<InvalidDataException>(() => BlueprintGraphValidator.Validate(missingViewport),
            "null viewport 必须以 InvalidDataException 拒绝");

        var missingNodes = BlueprintGraphFactory.CreateDefault("tilemap");
        missingNodes.Nodes = null!;
        Throws<InvalidDataException>(() => BlueprintGraphValidator.Validate(missingNodes),
            "null nodes 必须以 InvalidDataException 拒绝");

        var missingConnections = BlueprintGraphFactory.CreateDefault("tilemap");
        missingConnections.Connections = null!;
        Throws<InvalidDataException>(() => BlueprintGraphValidator.Validate(missingConnections),
            "null connections 必须以 InvalidDataException 拒绝");

        var missingPorts = BlueprintGraphFactory.CreateDefault("tilemap");
        missingPorts.Nodes.Single(node => node.Role == BlueprintNodeRole.Start).Ports = null!;
        Throws<InvalidDataException>(() => BlueprintGraphValidator.Validate(missingPorts),
            "null ports 必须以 InvalidDataException 拒绝");

        var nullStartPort = BlueprintGraphFactory.CreateDefault("tilemap");
        nullStartPort.Nodes.Single(node => node.Role == BlueprintNodeRole.Start).Ports = [null!];
        Throws<InvalidDataException>(() => BlueprintGraphValidator.Validate(nullStartPort),
            "Start ports:[null] 必须以 InvalidDataException 拒绝，不能泄漏 NullReferenceException");
    }

    private static void DeserializerRequiresExplicitSchemaAndNamedEnums()
    {
        var graph = BlueprintGraphFactory.CreateDefault("tilemap");
        var json = BlueprintJson.SerializeGraph(graph);

        var missingSchema = JsonNode.Parse(json)!.AsObject();
        missingSchema.Remove("schemaVersion");
        Throws<InvalidDataException>(() => BlueprintJson.DeserializeGraph(missingSchema.ToJsonString()),
            "缺失 schemaVersion 的蓝图 JSON 必须 fail closed");

        var stringSchema = JsonNode.Parse(json)!.AsObject();
        stringSchema["schemaVersion"] = "1";
        Throws<InvalidDataException>(() => BlueprintJson.DeserializeGraph(stringSchema.ToJsonString()),
            "字符串 schemaVersion 不能冒充整数协议版本");

        var numericEnum = JsonNode.Parse(json)!.AsObject();
        numericEnum["nodes"]!.AsArray()[0]!.AsObject()["role"] = 1;
        Throws<InvalidDataException>(() => BlueprintJson.DeserializeGraph(numericEnum.ToJsonString()),
            "蓝图枚举必须使用协议名称，不能接受整数枚举");

        var unknownRole = BlueprintJson.Clone(graph);
        unknownRole.Nodes[0].Role = (BlueprintNodeRole)99;
        Throws<InvalidDataException>(() => BlueprintGraphValidator.Validate(unknownRole),
            "未知节点角色枚举必须被拒绝");

        var unknownType = BlueprintGraphFactory.CreateDefault("tilemap");
        var typedNode = BlueprintGraphFactory.CreateNode(BlueprintNodeType.Description, 400d, 100d);
        typedNode.Type = (BlueprintNodeType)99;
        unknownType.Nodes.Add(typedNode);
        Throws<InvalidDataException>(() => BlueprintGraphValidator.Validate(unknownType),
            "未知节点类型枚举必须被拒绝");

        var unknownPortKind = BlueprintJson.Clone(graph);
        unknownPortKind.Nodes[0].Ports[0].Kind = (BlueprintPortKind)99;
        Throws<InvalidDataException>(() => BlueprintGraphValidator.Validate(unknownPortKind),
            "未知端点类型枚举必须被拒绝");

        var invalidWorkflowKey = BlueprintJson.Clone(graph);
        invalidWorkflowKey.WorkflowKey = "tile map";
        Throws<InvalidDataException>(() => BlueprintGraphValidator.Validate(invalidWorkflowKey),
            "Graph 与 Plan 的 workflowKey 标识符约束必须保持一致");
    }

    private static void DefaultGraphsAndProtocolAreVersionedAndDeterministic()
    {
        foreach (var workflow in WorkflowCatalog.All)
        {
            var defaultGraph = BlueprintGraphFactory.CreateDefault(workflow.Key);
            BlueprintGraphValidator.Validate(defaultGraph);
            Equal(BlueprintGraph.CurrentSchemaVersion, defaultGraph.SchemaVersion,
                $"{workflow.Key} 默认图 Schema");
            Equal(1, defaultGraph.Nodes.Count(node => node.Role == BlueprintNodeRole.Start),
                $"{workflow.Key} 默认起始节点数量");
            True(defaultGraph.Nodes.Single().Ports.All(port =>
                    port.Direction == BlueprintPortDirection.Output),
                $"{workflow.Key} 起始节点只能开放输出端点");
        }

        var graph = BlueprintGraphFactory.CreateDefault("tilemap");
        var description = BlueprintGraphFactory.CreateNode(BlueprintNodeType.Description, 450d, 100d);
        description.Title = "生成说明";
        description.Tags = ["tile", "agent"];
        description.Description = "生成一块无旋转的正方形地砖。";
        var tool = BlueprintGraphFactory.CreateNode(BlueprintNodeType.Tool, 800d, 100d);
        tool.Title = "像素对齐";
        var logic = BlueprintGraphFactory.CreateNode(BlueprintNodeType.Logic, 1150d, 100d);
        logic.ConditionText = "如果输出尺寸已经是 32 x 32";
        var function = BlueprintGraphFactory.CreateNode(BlueprintNodeType.FunctionCollection, 1500d, 100d);
        function.FunctionMemberNodeIds = [description.Id, tool.Id];
        graph.Nodes.AddRange([description, tool, logic, function]);
        graph.Connections.Add(CreateConnection(graph.Nodes[0], description));
        graph.Connections.Add(CreateConnection(description, tool));
        graph.Connections.Add(CreateConnection(tool, logic));
        graph.Connections.Add(CreateConnection(logic, function, outputName: "True"));
        BlueprintGraphValidator.Validate(graph);

        var roundTrip = BlueprintJson.DeserializeGraph(BlueprintJson.SerializeGraph(graph));
        BlueprintGraphValidator.Validate(roundTrip);
        Equal(5, roundTrip.Nodes.Count, "四种用户节点与起始节点 JSON 往返");
        Equal("生成一块无旋转的正方形地砖。",
            roundTrip.Nodes.Single(node => node.Id == description.Id).Description,
            "说明文字 JSON 往返");
        Equal(BlueprintToolCatalog.PixelAlignmentContractId,
            roundTrip.Nodes.Single(node => node.Id == tool.Id).ToolContractId,
            "工具合同 JSON 往返");
        Equal(2,
            roundTrip.Nodes.Single(node => node.Id == function.Id).FunctionMemberNodeIds.Count,
            "功能集合成员 JSON 往返");
        Equal(logic.ConditionText,
            roundTrip.Nodes.Single(node => node.Id == logic.Id).ConditionText,
            "If 条件 JSON 往返");

        var firstPlan = BlueprintExecutionPlanner.Compile(roundTrip, "start");
        var secondPlan = BlueprintExecutionPlanner.Compile(roundTrip, "start");
        Equal(BlueprintJson.SerializePlan(firstPlan), BlueprintJson.SerializePlan(secondPlan),
            "同一蓝图应稳定编译为完全一致的计划 JSON");
        Equal(BlueprintExecutionPlan.CurrentSchemaVersion, firstPlan.SchemaVersion,
            "执行计划 Schema");
        Equal(5, firstPlan.Steps.Count, "从起始节点可达的执行步骤数量");
        Equal(BlueprintExecutorKind.Harness,
            firstPlan.Steps.Single(step => step.NodeId == tool.Id).Executor,
            "工具节点由 Harness 执行");
        Equal(BlueprintToolCatalog.PixelAlignmentContractId,
            firstPlan.Steps.Single(step => step.NodeId == tool.Id).ContractId,
            "工具步骤保留稳定合同 ID");
        True(firstPlan.Steps.Single(step => step.NodeId == logic.Id).Routes.Any(route =>
                string.Equals(route.OutputName, "True", StringComparison.Ordinal)),
            "If 执行步骤保留 True 分支名称");

        var unsupported = BlueprintJson.Clone(graph);
        unsupported.SchemaVersion = 99;
        Throws<InvalidDataException>(() => BlueprintGraphValidator.Validate(unsupported),
            "未知蓝图 Schema 必须 fail closed");
    }

    private static void ExecutionPlansFreezeTypedRoutesAndRejectTampering()
    {
        var graph = BlueprintGraphFactory.CreateDefault("tilemap");
        var producer = BlueprintGraphFactory.CreateNode(BlueprintNodeType.Description, 420d, 100d);
        producer.Title = "生成图像";
        var consumer = BlueprintGraphFactory.CreateNode(BlueprintNodeType.Tool, 780d, 100d);
        consumer.Title = "像素对齐";
        var secondConsumer = BlueprintGraphFactory.CreateNode(BlueprintNodeType.Tool, 1140d, 100d);
        secondConsumer.Title = "背景剔除";
        BlueprintGraphFactory.ApplyToolDefinition(
            secondConsumer,
            BlueprintToolCatalog.All.Single(definition =>
                definition.ContractId == BlueprintToolCatalog.BackgroundRemovalContractId));
        var dataOutput = BlueprintGraphFactory.CreatePort(
            "图像",
            BlueprintPortDirection.Output,
            1,
            "producer-image-out",
            BlueprintPortKind.Data,
            BlueprintValueKind.Any);
        var dataInput = BlueprintGraphFactory.CreatePort(
            "源图像",
            BlueprintPortDirection.Input,
            1,
            "consumer-image-in",
            BlueprintPortKind.Data,
            BlueprintValueKind.ImageFile);
        var secondDataInput = BlueprintGraphFactory.CreatePort(
            "源文件",
            BlueprintPortDirection.Input,
            1,
            "second-consumer-file-in",
            BlueprintPortKind.Data,
            BlueprintValueKind.File);
        producer.Ports.Add(dataOutput);
        consumer.Ports.Add(dataInput);
        secondConsumer.Ports.Add(secondDataInput);
        graph.Nodes.AddRange([producer, consumer, secondConsumer]);
        graph.Connections.Add(CreateConnection(graph.Nodes[0], producer));
        graph.Connections.Add(CreateConnection(producer, consumer));
        graph.Connections.Add(new BlueprintConnection
        {
            Id = "producer-image-route",
            SourceNodeId = producer.Id,
            SourcePortId = dataOutput.Id,
            TargetNodeId = consumer.Id,
            TargetPortId = dataInput.Id,
            Order = 1
        });
        var secondFlowConnection = CreateConnection(producer, secondConsumer);
        secondFlowConnection.Order = 2;
        graph.Connections.Add(secondFlowConnection);
        graph.Connections.Add(new BlueprintConnection
        {
            Id = "producer-file-route",
            SourceNodeId = producer.Id,
            SourcePortId = dataOutput.Id,
            TargetNodeId = secondConsumer.Id,
            TargetPortId = secondDataInput.Id,
            Order = 3
        });

        var plan = BlueprintExecutionPlanner.Compile(graph, "start");
        BlueprintExecutionPlanValidator.Validate(plan);
        Equal(BlueprintExecutionPlan.ProtocolName, plan.Protocol,
            "执行计划必须使用稳定协议名");
        var producerStep = plan.Steps.Single(step => step.NodeId == producer.Id);
        var flowRoutes = producerStep.Routes.Where(route => route.Kind == BlueprintPortKind.Flow).ToList();
        var dataRoutes = producerStep.Routes.Where(route => route.Kind == BlueprintPortKind.Data).ToList();
        True(flowRoutes.All(route => route.ValueKind == BlueprintValueKind.Any),
            "流程路由必须冻结为 Any 值类型");
        True(dataRoutes.All(route => route.SourceValueKind == BlueprintValueKind.Any),
            "Any 数据输出必须显式冻结源声明类型");
        True(dataRoutes.Select(route => route.ValueKind).ToHashSet().SetEquals(
                [BlueprintValueKind.ImageFile, BlueprintValueKind.File]),
            "同一 Any 输出允许按每个目标输入冻结不同的有效值类型");
        var dataRoute = dataRoutes.Single(route => route.TargetNodeId == consumer.Id);
        Equal(BlueprintValueKind.ImageFile, dataRoute.ValueKind,
            "Any 数据输出应采用目标输入的具体值类型");
        Equal("源图像", dataRoute.TargetInputName,
            "执行计划必须冻结目标输入名称供 Harness 绑定");

        Throws<InvalidDataException>(() => BlueprintExecutionPlanValidator.Validate(null!),
            "null 执行计划必须被拒绝");

        var invalidExecutor = ClonePlan(plan);
        invalidExecutor.Steps.Single(step => step.NodeId == consumer.Id).Executor = BlueprintExecutorKind.Agent;
        Throws<InvalidDataException>(() => BlueprintExecutionPlanValidator.Validate(invalidExecutor),
            "工具步骤不能绕过 Harness 执行器映射");

        var missingDependency = ClonePlan(plan);
        missingDependency.Steps.Single(step => step.NodeId == consumer.Id).DependsOn.Clear();
        Throws<InvalidDataException>(() => BlueprintExecutionPlanValidator.Validate(missingDependency),
            "路由与依赖不一致的执行计划必须被拒绝");

        var invalidRouteEnum = ClonePlan(plan);
        invalidRouteEnum.Steps.Single(step => step.NodeId == producer.Id)
            .Routes.Single(route =>
                route.Kind == BlueprintPortKind.Data &&
                route.TargetNodeId == consumer.Id)
            .Kind = (BlueprintPortKind)99;
        Throws<InvalidDataException>(() => BlueprintExecutionPlanValidator.Validate(invalidRouteEnum),
            "未知路由枚举必须被拒绝");

        var invalidSequence = ClonePlan(plan);
        invalidSequence.Steps[1].Sequence = 99;
        Throws<InvalidDataException>(() => BlueprintExecutionPlanValidator.Validate(invalidSequence),
            "执行步骤 sequence 必须连续且匹配列表顺序");

        var contradictoryFanOut = ClonePlan(plan);
        var contradictoryRoutes = contradictoryFanOut.Steps
            .Single(step => step.NodeId == producer.Id)
            .Routes
            .Where(route => route.OutputPortId == dataOutput.Id)
            .ToList();
        contradictoryRoutes[1].OutputName = "伪造的同端点名称";
        Throws<InvalidDataException>(() => BlueprintExecutionPlanValidator.Validate(contradictoryFanOut),
            "同一输出端点的 fan-out 不能伪造矛盾名称或端点种类");

        var maximumNodeIdGraph = BlueprintGraphFactory.CreateDefault("tilemap");
        var maximumNodeId = BlueprintGraphFactory.CreateNode(
            BlueprintNodeType.Description,
            420d,
            100d);
        maximumNodeId.Id = new string('n', BlueprintGraphValidator.MaximumIdentifierLength);
        maximumNodeIdGraph.Nodes.Add(maximumNodeId);
        maximumNodeIdGraph.Connections.Add(CreateConnection(maximumNodeIdGraph.Nodes[0], maximumNodeId));
        var maximumNodeIdPlan = BlueprintExecutionPlanner.Compile(maximumNodeIdGraph, "start");
        Equal(
            BlueprintExecutionPlanValidator.MaximumStepIdLength,
            maximumNodeIdPlan.Steps.Single(step => step.NodeId == maximumNodeId.Id).StepId.Length,
            "合法最大长度 NodeId 派生的 StepId 必须仍能通过执行计划校验");
    }

    private static void FunctionCollectionMaximumMemberListCompiles()
    {
        var graph = BlueprintGraphFactory.CreateDefault("tilemap");
        var memberIds = new List<string>(BlueprintGraphValidator.MaximumFunctionMemberNodeIds);
        for (var index = 0; index < BlueprintGraphValidator.MaximumFunctionMemberNodeIds; index++)
        {
            var member = BlueprintGraphFactory.CreateNode(
                BlueprintNodeType.Description,
                300d + index,
                100d);
            graph.Nodes.Add(member);
            memberIds.Add(member.Id);
        }

        var function = BlueprintGraphFactory.CreateNode(
            BlueprintNodeType.FunctionCollection,
            600d,
            300d);
        function.FunctionMemberNodeIds = memberIds;
        graph.Nodes.Add(function);
        graph.Connections.Add(CreateConnection(graph.Nodes[0], function));

        var plan = BlueprintExecutionPlanner.Compile(graph, "start");
        var memberParameter = plan.Steps.Single(step => step.NodeId == function.Id)
            .Parameters["memberNodeIds"];
        True(memberParameter.Length > 32_000,
            "边界回归必须覆盖旧版 32,000 字符参数上限");
        True(memberParameter.Length <= BlueprintExecutionPlanValidator.MaximumParameterValueLength,
            "功能集合最大成员列表必须处于协议参数预算内");
        BlueprintExecutionPlanValidator.Validate(plan);
    }

    private static void ConnectionsRejectInvalidDirectionsDuplicatesAndCycles()
    {
        var graph = BlueprintGraphFactory.CreateDefault("ui");
        var first = BlueprintGraphFactory.CreateNode(BlueprintNodeType.Description, 400d, 100d);
        var second = BlueprintGraphFactory.CreateNode(BlueprintNodeType.Tool, 760d, 100d);
        graph.Nodes.AddRange([first, second]);
        var connection = CreateConnection(graph.Nodes[0], first);
        graph.Connections.Add(connection);
        graph.Connections.Add(CreateConnection(first, second));
        BlueprintGraphValidator.Validate(graph);

        var duplicate = BlueprintJson.Clone(graph);
        var duplicateConnection = duplicate.Connections[0];
        duplicate.Connections.Add(new BlueprintConnection
        {
            Id = Guid.NewGuid().ToString("N"),
            SourceNodeId = duplicateConnection.SourceNodeId,
            SourcePortId = duplicateConnection.SourcePortId,
            TargetNodeId = duplicateConnection.TargetNodeId,
            TargetPortId = duplicateConnection.TargetPortId
        });
        Throws<InvalidDataException>(() => BlueprintGraphValidator.Validate(duplicate),
            "重复连线必须被拒绝");

        var reversed = BlueprintJson.Clone(graph);
        var reversedConnection = reversed.Connections[0];
        (reversedConnection.SourceNodeId, reversedConnection.TargetNodeId) =
            (reversedConnection.TargetNodeId, reversedConnection.SourceNodeId);
        (reversedConnection.SourcePortId, reversedConnection.TargetPortId) =
            (reversedConnection.TargetPortId, reversedConnection.SourcePortId);
        Throws<InvalidDataException>(() => BlueprintGraphValidator.Validate(reversed),
            "输入到输出的反向连线必须被拒绝");

        var self = BlueprintJson.Clone(graph);
        var selfNode = self.Nodes.Single(node => node.Id == first.Id);
        self.Connections.Add(new BlueprintConnection
        {
            Id = Guid.NewGuid().ToString("N"),
            SourceNodeId = selfNode.Id,
            SourcePortId = selfNode.Ports.Single(port => port.Direction == BlueprintPortDirection.Output).Id,
            TargetNodeId = selfNode.Id,
            TargetPortId = selfNode.Ports.Single(port => port.Direction == BlueprintPortDirection.Input).Id
        });
        Throws<InvalidDataException>(() => BlueprintGraphValidator.Validate(self),
            "节点自连必须被拒绝");

        var cyclic = BlueprintGraphFactory.CreateDefault("card");
        var a = BlueprintGraphFactory.CreateNode(BlueprintNodeType.Description, 100d, 100d);
        var b = BlueprintGraphFactory.CreateNode(BlueprintNodeType.Description, 450d, 100d);
        var c = BlueprintGraphFactory.CreateNode(BlueprintNodeType.Description, 800d, 100d);
        cyclic.Nodes.AddRange([a, b, c]);
        cyclic.Connections.Add(CreateConnection(a, b));
        cyclic.Connections.Add(CreateConnection(b, c));
        cyclic.Connections.Add(CreateConnection(c, a));
        Throws<InvalidDataException>(() => BlueprintGraphValidator.Validate(cyclic),
            "间接有向循环必须被拒绝");
    }

    private static void RepositoryEnforcesWriteBudgetAndFormalProjectRoots(string temporaryRoot)
    {
        var parent = Path.Combine(temporaryRoot, "blueprint-protocol-storage");
        Directory.CreateDirectory(parent);
        var project = new ProjectWorkspaceService().Create(parent, "WriteBudget", "tilemap");
        var appPaths = new WorkspacePaths(Path.Combine(temporaryRoot, "blueprint-protocol-app-data"));
        var repository = new JsonBlueprintRepository(appPaths);
        var loaded = repository.LoadOrCreate(project.Paths.RootDirectory, "tilemap");
        var originalContent = File.ReadAllText(loaded.FilePath);
        var oversized = BlueprintJson.Clone(loaded.Graph);
        for (var index = 0; index < 270; index++)
        {
            var node = BlueprintGraphFactory.CreateNode(
                BlueprintNodeType.Description,
                300d + index,
                100d + index);
            node.Title = $"大文本节点 {index}";
            node.Description = new string('x', 32_000);
            oversized.Nodes.Add(node);
        }

        Throws<InvalidDataException>(
            () => repository.Save(project.Paths.RootDirectory, "tilemap", oversized),
            "超过 8 MiB 的蓝图必须在写盘前被拒绝");
        Equal(originalContent, File.ReadAllText(loaded.FilePath),
            "超限保存不得改变既有蓝图主文件");

        using (var exclusiveLock = new FileStream(
                   loaded.FilePath,
                   FileMode.Open,
                   FileAccess.ReadWrite,
                   FileShare.None))
        {
            Throws<IOException>(
                () => repository.LoadOrCreate(project.Paths.RootDirectory, "tilemap"),
                "主蓝图被独占锁定时必须中止，不能误回退到旧备份或安全默认图");
        }

        var ordinaryDirectory = Path.Combine(parent, "NotAProject");
        Directory.CreateDirectory(ordinaryDirectory);
        var untrustedPaths = new ProjectPaths(ordinaryDirectory);
        Throws<InvalidDataException>(
            () => repository.ResolveGraphPath(ordinaryDirectory, "tilemap"),
            "普通目录不能冒充正式项目根目录");
        True(!Directory.Exists(untrustedPaths.ProjectSettingsDirectory),
            "拒绝普通目录时不得代建 ProjectSettings 项目结构");
        True(!Directory.Exists(untrustedPaths.AssetDirectory),
            "拒绝普通目录时不得代建 Assets 项目结构");

        var validPlan = BlueprintExecutionPlanner.Compile(
            BlueprintGraphFactory.CreateDefault("tilemap"),
            "start");
        var dispatcher = new BlueprintRunWorkspaceService(appPaths);
        Throws<InvalidDataException>(
            () => dispatcher.DispatchAsync(validPlan, ordinaryDirectory).GetAwaiter().GetResult(),
            "运行发布同样只能接受正式项目根目录");
        True(!Directory.Exists(untrustedPaths.AiAssetDirectory) &&
             !Directory.Exists(untrustedPaths.BlueprintRunDirectory),
            "拒绝普通目录运行发布时不得创建 AI/Blueprint 目录");

        var isolatedPaths = new WorkspacePaths(Path.Combine(temporaryRoot, "blueprint-invalid-plan-app-data"));
        var invalidPlan = ClonePlan(validPlan);
        invalidPlan.Protocol = "untrusted.protocol";
        Throws<InvalidDataException>(
            () => new BlueprintRunWorkspaceService(isolatedPaths)
                .DispatchAsync(invalidPlan, null)
                .GetAwaiter()
                .GetResult(),
            "运行服务必须在创建目录前校验完整执行计划");
        True(!Directory.Exists(Path.Combine(isolatedPaths.WorkspaceDirectory, "BlueprintRuns")),
            "无效执行计划不得创建运行目录");
    }

    private static async Task EditorPersistsPerProjectAndPublishesFrozenRunAsync(string temporaryRoot)
    {
        var root = Path.Combine(temporaryRoot, "blueprint-project-isolation");
        Directory.CreateDirectory(root);
        var projectService = new ProjectWorkspaceService();
        var projectA = projectService.Create(root, "BlueprintA", "tilemap");
        var projectB = projectService.Create(root, "BlueprintB", "tilemap");
        var appPaths = new WorkspacePaths(Path.Combine(temporaryRoot, "blueprint-app-data"));
        using var console = new WorkbenchConsoleService(appPaths);
        var repository = new JsonBlueprintRepository(appPaths);
        using var viewModel = new BlueprintViewModel(
            repository,
            new BlueprintRunWorkspaceService(appPaths),
            console);

        viewModel.Activate(projectA.Paths.RootDirectory, "tilemap");
        Equal(1, viewModel.Nodes.Count, "项目 A 初始只有起始蓝图");
        viewModel.SelectedAddNodeType = viewModel.AddNodeTypes.Single(option =>
            option.Type == BlueprintNodeType.Description);
        var description = viewModel.AddNode(420d, 180d);
        description.Title = "项目 A 说明";
        description.Description = "只属于项目 A";
        description.TagsText = "A, tilemap";
        viewModel.MoveNode(description, 510d, 260d, commit: true);
        viewModel.SelectedAddNodeType = viewModel.AddNodeTypes.Single(option =>
            option.Type == BlueprintNodeType.Tool);
        var tool = viewModel.AddNode(840d, 260d);
        tool.AddOutputPortCommand.Execute(null);
        Equal(2, tool.OutputPorts.Count, "工具节点可添加额外输出接口");
        viewModel.SelectNode(description, additive: false);
        viewModel.SelectNode(tool, additive: true);
        viewModel.SelectedAddNodeType = viewModel.AddNodeTypes.Single(option =>
            option.Type == BlueprintNodeType.FunctionCollection);
        var function = viewModel.AddNode(1170d, 260d);
        True(function.FunctionMembersText.Contains("2 个成员", StringComparison.Ordinal),
            "功能集合创建时应捕获 Ctrl 多选节点");
        viewModel.SelectedAddNodeType = viewModel.AddNodeTypes.Single(option =>
            option.Type == BlueprintNodeType.Logic);
        viewModel.AddNode(1500d, 260d);
        Equal(5, viewModel.Nodes.Count,
            "编辑器可创建说明、工具、功能集合和逻辑四类用户蓝图");
        viewModel.SelectToolCommand.Execute("connect");
        var start = viewModel.Nodes.Single(node => node.IsStart);
        start.OutputPorts[0].ClickCommand.Execute(null);
        description.InputPorts[0].ClickCommand.Execute(null);
        Equal(1, viewModel.Connections.Count, "编辑器合法连线写入");
        viewModel.SaveNow();

        var projectAPath = repository.ResolveGraphPath(projectA.Paths.RootDirectory, "tilemap");
        True(File.Exists(projectAPath), "项目 A 蓝图应落在 ProjectSettings/Blueprints");
        True(projectAPath.StartsWith(projectA.Paths.ProjectSettingsDirectory, StringComparison.OrdinalIgnoreCase),
            "项目 A 蓝图路径必须位于项目 ProjectSettings 内");

        viewModel.Activate(projectB.Paths.RootDirectory, "tilemap");
        Equal(1, viewModel.Nodes.Count, "项目 B 不得继承项目 A 节点");
        viewModel.Activate(projectA.Paths.RootDirectory, "tilemap");
        Equal(5, viewModel.Nodes.Count, "切回项目 A 应恢复四类用户节点");
        Equal("只属于项目 A",
            viewModel.Nodes.Single(node => node.IsDescription).Description,
            "项目 A 说明内容恢复");
        Equal(510d, viewModel.Nodes.Single(node => node.IsDescription).X,
            "节点移动坐标项目级持久化");

        await viewModel.ExecuteAsync("start");
        True(!string.IsNullOrWhiteSpace(viewModel.LastRunDirectory) &&
             Directory.Exists(viewModel.LastRunDirectory),
            "起始按钮应创建可审计运行目录");
        True(viewModel.LastRunDirectory.StartsWith(
                projectA.Paths.BlueprintRunDirectory,
                StringComparison.OrdinalIgnoreCase),
            "正式项目的蓝图运行必须落在当前项目 AI/Blueprint 目录");
        True(AssetRootService.PathEquals(
                projectA.Paths.BlueprintRunDirectory,
                Directory.GetParent(viewModel.LastRunDirectory)?.FullName ?? string.Empty),
            "正式项目的蓝图运行目录应是 AI/Blueprint 的直接子目录");
        True(!Directory.Exists(projectA.Paths.LegacyGeneratedAssetDirectory),
            "正式蓝图运行不得新建旧 Generated 目录");
        True(File.Exists(Path.Combine(viewModel.LastRunDirectory, "execution-plan.v1.json")),
            "运行目录应包含冻结执行计划");
        var timelinePath = Path.Combine(viewModel.LastRunDirectory, "timeline.v1.jsonl");
        True(File.Exists(timelinePath),
            "运行目录应包含可继续追加的时间线");
        AssertJsonLinesAreNewlineTerminated(timelinePath);
        True(File.Exists(Path.Combine(viewModel.LastRunDirectory, "README.md")),
            "planned 提交前应先写入运行说明");
        var runStatePath = Path.Combine(viewModel.LastRunDirectory, "run-state.v1.json");
        True(File.Exists(runStatePath),
            "完整准备成功后必须写入唯一 planned 提交标记");
        using (var runState = JsonDocument.Parse(File.ReadAllText(runStatePath)))
        {
            Equal("planned", runState.RootElement.GetProperty("status").GetString(),
                "run-state 必须作为 planned 提交标记");
        }

        var firstTimelineLine = File.ReadLines(timelinePath).First();
        using (var timelineEntry = JsonDocument.Parse(firstTimelineLine))
        {
            Equal("prepared", timelineEntry.RootElement.GetProperty("status").GetString(),
                "run-state 提交前的 timeline 只能标记为 prepared");
        }

        File.WriteAllText(projectAPath, "{broken-primary");
        var recovered = repository.LoadOrCreate(projectA.Paths.RootDirectory, "tilemap");
        Equal("backup", recovered.RecoverySource,
            "损坏主蓝图应从最近一次有效备份恢复");
        Equal("{broken-primary", File.ReadAllText(projectAPath),
            "只读恢复不能覆盖损坏主文件，保留诊断证据");
    }

    private static BlueprintConnection CreateConnection(
        BlueprintNode source,
        BlueprintNode target,
        string? outputName = null)
    {
        var output = source.Ports
            .Where(port => port.Direction == BlueprintPortDirection.Output)
            .OrderBy(port => port.Order)
            .First(port => outputName is null ||
                string.Equals(port.Name, outputName, StringComparison.OrdinalIgnoreCase));
        var input = target.Ports
            .Where(port => port.Direction == BlueprintPortDirection.Input)
            .OrderBy(port => port.Order)
            .First();
        return new BlueprintConnection
        {
            Id = Guid.NewGuid().ToString("N"),
            SourceNodeId = source.Id,
            SourcePortId = output.Id,
            TargetNodeId = target.Id,
            TargetPortId = input.Id
        };
    }

    private static BlueprintExecutionPlan ClonePlan(BlueprintExecutionPlan plan) =>
        JsonSerializer.Deserialize<BlueprintExecutionPlan>(
            BlueprintJson.SerializePlan(plan),
            BlueprintJson.Options)
        ?? throw new InvalidOperationException("无法克隆蓝图执行计划。");

    private static void AssertJsonLinesAreNewlineTerminated(string path)
    {
        var content = File.ReadAllText(path);
        True(content.EndsWith(Environment.NewLine, StringComparison.Ordinal),
            "timeline JSONL 的首条及最后一条记录必须以换行符终止");

        var lines = content.Split([Environment.NewLine], StringSplitOptions.None);
        True(lines.Length >= 2 && lines[^1].Length == 0,
            "timeline JSONL 必须保留末尾空分段以证明最后一条已换行终止");
        foreach (var line in lines[..^1])
        {
            True(!string.IsNullOrWhiteSpace(line),
                "timeline JSONL 不应在事件之间写入空行");
            using var _ = JsonDocument.Parse(line);
        }
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
