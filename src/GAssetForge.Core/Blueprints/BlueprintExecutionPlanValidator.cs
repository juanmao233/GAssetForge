namespace GAssetForge.Core.Blueprints;

public static class BlueprintExecutionPlanValidator
{
    private const int GeneratedStepIdPrefixLength = 10;

    public const int MaximumSteps = BlueprintGraphValidator.MaximumNodes;
    public const int MaximumRoutes = BlueprintGraphValidator.MaximumConnections;
    public const int MaximumStepIdLength =
        BlueprintGraphValidator.MaximumIdentifierLength + GeneratedStepIdPrefixLength;
    public const int MaximumParameterValueLength =
        BlueprintGraphValidator.MaximumFunctionMemberNodeIds *
        (BlueprintGraphValidator.MaximumIdentifierLength + 1);

    public static void Validate(BlueprintExecutionPlan plan)
    {
        if (plan is null)
        {
            throw new InvalidDataException("蓝图执行计划不能为空。");
        }

        if (!string.Equals(plan.Protocol, BlueprintExecutionPlan.ProtocolName, StringComparison.Ordinal))
        {
            throw new InvalidDataException("蓝图执行计划协议无效。");
        }

        if (plan.SchemaVersion != BlueprintExecutionPlan.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"不支持执行计划 Schema v{plan.SchemaVersion}；当前仅支持 v{BlueprintExecutionPlan.CurrentSchemaVersion}。");
        }

        RequireId(plan.GraphId, "执行计划 Graph ID");
        RequireId(plan.WorkflowKey, "执行计划工作流键", 80);
        RequireId(plan.StartNodeId, "执行计划起始节点 ID");
        if (plan.Steps is null || plan.Steps.Count == 0 || plan.Steps.Count > MaximumSteps)
        {
            throw new InvalidDataException($"执行计划步骤数量必须在 1 到 {MaximumSteps} 之间。");
        }

        if (plan.Steps.Any(static step => step is null))
        {
            throw new InvalidDataException("执行计划包含 null 步骤。");
        }

        var stepIds = new HashSet<string>(StringComparer.Ordinal);
        var nodeIds = new HashSet<string>(StringComparer.Ordinal);
        var stepsByNode = new Dictionary<string, BlueprintExecutionStep>(StringComparer.Ordinal);
        var startCount = 0;
        var routeCount = 0;
        for (var index = 0; index < plan.Steps.Count; index++)
        {
            var step = plan.Steps[index];
            RequireId(step.StepId, $"步骤 {index} ID", MaximumStepIdLength);
            RequireId(step.NodeId, $"步骤 {step.StepId} 节点 ID");
            RequireText(step.Title, $"步骤 {step.StepId} 标题", 160);
            if (!stepIds.Add(step.StepId))
            {
                throw new InvalidDataException($"执行计划包含重复步骤 ID：{step.StepId}。");
            }

            if (!nodeIds.Add(step.NodeId))
            {
                throw new InvalidDataException($"执行计划包含重复节点 ID：{step.NodeId}。");
            }

            if (step.Sequence != index)
            {
                throw new InvalidDataException($"步骤 {step.StepId} 的 sequence 必须与列表顺序连续一致。");
            }

            if (!Enum.IsDefined(typeof(BlueprintNodeRole), step.Role) ||
                !Enum.IsDefined(typeof(BlueprintExecutorKind), step.Executor) ||
                step.Type is { } type && !Enum.IsDefined(typeof(BlueprintNodeType), type))
            {
                throw new InvalidDataException($"步骤 {step.StepId} 的角色、类型或执行器枚举无效。");
            }

            if (step.Tags is null || step.DependsOn is null || step.Routes is null || step.Parameters is null)
            {
                throw new InvalidDataException($"步骤 {step.StepId} 缺少必需集合。");
            }

            ValidateTags(step);
            ValidateParameters(step);
            ValidateContract(step, plan.StartNodeId);
            routeCount += step.Routes.Count;
            if (routeCount > MaximumRoutes)
            {
                throw new InvalidDataException($"执行计划路由数量不能超过 {MaximumRoutes}。");
            }

            if (step.Role == BlueprintNodeRole.Start)
            {
                startCount++;
            }

            stepsByNode.Add(step.NodeId, step);
        }

        if (startCount != 1 ||
            plan.Steps[0].Role != BlueprintNodeRole.Start ||
            !string.Equals(plan.Steps[0].NodeId, plan.StartNodeId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("执行计划必须以唯一且匹配 startNodeId 的起始步骤开头。");
        }

        ValidateDependenciesAndRoutes(plan.Steps, stepsByNode);
    }

    private static void ValidateTags(BlueprintExecutionStep step)
    {
        if (step.Tags.Count > 32 ||
            step.Tags.Any(tag => string.IsNullOrWhiteSpace(tag) || tag.Length > 64))
        {
            throw new InvalidDataException($"步骤 {step.StepId} 的标签无效。");
        }
    }

    private static void ValidateParameters(BlueprintExecutionStep step)
    {
        if (step.Parameters.Count > 256 || step.Parameters.Any(pair =>
                string.IsNullOrWhiteSpace(pair.Key) || pair.Key.Length > 160 ||
                pair.Value is null || pair.Value.Length > MaximumParameterValueLength))
        {
            throw new InvalidDataException($"步骤 {step.StepId} 的参数无效。");
        }
    }

    private static void ValidateContract(BlueprintExecutionStep step, string startNodeId)
    {
        RequireId(step.ContractId, $"步骤 {step.StepId} 合同 ID");
        if (step.ContractVersion <= 0)
        {
            throw new InvalidDataException($"步骤 {step.StepId} 的合同版本无效。");
        }

        if (step.Role == BlueprintNodeRole.Start)
        {
            if (!string.Equals(step.NodeId, startNodeId, StringComparison.Ordinal) ||
                step.Type is not null ||
                step.Executor != BlueprintExecutorKind.Control ||
                !string.Equals(step.ContractId, "gassetforge.control.start", StringComparison.Ordinal) ||
                step.ContractVersion != 1 ||
                step.DependsOn.Count != 0)
            {
                throw new InvalidDataException($"起始步骤 {step.StepId} 的角色、执行器或合同映射无效。");
            }

            return;
        }

        if (step.Role != BlueprintNodeRole.Standard || step.Type is null ||
            string.Equals(step.NodeId, startNodeId, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"普通步骤 {step.StepId} 的节点角色或类型无效。");
        }

        switch (step.Type.Value)
        {
            case BlueprintNodeType.Description:
                RequireMapping(step, BlueprintExecutorKind.Agent, "gassetforge.agent.instruction", 1);
                RequireParameter(step, "instruction");
                break;
            case BlueprintNodeType.Tool:
                if (step.Executor != BlueprintExecutorKind.Harness)
                {
                    throw new InvalidDataException($"工具步骤 {step.StepId} 必须由 Harness 执行。");
                }

                _ = BlueprintToolCatalog.Find(step.ContractId, step.ContractVersion);
                break;
            case BlueprintNodeType.FunctionCollection:
                RequireMapping(step, BlueprintExecutorKind.Harness, "gassetforge.harness.function-collection", 1);
                RequireParameter(step, "functionKey");
                RequireParameter(step, "memberNodeIds");
                break;
            case BlueprintNodeType.Logic:
                RequireMapping(step, BlueprintExecutorKind.Agent, "gassetforge.agent.text-condition", 1);
                RequireParameter(step, "condition");
                RequireParameter(step, "resultSchema");
                break;
            default:
                throw new InvalidDataException($"步骤 {step.StepId} 的节点类型无效。");
        }
    }

    private static void RequireMapping(
        BlueprintExecutionStep step,
        BlueprintExecutorKind executor,
        string contractId,
        int contractVersion)
    {
        if (step.Executor != executor ||
            !string.Equals(step.ContractId, contractId, StringComparison.Ordinal) ||
            step.ContractVersion != contractVersion)
        {
            throw new InvalidDataException($"步骤 {step.StepId} 的执行器与合同映射无效。");
        }
    }

    private static void RequireParameter(BlueprintExecutionStep step, string name)
    {
        if (!step.Parameters.ContainsKey(name))
        {
            throw new InvalidDataException($"步骤 {step.StepId} 缺少参数 {name}。");
        }
    }

    private static void ValidateDependenciesAndRoutes(
        IReadOnlyList<BlueprintExecutionStep> steps,
        IReadOnlyDictionary<string, BlueprintExecutionStep> stepsByNode)
    {
        var occupiedInputs = new HashSet<string>(StringComparer.Ordinal);
        var routeKeys = new HashSet<string>(StringComparer.Ordinal);
        var sourcePortMetadata = new Dictionary<
            string,
            (string OutputName, BlueprintPortKind Kind, BlueprintValueKind SourceValueKind)>(
            StringComparer.Ordinal);
        var adjacency = steps.ToDictionary(
            step => step.NodeId,
            _ => new List<string>(),
            StringComparer.Ordinal);

        foreach (var step in steps)
        {
            if (step.DependsOn.Count > MaximumSteps || step.Routes.Count > MaximumRoutes)
            {
                throw new InvalidDataException($"步骤 {step.StepId} 的依赖或路由数量超出限制。");
            }

            var dependencyIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var dependencyId in step.DependsOn)
            {
                RequireId(dependencyId, $"步骤 {step.StepId} 依赖节点 ID");
                if (!dependencyIds.Add(dependencyId) ||
                    !stepsByNode.TryGetValue(dependencyId, out var dependency) ||
                    string.Equals(dependencyId, step.NodeId, StringComparison.Ordinal) ||
                    dependency.Sequence >= step.Sequence)
                {
                    throw new InvalidDataException($"步骤 {step.StepId} 的依赖关系或拓扑顺序无效。");
                }
            }

            foreach (var route in step.Routes)
            {
                if (route is null)
                {
                    throw new InvalidDataException($"步骤 {step.StepId} 包含 null 路由。");
                }

                RequireId(route.OutputPortId, $"步骤 {step.StepId} 路由输出端点 ID");
                RequireText(route.OutputName, $"步骤 {step.StepId} 路由输出名称", 80);
                RequireId(route.TargetNodeId, $"步骤 {step.StepId} 路由目标节点 ID");
                RequireId(route.TargetPortId, $"步骤 {step.StepId} 路由目标端点 ID");
                RequireText(route.TargetInputName, $"步骤 {step.StepId} 路由目标输入名称", 80);
                if (!Enum.IsDefined(typeof(BlueprintPortKind), route.Kind) ||
                    !Enum.IsDefined(typeof(BlueprintValueKind), route.ValueKind) ||
                    !Enum.IsDefined(typeof(BlueprintValueKind), route.SourceValueKind) ||
                    route.Kind == BlueprintPortKind.Flow &&
                    (route.ValueKind != BlueprintValueKind.Any ||
                     route.SourceValueKind != BlueprintValueKind.Any) ||
                    route.Kind == BlueprintPortKind.Data &&
                    route.SourceValueKind != BlueprintValueKind.Any &&
                    route.ValueKind != route.SourceValueKind)
                {
                    throw new InvalidDataException($"步骤 {step.StepId} 的路由类型无效。");
                }

                var sourcePortKey = $"{step.NodeId}\u001f{route.OutputPortId}";
                var metadata = (route.OutputName, route.Kind, route.SourceValueKind);
                if (sourcePortMetadata.TryGetValue(sourcePortKey, out var frozenMetadata))
                {
                    if (!string.Equals(
                            frozenMetadata.OutputName,
                            metadata.OutputName,
                            StringComparison.Ordinal) ||
                        frozenMetadata.Kind != metadata.Kind ||
                        frozenMetadata.SourceValueKind != metadata.SourceValueKind)
                    {
                        throw new InvalidDataException(
                            $"步骤 {step.StepId} 的同一输出端点包含矛盾的冻结元数据。");
                    }
                }
                else
                {
                    sourcePortMetadata.Add(sourcePortKey, metadata);
                }

                if (!stepsByNode.TryGetValue(route.TargetNodeId, out var target) ||
                    string.Equals(route.TargetNodeId, step.NodeId, StringComparison.Ordinal) ||
                    target.Sequence <= step.Sequence)
                {
                    throw new InvalidDataException($"步骤 {step.StepId} 的路由目标或拓扑顺序无效。");
                }

                var routeKey = string.Join('\u001f',
                    step.NodeId,
                    route.OutputPortId,
                    route.TargetNodeId,
                    route.TargetPortId);
                if (!routeKeys.Add(routeKey))
                {
                    throw new InvalidDataException($"步骤 {step.StepId} 包含重复路由。");
                }

                var inputKey = $"{route.TargetNodeId}\u001f{route.TargetPortId}";
                if (!occupiedInputs.Add(inputKey))
                {
                    throw new InvalidDataException($"执行计划输入端点 {route.TargetPortId} 被重复绑定。");
                }

                if (!target.DependsOn.Contains(step.NodeId, StringComparer.Ordinal))
                {
                    throw new InvalidDataException($"步骤 {step.StepId} 的路由与目标依赖不一致。");
                }

                adjacency[step.NodeId].Add(route.TargetNodeId);
            }
        }

        foreach (var step in steps)
        {
            foreach (var dependencyId in step.DependsOn)
            {
                if (!stepsByNode[dependencyId].Routes.Any(route =>
                        string.Equals(route.TargetNodeId, step.NodeId, StringComparison.Ordinal)))
                {
                    throw new InvalidDataException($"步骤 {step.StepId} 的依赖缺少对应路由。");
                }
            }
        }

        RejectCycles(adjacency);
        var reachable = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>();
        pending.Push(steps[0].NodeId);
        while (pending.TryPop(out var nodeId))
        {
            if (!reachable.Add(nodeId))
            {
                continue;
            }

            foreach (var target in adjacency[nodeId])
            {
                pending.Push(target);
            }
        }

        if (reachable.Count != steps.Count)
        {
            throw new InvalidDataException("执行计划包含无法从起始步骤到达的孤立步骤。");
        }
    }

    private static void RejectCycles(IReadOnlyDictionary<string, List<string>> adjacency)
    {
        var states = adjacency.Keys.ToDictionary(nodeId => nodeId, _ => 0, StringComparer.Ordinal);
        foreach (var nodeId in adjacency.Keys)
        {
            Visit(nodeId);
        }

        return;

        void Visit(string nodeId)
        {
            if (states[nodeId] == 2)
            {
                return;
            }

            if (states[nodeId] == 1)
            {
                throw new InvalidDataException("执行计划路由包含循环。");
            }

            states[nodeId] = 1;
            foreach (var target in adjacency[nodeId])
            {
                Visit(target);
            }

            states[nodeId] = 2;
        }
    }

    private static void RequireId(string value, string label, int maximumLength = 160)
    {
        RequireText(value, label, maximumLength);
        if (value.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')))
        {
            throw new InvalidDataException($"{label} 只能包含字母、数字、点、横线和下划线。");
        }
    }

    private static void RequireText(string value, string label, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength)
        {
            throw new InvalidDataException($"{label} 不能为空且不能超过 {maximumLength} 个字符。");
        }
    }
}
