namespace GAssetForge.Core.Blueprints;

public enum BlueprintNodeRole
{
    Standard,
    Start
}

public enum BlueprintNodeType
{
    Description,
    Tool,
    FunctionCollection,
    Logic
}

public enum BlueprintPortDirection
{
    Input,
    Output
}

public enum BlueprintPortKind
{
    Flow,
    Data
}

public enum BlueprintValueKind
{
    Any,
    Text,
    Boolean,
    Number,
    Json,
    ImageFile,
    File,
    Directory
}

public enum BlueprintExecutorKind
{
    Control,
    Harness,
    Agent
}

public sealed class BlueprintGraph
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string WorkflowKey { get; set; } = string.Empty;

    public string Title { get; set; } = "工作流蓝图";

    public BlueprintViewport Viewport { get; set; } = new();

    public List<BlueprintNode> Nodes { get; set; } = [];

    public List<BlueprintConnection> Connections { get; set; } = [];

    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class BlueprintViewport
{
    public double PanX { get; set; }

    public double PanY { get; set; }

    public double Zoom { get; set; } = 1d;
}

public sealed class BlueprintNode
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public BlueprintNodeRole Role { get; set; }

    public BlueprintNodeType? Type { get; set; }

    public string Title { get; set; } = string.Empty;

    public List<string> Tags { get; set; } = [];

    public double X { get; set; }

    public double Y { get; set; }

    public double Width { get; set; } = 286d;

    public string Description { get; set; } = string.Empty;

    public string ToolContractId { get; set; } = string.Empty;

    public int ToolContractVersion { get; set; } = 1;

    public List<BlueprintVariable> Variables { get; set; } = [];

    public string FunctionKey { get; set; } = string.Empty;

    public List<string> FunctionMemberNodeIds { get; set; } = [];

    public string ConditionText { get; set; } = string.Empty;

    public List<BlueprintPort> Ports { get; set; } = [];
}

public sealed class BlueprintVariable
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;
}

public sealed class BlueprintPort
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = string.Empty;

    public BlueprintPortDirection Direction { get; set; }

    public BlueprintPortKind Kind { get; set; } = BlueprintPortKind.Flow;

    public BlueprintValueKind ValueKind { get; set; } = BlueprintValueKind.Any;

    public int Order { get; set; }
}

public sealed class BlueprintConnection
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string SourceNodeId { get; set; } = string.Empty;

    public string SourcePortId { get; set; } = string.Empty;

    public string TargetNodeId { get; set; } = string.Empty;

    public string TargetPortId { get; set; } = string.Empty;

    public int Order { get; set; }
}

public sealed record BlueprintToolDefinition(
    string ContractId,
    int ContractVersion,
    string DisplayName,
    IReadOnlyDictionary<string, string> DefaultVariables);

public static class BlueprintToolCatalog
{
    public const string PixelAlignmentContractId = "gassetforge.harness.pixel-alignment";
    public const string BackgroundRemovalContractId = "gassetforge.harness.background-removal";

    public static IReadOnlyList<BlueprintToolDefinition> All { get; } =
    [
        new(
            PixelAlignmentContractId,
            1,
            "像素点对齐工具",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["columns"] = "32",
                ["rows"] = "32",
                ["samplingMode"] = "center"
            }),
        new(
            BackgroundRemovalContractId,
            1,
            "背景剔除工具",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["preset"] = "magenta",
                ["transparentThreshold"] = "18",
                ["opaqueThreshold"] = "64"
            })
    ];

    public static BlueprintToolDefinition Find(string? contractId, int contractVersion = 1) =>
        All.FirstOrDefault(item =>
            string.Equals(item.ContractId, contractId, StringComparison.Ordinal) &&
            item.ContractVersion == contractVersion)
        ?? throw new InvalidDataException($"未知的蓝图工具合同：{contractId ?? "<null>"} v{contractVersion}。");
}

public static class BlueprintGraphFactory
{
    public static BlueprintGraph CreateDefault(string workflowKey, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowKey);
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        return new BlueprintGraph
        {
            Id = Guid.NewGuid().ToString("N"),
            WorkflowKey = workflowKey,
            Title = $"{workflowKey} 工作流蓝图",
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            Nodes =
            [
                new BlueprintNode
                {
                    Id = "start",
                    Role = BlueprintNodeRole.Start,
                    Type = null,
                    Title = "开始执行",
                    Tags = ["系统", "入口"],
                    X = 120d,
                    Y = 180d,
                    Description = "点击按钮后冻结当前图并生成版本化 Harness 执行计划。",
                    Ports =
                    [
                        CreatePort("执行", BlueprintPortDirection.Output, 0, "start-flow-out")
                    ]
                }
            ]
        };
    }

    public static BlueprintNode CreateNode(BlueprintNodeType type, double x, double y)
    {
        var node = new BlueprintNode
        {
            Id = Guid.NewGuid().ToString("N"),
            Role = BlueprintNodeRole.Standard,
            Type = type,
            Title = type switch
            {
                BlueprintNodeType.Description => "说明蓝图",
                BlueprintNodeType.Tool => "工具类蓝图",
                BlueprintNodeType.FunctionCollection => "功能集合蓝图",
                BlueprintNodeType.Logic => "If 逻辑蓝图",
                _ => "蓝图"
            },
            X = x,
            Y = y,
            Description = type == BlueprintNodeType.Description
                ? "在这里输入需要 Agent 执行或理解的说明。"
                : string.Empty,
            ConditionText = type == BlueprintNodeType.Logic
                ? "如果上一步结果满足这里输入的条件"
                : string.Empty
        };

        node.Ports.Add(CreatePort("输入", BlueprintPortDirection.Input, 0));
        if (type == BlueprintNodeType.Logic)
        {
            node.Ports.Add(CreatePort("True", BlueprintPortDirection.Output, 0));
            node.Ports.Add(CreatePort("False", BlueprintPortDirection.Output, 1));
        }
        else
        {
            node.Ports.Add(CreatePort("输出", BlueprintPortDirection.Output, 0));
        }

        if (type == BlueprintNodeType.Tool)
        {
            ApplyToolDefinition(node, BlueprintToolCatalog.All[0]);
        }
        else if (type == BlueprintNodeType.FunctionCollection)
        {
            node.FunctionKey = $"function.{node.Id}";
        }

        return node;
    }

    public static BlueprintPort CreatePort(
        string name,
        BlueprintPortDirection direction,
        int order,
        string? id = null,
        BlueprintPortKind kind = BlueprintPortKind.Flow,
        BlueprintValueKind valueKind = BlueprintValueKind.Any) =>
        new()
        {
            Id = id ?? Guid.NewGuid().ToString("N"),
            Name = name,
            Direction = direction,
            Kind = kind,
            ValueKind = valueKind,
            Order = order
        };

    public static void ApplyToolDefinition(BlueprintNode node, BlueprintToolDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(definition);
        node.ToolContractId = definition.ContractId;
        node.ToolContractVersion = definition.ContractVersion;
        node.Variables = definition.DefaultVariables
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new BlueprintVariable
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = pair.Key,
                Value = pair.Value
            })
            .ToList();
    }
}
