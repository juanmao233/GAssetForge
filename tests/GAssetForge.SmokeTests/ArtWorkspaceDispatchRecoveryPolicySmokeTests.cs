using GAssetForge.App.Services;
using GAssetForge.Core.Generation;
using System.IO;
using System.Text.Json.Nodes;

namespace GAssetForge.SmokeTests;

internal static class ArtWorkspaceDispatchRecoveryPolicySmokeTests
{
    public static void RunAll()
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), "GAssetForge.ArtRecoveryPolicy", "ProjectA");
        var otherProjectRoot = Path.Combine(Path.GetTempPath(), "GAssetForge.ArtRecoveryPolicy", "ProjectB");
        var artTask = CreateTask(ArtWorkspaceGenerationConstraintPackageBuilder.WorkflowId, projectRoot);
        var iconTask = CreateTask(IconDesignConstraintPackageBuilder.WorkflowId, projectRoot);
        False(ArtWorkspaceDispatchRecoveryPolicy.CanAutomaticallyRecover([artTask], hasLiveArtOwner: false),
            "没有实时 owner 的旧美术批次不能在重启握手后自动执行");
        True(ArtWorkspaceDispatchRecoveryPolicy.CanAutomaticallyRecover([artTask], hasLiveArtOwner: true),
            "同一会话仍有 owner 的美术批次必须保留自动恢复能力");
        True(ArtWorkspaceDispatchRecoveryPolicy.CanAutomaticallyRecover([iconTask], hasLiveArtOwner: false),
            "其它工作流的自动恢复不得因美术对话清理策略改变");
        False(ArtWorkspaceDispatchRecoveryPolicy.CanAutomaticallyRecover([iconTask, artTask], hasLiveArtOwner: false),
            "混合批次只要包含失去 owner 的美术任务就不得自动恢复");

        True(ArtWorkspaceDispatchRecoveryPolicy.BelongsToProject(artTask, projectRoot),
            "有效美术交付合同应准确匹配当前项目 Assets");
        True(ArtWorkspaceDispatchRecoveryPolicy.BelongsToProject(artTask, projectRoot + Path.DirectorySeparatorChar),
            "同一项目路径末尾的分隔符不应破坏规范化匹配");
        False(ArtWorkspaceDispatchRecoveryPolicy.BelongsToProject(artTask, otherProjectRoot),
            "清除当前项目不得命中其它项目的美术任务");
        False(ArtWorkspaceDispatchRecoveryPolicy.BelongsToProject(iconTask, projectRoot),
            "相同 Assets 根目录下其它工作流不得被美术清理命中");
        False(ArtWorkspaceDispatchRecoveryPolicy.BelongsToProject(artTask, "relative-project"),
            "未知相对项目路径必须拒绝匹配");
        False(ArtWorkspaceDispatchRecoveryPolicy.BelongsToProject(artTask, string.Empty),
            "未打开项目时不得清除持久任务");

        var missingDelivery = artTask with { Constraints = artTask.Constraints with { Parameters = [] } };
        False(ArtWorkspaceDispatchRecoveryPolicy.BelongsToProject(missingDelivery, projectRoot),
            "缺少 delivery 的旧任务无法证明项目归属时必须保留");
        var brokenParameters = artTask.Constraints.Parameters.DeepClone().AsObject();
        brokenParameters["delivery"] = "not-a-delivery-object";
        var brokenDelivery = artTask with
        {
            Constraints = artTask.Constraints with { Parameters = brokenParameters }
        };
        False(ArtWorkspaceDispatchRecoveryPolicy.BelongsToProject(brokenDelivery, projectRoot),
            "损坏 delivery 不能用于清除任务");
        var malformedParameters = artTask.Constraints.Parameters.DeepClone().AsObject();
        malformedParameters["delivery"]!["assetRoot"] = "Assets";
        False(ArtWorkspaceDispatchRecoveryPolicy.BelongsToProject(
                artTask with { Constraints = artTask.Constraints with { Parameters = malformedParameters } },
                projectRoot),
            "非绝对冻结 AssetRoot 必须拒绝匹配");
        var unknownSchema = artTask.Constraints.Parameters.DeepClone().AsObject();
        unknownSchema["delivery"]!["schemaVersion"] = 999;
        False(ArtWorkspaceDispatchRecoveryPolicy.BelongsToProject(
                artTask with { Constraints = artTask.Constraints with { Parameters = unknownSchema } },
                projectRoot),
            "未知 delivery schema 必须保留任务而非猜测项目归属");
        var nestedAssetRoot = artTask.Constraints.Parameters.DeepClone().AsObject();
        nestedAssetRoot["delivery"]!["assetRoot"] = Path.Combine(projectRoot, "Assets", "Nested");
        False(ArtWorkspaceDispatchRecoveryPolicy.BelongsToProject(
                artTask with { Constraints = artTask.Constraints with { Parameters = nestedAssetRoot } },
                projectRoot),
            "仅为当前 Assets 子目录的交付根不得视为根目录精确相同");
    }

    private static GenerationTask CreateTask(string workflowId, string projectRoot) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Constraints = new GenerationConstraintPackage
        {
            WorkflowId = workflowId,
            Parameters = new JsonObject
            {
                ["delivery"] = new JsonObject
                {
                    ["schemaVersion"] = 1,
                    ["assetRoot"] = Path.Combine(projectRoot, "Assets"),
                    ["outputDirectory"] = "Final/ArtWorkspace/fixture",
                    ["suggestedBaseName"] = "fixture",
                    ["targetPaths"] = new JsonArray("Final/ArtWorkspace/fixture/output.png")
                }
            }
        }
    };

    private static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void False(bool condition, string message) => True(!condition, message);
}
