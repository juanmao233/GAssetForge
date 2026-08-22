namespace GAssetForge.App.ViewModels;

public sealed class BlueprintViewModel
{
    public string Header => "Agent 工作流蓝图";

    public string Description =>
        "用于描述 Agent 如何在不同工具之间传递输入、执行任务并回收结果。";

    public string EmptyStateTitle => "蓝图工作区已就绪";

    public string EmptyStateDescription =>
        "当前先保留稳定的停靠画布。下一步可在这里定义节点类型、连接规则、执行条件与 Agent 轨迹。";

    public IReadOnlyList<string> PlannedNodeKinds { get; } =
        ["Agent 节点", "工具节点", "条件与连线"];

    public string StatusText => "空蓝图 · 0 个节点 · 0 条连接";

    public string PersistenceText => "尚未创建项目级蓝图数据";
}
