namespace GAssetForge.Core.Workflows;

public static class WorkflowCatalog
{
    public static IReadOnlyList<WorkflowDefinition> All { get; } =
    [
        new(WorkflowKind.Tilemap, "tilemap", "Tilemap 制作", "场景资产",
            "面向地图拼块、地表组合和地图规则的独立业务工作区。", "#53D4C8", true),
        new(WorkflowKind.CardDesign, "card", "卡牌设计", "2D 资产",
            "面向卡面结构、插画、文字区与批量变体的独立业务工作区。", "#E77EB3", false),
        new(WorkflowKind.ParallelPerspective, "parallel", "平行透视角物体制作", "物体资产",
            "面向固定平行透视角物体资产、角度规范与批量校验的独立业务工作区。", "#E2AB63", false),
        new(WorkflowKind.UiDesign, "ui", "页面 UI 设计工具", "界面资产",
            "原生 WPF 页面编辑器，包含画布、图层、属性和 Prefab 资源。", "#65ADF2", true),
        new(WorkflowKind.Test, "test", "测试模板", "参考原型",
            "保留原 HTML 三套视觉原型作为迁移参考，不再作为桌面主线运行时。", "#9678FF", false)
    ];

    public static IReadOnlyList<WorkflowDefinition> Dockable { get; } =
        All.Where(item => item.Kind != WorkflowKind.Test).ToArray();

    public static WorkflowDefinition UiDesign => All.Single(item => item.Kind == WorkflowKind.UiDesign);

    public static WorkflowDefinition Find(string? key) =>
        All.FirstOrDefault(item => string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase)) ?? UiDesign;

    public static WorkflowDefinition FindDockable(string? key) =>
        Dockable.FirstOrDefault(item => string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase)) ?? UiDesign;
}
