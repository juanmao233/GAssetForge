namespace GAssetForge.Core.Blueprints;

public sealed class BlueprintExecutionPlan
{
    public const string ProtocolName = "gassetforge.blueprint.execution-plan";
    public const int CurrentSchemaVersion = 1;

    public string Protocol { get; set; } = ProtocolName;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public string GraphId { get; set; } = string.Empty;

    public string WorkflowKey { get; set; } = string.Empty;

    public string StartNodeId { get; set; } = string.Empty;

    public List<BlueprintExecutionStep> Steps { get; set; } = [];
}

public sealed class BlueprintExecutionStep
{
    public string StepId { get; set; } = string.Empty;

    public int Sequence { get; set; }

    public string NodeId { get; set; } = string.Empty;

    public BlueprintNodeRole Role { get; set; }

    public BlueprintNodeType? Type { get; set; }

    public string Title { get; set; } = string.Empty;

    public BlueprintExecutorKind Executor { get; set; }

    public string ContractId { get; set; } = string.Empty;

    public int ContractVersion { get; set; } = 1;

    public List<string> Tags { get; set; } = [];

    public List<string> DependsOn { get; set; } = [];

    public List<BlueprintRoute> Routes { get; set; } = [];

    public SortedDictionary<string, string> Parameters { get; set; } =
        new(StringComparer.Ordinal);
}

public sealed class BlueprintRoute
{
    public string OutputPortId { get; set; } = string.Empty;

    public string OutputName { get; set; } = string.Empty;

    public BlueprintPortKind Kind { get; set; }

    public BlueprintValueKind ValueKind { get; set; }

    public BlueprintValueKind SourceValueKind { get; set; }

    public string TargetNodeId { get; set; } = string.Empty;

    public string TargetPortId { get; set; } = string.Empty;

    public string TargetInputName { get; set; } = string.Empty;
}

public sealed record BlueprintDispatchReceipt(
    string RunId,
    string RunDirectory,
    string Status);

public interface IBlueprintExecutionDispatcher
{
    Task<BlueprintDispatchReceipt> DispatchAsync(
        BlueprintExecutionPlan plan,
        string? projectRoot,
        CancellationToken cancellationToken = default);
}

public static class BlueprintExecutionPlanner
{
    public static BlueprintExecutionPlan Compile(BlueprintGraph graph, string startNodeId)
    {
        BlueprintGraphValidator.Validate(graph);
        var nodes = graph.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        if (!nodes.TryGetValue(startNodeId, out var start) || start.Role != BlueprintNodeRole.Start)
        {
            throw new InvalidDataException("执行计划必须从有效的系统起始节点编译。");
        }

        var outgoing = graph.Nodes.ToDictionary(
            node => node.Id,
            _ => new List<BlueprintConnection>(),
            StringComparer.Ordinal);
        var incoming = graph.Nodes.ToDictionary(
            node => node.Id,
            _ => new List<BlueprintConnection>(),
            StringComparer.Ordinal);
        foreach (var connection in graph.Connections)
        {
            outgoing[connection.SourceNodeId].Add(connection);
            incoming[connection.TargetNodeId].Add(connection);
        }

        var reachable = CollectReachable(startNodeId, outgoing);
        var indegree = reachable.ToDictionary(
            nodeId => nodeId,
            nodeId => incoming[nodeId].Count(edge => reachable.Contains(edge.SourceNodeId)),
            StringComparer.Ordinal);
        var ready = new SortedSet<string>(
            indegree.Where(pair => pair.Value == 0).Select(pair => pair.Key),
            StringComparer.Ordinal);
        var ordered = new List<string>(reachable.Count);
        while (ready.Count > 0)
        {
            var nodeId = ready.Min!;
            ready.Remove(nodeId);
            ordered.Add(nodeId);
            foreach (var edge in outgoing[nodeId]
                         .Where(edge => reachable.Contains(edge.TargetNodeId))
                         .OrderBy(edge => edge.Order)
                         .ThenBy(edge => edge.TargetNodeId, StringComparer.Ordinal))
            {
                indegree[edge.TargetNodeId]--;
                if (indegree[edge.TargetNodeId] == 0)
                {
                    ready.Add(edge.TargetNodeId);
                }
            }
        }

        if (ordered.Count != reachable.Count)
        {
            throw new InvalidDataException("蓝图执行计划包含循环，无法编译。");
        }

        var plan = new BlueprintExecutionPlan
        {
            GraphId = graph.Id,
            WorkflowKey = graph.WorkflowKey,
            StartNodeId = startNodeId
        };
        for (var index = 0; index < ordered.Count; index++)
        {
            var node = nodes[ordered[index]];
            var step = CreateStep(node, index);
            step.DependsOn = incoming[node.Id]
                .Where(edge => reachable.Contains(edge.SourceNodeId))
                .Select(edge => edge.SourceNodeId)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToList();
            step.Routes = outgoing[node.Id]
                .Where(edge => reachable.Contains(edge.TargetNodeId))
                .OrderBy(edge => edge.Order)
                .ThenBy(edge => edge.TargetNodeId, StringComparer.Ordinal)
                .Select(edge => CreateRoute(node, nodes[edge.TargetNodeId], edge))
                .ToList();
            plan.Steps.Add(step);
        }

        BlueprintExecutionPlanValidator.Validate(plan);
        return plan;
    }

    private static BlueprintRoute CreateRoute(
        BlueprintNode sourceNode,
        BlueprintNode targetNode,
        BlueprintConnection edge)
    {
        var sourcePort = sourceNode.Ports.Single(port => port.Id == edge.SourcePortId);
        var targetPort = targetNode.Ports.Single(port => port.Id == edge.TargetPortId);
        return new BlueprintRoute
        {
            OutputPortId = sourcePort.Id,
            OutputName = sourcePort.Name,
            Kind = sourcePort.Kind,
            ValueKind = sourcePort.Kind == BlueprintPortKind.Flow
                ? BlueprintValueKind.Any
                : sourcePort.ValueKind == BlueprintValueKind.Any
                    ? targetPort.ValueKind
                    : sourcePort.ValueKind,
            SourceValueKind = sourcePort.Kind == BlueprintPortKind.Flow
                ? BlueprintValueKind.Any
                : sourcePort.ValueKind,
            TargetNodeId = targetNode.Id,
            TargetPortId = targetPort.Id,
            TargetInputName = targetPort.Name
        };
    }

    private static HashSet<string> CollectReachable(
        string startNodeId,
        IReadOnlyDictionary<string, List<BlueprintConnection>> outgoing)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<string>();
        pending.Enqueue(startNodeId);
        while (pending.TryDequeue(out var nodeId))
        {
            if (!result.Add(nodeId))
            {
                continue;
            }

            foreach (var edge in outgoing[nodeId]
                         .OrderBy(edge => edge.Order)
                         .ThenBy(edge => edge.TargetNodeId, StringComparer.Ordinal))
            {
                pending.Enqueue(edge.TargetNodeId);
            }
        }

        return result;
    }

    private static BlueprintExecutionStep CreateStep(BlueprintNode node, int sequence)
    {
        var step = new BlueprintExecutionStep
        {
            StepId = $"step-{sequence:D4}-{node.Id}",
            Sequence = sequence,
            NodeId = node.Id,
            Role = node.Role,
            Type = node.Type,
            Title = node.Title,
            Tags = node.Tags.OrderBy(value => value, StringComparer.Ordinal).ToList()
        };

        if (node.Role == BlueprintNodeRole.Start)
        {
            step.Executor = BlueprintExecutorKind.Control;
            step.ContractId = "gassetforge.control.start";
            return step;
        }

        switch (node.Type)
        {
            case BlueprintNodeType.Description:
                step.Executor = BlueprintExecutorKind.Agent;
                step.ContractId = "gassetforge.agent.instruction";
                step.Parameters["instruction"] = node.Description;
                break;
            case BlueprintNodeType.Tool:
                step.Executor = BlueprintExecutorKind.Harness;
                step.ContractId = node.ToolContractId;
                step.ContractVersion = node.ToolContractVersion;
                foreach (var variable in node.Variables.OrderBy(variable => variable.Name, StringComparer.Ordinal))
                {
                    step.Parameters[variable.Name] = variable.Value;
                }

                break;
            case BlueprintNodeType.FunctionCollection:
                step.Executor = BlueprintExecutorKind.Harness;
                step.ContractId = "gassetforge.harness.function-collection";
                step.Parameters["functionKey"] = node.FunctionKey;
                step.Parameters["memberNodeIds"] = string.Join(",", node.FunctionMemberNodeIds
                    .OrderBy(value => value, StringComparer.Ordinal));
                break;
            case BlueprintNodeType.Logic:
                step.Executor = BlueprintExecutorKind.Agent;
                step.ContractId = "gassetforge.agent.text-condition";
                step.Parameters["condition"] = node.ConditionText;
                step.Parameters["resultSchema"] = "{schemaVersion:1,decision:boolean,reason:string}";
                break;
            default:
                throw new InvalidDataException($"节点 {node.Id} 的执行类型无效。");
        }

        return step;
    }
}
