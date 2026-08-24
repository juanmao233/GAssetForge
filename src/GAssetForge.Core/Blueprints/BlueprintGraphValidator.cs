namespace GAssetForge.Core.Blueprints;

public static class BlueprintGraphValidator
{
    public const int MaximumIdentifierLength = 160;
    public const int MaximumFunctionMemberNodeIds = 1_000;
    public const int MaximumNodes = 2_000;
    public const int MaximumConnections = 10_000;
    public const int MaximumPortsPerNode = 64;

    public static void Validate(BlueprintGraph graph)
    {
        if (graph is null)
        {
            throw new InvalidDataException("蓝图不能为空。");
        }

        if (graph.SchemaVersion != BlueprintGraph.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"不支持蓝图 Schema v{graph.SchemaVersion}；当前仅支持 v{BlueprintGraph.CurrentSchemaVersion}。");
        }

        if (graph.Viewport is null)
        {
            throw new InvalidDataException("蓝图缺少 viewport 必需结构。");
        }

        if (graph.Nodes is null)
        {
            throw new InvalidDataException("蓝图缺少 nodes 必需结构。");
        }

        if (graph.Connections is null)
        {
            throw new InvalidDataException("蓝图缺少 connections 必需结构。");
        }

        RequireId(graph.Id, "蓝图 ID");
        RequireId(graph.WorkflowKey, "工作流键", 80);
        RequireText(graph.Title, "蓝图标题", 160);
        if (graph.Nodes.Count == 0 || graph.Nodes.Count > MaximumNodes)
        {
            throw new InvalidDataException($"蓝图节点数量必须在 1 到 {MaximumNodes} 之间。");
        }

        if (graph.Connections.Count > MaximumConnections)
        {
            throw new InvalidDataException($"蓝图连线数量不能超过 {MaximumConnections}。");
        }

        if (!double.IsFinite(graph.Viewport.PanX) ||
            !double.IsFinite(graph.Viewport.PanY) ||
            !double.IsFinite(graph.Viewport.Zoom) ||
            graph.Viewport.Zoom is < 0.1d or > 4d)
        {
            throw new InvalidDataException("蓝图视口参数无效。");
        }

        var nodeIds = new HashSet<string>(StringComparer.Ordinal);
        var nodesById = new Dictionary<string, BlueprintNode>(StringComparer.Ordinal);
        var portsByNode = new Dictionary<string, Dictionary<string, BlueprintPort>>(StringComparer.Ordinal);
        var startCount = 0;
        foreach (var node in graph.Nodes)
        {
            if (node is null)
            {
                throw new InvalidDataException("蓝图包含 null 节点。");
            }

            if (node.Tags is null || node.Variables is null || node.FunctionMemberNodeIds is null)
            {
                throw new InvalidDataException($"节点 {node.Id} 的必需集合缺失。");
            }

            if (node.Ports is null)
            {
                throw new InvalidDataException($"节点 {node.Id} 缺少 ports 必需结构。");
            }

            // Start-node rules inspect port direction before the detailed port loop below. Reject
            // malformed JSON here so ports:[null] is always an InvalidDataException, never an NRE.
            if (node.Ports.Any(static port => port is null))
            {
                throw new InvalidDataException($"节点 {node.Id} 包含 null 端点。");
            }

            RequireId(node.Id, "节点 ID");
            if (!nodeIds.Add(node.Id))
            {
                throw new InvalidDataException($"蓝图包含重复节点 ID：{node.Id}。");
            }

            nodesById.Add(node.Id, node);
            RequireText(node.Title, $"节点 {node.Id} 标题", 160);
            if (!Enum.IsDefined(typeof(BlueprintNodeRole), node.Role))
            {
                throw new InvalidDataException($"节点 {node.Id} 的角色枚举无效。");
            }

            if (node.Type is { } nodeType && !Enum.IsDefined(typeof(BlueprintNodeType), nodeType))
            {
                throw new InvalidDataException($"节点 {node.Id} 的类型枚举无效。");
            }

            if (!double.IsFinite(node.X) || !double.IsFinite(node.Y) ||
                !double.IsFinite(node.Width) || node.Width is < 180d or > 800d)
            {
                throw new InvalidDataException($"节点 {node.Id} 的位置或宽度无效。");
            }

            if (node.Tags.Count > 32 || node.Tags.Any(tag => string.IsNullOrWhiteSpace(tag) || tag.Length > 64))
            {
                throw new InvalidDataException($"节点 {node.Id} 的标签无效。");
            }

            if (node.Role == BlueprintNodeRole.Start)
            {
                startCount++;
                if (node.Type is not null || node.Ports.Any(port => port.Direction == BlueprintPortDirection.Input))
                {
                    throw new InvalidDataException($"起始节点 {node.Id} 不属于四种用户节点类型且不能有输入端点。");
                }

                if (!node.Ports.Any(port =>
                        port.Direction == BlueprintPortDirection.Output &&
                        port.Kind == BlueprintPortKind.Flow))
                {
                    throw new InvalidDataException($"起始节点 {node.Id} 至少需要一个流程输出端点。");
                }
            }
            else if (node.Type is null)
            {
                throw new InvalidDataException($"普通节点 {node.Id} 缺少基础类型。");
            }

            if (node.Ports.Count > MaximumPortsPerNode)
            {
                throw new InvalidDataException($"节点 {node.Id} 的端点数量不能超过 {MaximumPortsPerNode}。");
            }

            var portIds = new HashSet<string>(StringComparer.Ordinal);
            var portOrders = new HashSet<string>(StringComparer.Ordinal);
            var portNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var portMap = new Dictionary<string, BlueprintPort>(StringComparer.Ordinal);
            foreach (var port in node.Ports)
            {
                if (port is null)
                {
                    throw new InvalidDataException($"节点 {node.Id} 包含 null 端点。");
                }

                RequireId(port.Id, $"节点 {node.Id} 的端点 ID");
                RequireText(port.Name, $"端点 {port.Id} 名称", 80);
                if (!Enum.IsDefined(typeof(BlueprintPortDirection), port.Direction) ||
                    !Enum.IsDefined(typeof(BlueprintPortKind), port.Kind) ||
                    !Enum.IsDefined(typeof(BlueprintValueKind), port.ValueKind))
                {
                    throw new InvalidDataException($"端点 {port.Id} 的方向或类型枚举无效。");
                }

                if (port.Kind == BlueprintPortKind.Flow && port.ValueKind != BlueprintValueKind.Any)
                {
                    throw new InvalidDataException($"流程端点 {port.Id} 的值类型必须为 Any。");
                }

                if (!portIds.Add(port.Id))
                {
                    throw new InvalidDataException($"节点 {node.Id} 包含重复端点 ID：{port.Id}。");
                }

                if (port.Order < 0)
                {
                    throw new InvalidDataException($"端点 {port.Id} 的顺序不能为负数。");
                }

                if (!portOrders.Add($"{port.Direction}\u001f{port.Order}"))
                {
                    throw new InvalidDataException(
                        $"节点 {node.Id} 的 {port.Direction} 端点顺序 {port.Order} 重复。");
                }

                if (!portNames.Add($"{port.Direction}\u001f{port.Name}"))
                {
                    throw new InvalidDataException(
                        $"节点 {node.Id} 的 {port.Direction} 端点名称 {port.Name} 重复。");
                }

                portMap.Add(port.Id, port);
            }

            portsByNode.Add(node.Id, portMap);
            ValidateNodePayload(node, nodesById: null);
        }

        if (startCount == 0)
        {
            throw new InvalidDataException("每个蓝图至少需要一个系统起始节点。");
        }

        foreach (var node in graph.Nodes)
        {
            ValidateNodePayload(node, nodesById);
        }

        var connectionIds = new HashSet<string>(StringComparer.Ordinal);
        var endpointPairs = new HashSet<string>(StringComparer.Ordinal);
        var occupiedInputs = new HashSet<string>(StringComparer.Ordinal);
        var sourceOrders = new HashSet<string>(StringComparer.Ordinal);
        var adjacency = graph.Nodes.ToDictionary(
            node => node.Id,
            _ => new List<string>(),
            StringComparer.Ordinal);
        foreach (var connection in graph.Connections)
        {
            if (connection is null)
            {
                throw new InvalidDataException("蓝图包含 null 连线。");
            }

            RequireId(connection.Id, "连线 ID");
            RequireId(connection.SourceNodeId, $"连线 {connection.Id} 源节点 ID");
            RequireId(connection.SourcePortId, $"连线 {connection.Id} 源端点 ID");
            RequireId(connection.TargetNodeId, $"连线 {connection.Id} 目标节点 ID");
            RequireId(connection.TargetPortId, $"连线 {connection.Id} 目标端点 ID");
            if (!connectionIds.Add(connection.Id))
            {
                throw new InvalidDataException($"蓝图包含重复连线 ID：{connection.Id}。");
            }

            if (connection.Order < 0)
            {
                throw new InvalidDataException($"连线 {connection.Id} 的顺序不能为负数。");
            }

            if (!sourceOrders.Add($"{connection.SourceNodeId}\u001f{connection.Order}"))
            {
                throw new InvalidDataException(
                    $"节点 {connection.SourceNodeId} 的连线顺序 {connection.Order} 重复。");
            }

            if (!nodesById.ContainsKey(connection.SourceNodeId) ||
                !nodesById.ContainsKey(connection.TargetNodeId))
            {
                throw new InvalidDataException($"连线 {connection.Id} 引用了不存在的节点。");
            }

            if (string.Equals(connection.SourceNodeId, connection.TargetNodeId, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"连线 {connection.Id} 不能连接节点自身。");
            }

            if (!portsByNode[connection.SourceNodeId].TryGetValue(connection.SourcePortId, out var sourcePort) ||
                !portsByNode[connection.TargetNodeId].TryGetValue(connection.TargetPortId, out var targetPort))
            {
                throw new InvalidDataException($"连线 {connection.Id} 引用了不存在的端点。");
            }

            if (sourcePort.Direction != BlueprintPortDirection.Output ||
                targetPort.Direction != BlueprintPortDirection.Input)
            {
                throw new InvalidDataException($"连线 {connection.Id} 必须从输出端点指向输入端点。");
            }

            if (sourcePort.Kind != targetPort.Kind || !AreValueKindsCompatible(sourcePort, targetPort))
            {
                throw new InvalidDataException($"连线 {connection.Id} 的端点类型不兼容。");
            }

            var endpointKey = string.Join('\u001f',
                connection.SourceNodeId,
                connection.SourcePortId,
                connection.TargetNodeId,
                connection.TargetPortId);
            if (!endpointPairs.Add(endpointKey))
            {
                throw new InvalidDataException($"检测到重复连线：{connection.Id}。");
            }

            var targetKey = $"{connection.TargetNodeId}\u001f{connection.TargetPortId}";
            if (!occupiedInputs.Add(targetKey))
            {
                throw new InvalidDataException($"输入端点 {connection.TargetPortId} 只能接收一条连线。");
            }

            adjacency[connection.SourceNodeId].Add(connection.TargetNodeId);
        }

        RejectCycles(adjacency);
    }

    private static void ValidateNodePayload(
        BlueprintNode node,
        IReadOnlyDictionary<string, BlueprintNode>? nodesById)
    {
        if (node.Role == BlueprintNodeRole.Start)
        {
            return;
        }

        switch (node.Type)
        {
            case BlueprintNodeType.Description:
                if (node.Description is null || node.Description.Length > 32_000)
                {
                    throw new InvalidDataException($"说明节点 {node.Id} 的文字过长。");
                }

                break;
            case BlueprintNodeType.Tool:
                if (node.ToolContractId is null || node.Variables.Any(variable => variable is null))
                {
                    throw new InvalidDataException($"工具节点 {node.Id} 的合同或变量结构缺失。");
                }

                BlueprintToolCatalog.Find(node.ToolContractId, node.ToolContractVersion);
                if (node.Variables.Count > 128 ||
                    node.Variables.Select(variable => variable.Id).Distinct(StringComparer.Ordinal).Count() !=
                    node.Variables.Count ||
                    node.Variables.Select(variable => variable.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() !=
                    node.Variables.Count ||
                    node.Variables.Any(variable =>
                        string.IsNullOrWhiteSpace(variable.Id) ||
                        string.IsNullOrWhiteSpace(variable.Name) || variable.Name.Length > 120 ||
                        variable.Value is null || variable.Value.Length > 16_000))
                {
                    throw new InvalidDataException($"工具节点 {node.Id} 的变量无效。");
                }

                break;
            case BlueprintNodeType.FunctionCollection:
                RequireText(node.FunctionKey, $"功能集合节点 {node.Id} 的功能键", 160);
                if (node.FunctionMemberNodeIds.Count > MaximumFunctionMemberNodeIds ||
                    node.FunctionMemberNodeIds.Distinct(StringComparer.Ordinal).Count() !=
                    node.FunctionMemberNodeIds.Count ||
                    node.FunctionMemberNodeIds.Any(memberId =>
                        string.IsNullOrWhiteSpace(memberId) ||
                        string.Equals(memberId, node.Id, StringComparison.Ordinal) ||
                        nodesById is not null && !nodesById.ContainsKey(memberId)))
                {
                    throw new InvalidDataException($"功能集合节点 {node.Id} 的成员引用无效。");
                }

                break;
            case BlueprintNodeType.Logic:
                if (node.ConditionText is null || node.ConditionText.Length > 16_000)
                {
                    throw new InvalidDataException($"逻辑节点 {node.Id} 的条件文字过长。");
                }

                var flowOutputs = node.Ports
                    .Where(port =>
                        port.Direction == BlueprintPortDirection.Output &&
                        port.Kind == BlueprintPortKind.Flow)
                    .Select(port => port.Name)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (!flowOutputs.Contains("True") || !flowOutputs.Contains("False"))
                {
                    throw new InvalidDataException($"If 节点 {node.Id} 必须保留 True/False 流程输出端点。");
                }

                break;
            default:
                throw new InvalidDataException($"节点 {node.Id} 的基础类型无效。");
        }
    }

    private static bool AreValueKindsCompatible(BlueprintPort source, BlueprintPort target) =>
        source.Kind == BlueprintPortKind.Flow ||
        source.ValueKind == BlueprintValueKind.Any ||
        target.ValueKind == BlueprintValueKind.Any ||
        source.ValueKind == target.ValueKind;

    private static void RejectCycles(IReadOnlyDictionary<string, List<string>> adjacency)
    {
        var states = adjacency.Keys.ToDictionary(nodeId => nodeId, _ => 0, StringComparer.Ordinal);
        foreach (var nodeId in adjacency.Keys.OrderBy(value => value, StringComparer.Ordinal))
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
                throw new InvalidDataException("蓝图首版不允许形成有向循环。");
            }

            states[nodeId] = 1;
            foreach (var target in adjacency[nodeId].Distinct(StringComparer.Ordinal))
            {
                Visit(target);
            }

            states[nodeId] = 2;
        }
    }

    private static void RequireId(
        string value,
        string label,
        int maximumLength = MaximumIdentifierLength)
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
