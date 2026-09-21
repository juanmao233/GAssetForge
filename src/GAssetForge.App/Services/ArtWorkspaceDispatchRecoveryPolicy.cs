using GAssetForge.Core.Generation;
using System.IO;

namespace GAssetForge.App.Services;

/// <summary>
/// Keeps restart recovery from reviving an Art Workspace conversation that has no live waiter,
/// and scopes explicit cleanup to the project's frozen delivery root.
/// </summary>
internal static class ArtWorkspaceDispatchRecoveryPolicy
{
    public static bool CanAutomaticallyRecover(
        IReadOnlyList<GenerationTask> tasks,
        bool hasLiveArtOwner)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        return hasLiveArtOwner || !tasks.Any(IsArtWorkspaceTask);
    }

    public static bool BelongsToProject(GenerationTask task, string projectRoot)
    {
        if (!IsArtWorkspaceTask(task) ||
            string.IsNullOrWhiteSpace(projectRoot) ||
            !Path.IsPathFullyQualified(projectRoot))
        {
            return false;
        }

        try
        {
            return MaterialGenerationConstraintComposer.TryReadDelivery(task.Constraints, out var delivery) &&
                   delivery is not null &&
                   AssetRootService.PathEquals(
                       delivery.AssetRoot,
                       new ProjectPaths(projectRoot).AssetDirectory);
        }
        catch (Exception exception) when (exception is
            ArgumentException or
            InvalidDataException or
            IOException or
            UnauthorizedAccessException or
            NotSupportedException or
            System.Security.SecurityException)
        {
            return false;
        }
    }

    private static bool IsArtWorkspaceTask(GenerationTask? task) =>
        string.Equals(
            task?.Constraints?.WorkflowId,
            ArtWorkspaceGenerationConstraintPackageBuilder.WorkflowId,
            StringComparison.OrdinalIgnoreCase);
}
