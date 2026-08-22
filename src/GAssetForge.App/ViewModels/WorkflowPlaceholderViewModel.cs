using GAssetForge.Core.Workflows;

namespace GAssetForge.App.ViewModels;

public sealed record WorkflowPlaceholderViewModel(WorkflowDefinition Workflow)
{
    public string StateLabel => Workflow.IsImplemented ? "功能已接入" : "框架入口已建立";
    public string NextStep => Workflow.Kind switch
    {
        WorkflowKind.Tilemap => "基础网格已接入；后续增加 Tile 规则、地图层级、批量生成与引擎导出。",
        WorkflowKind.CardDesign => "后续接入卡面区域、数据绑定、批量变体与印刷导出。",
        WorkflowKind.ParallelPerspective => "后续接入固定相机、角度约束、生成队列与结构校验。",
        WorkflowKind.Test => "HTML 原型保留在 prototype-art-workbench，仅作为迁移参照。",
        _ => ""
    };
}
