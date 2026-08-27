using GAssetForge.Core.Generation;
using System.Security.Cryptography;
using System.Text;

namespace GAssetForge.App.Services;

/// <summary>
/// Provides the stable Codex claimant identity owned by one desktop generation task.
/// A reconnect may reuse only the exact identity derived for that task and must never impersonate
/// another Codex session or Agent family.
/// </summary>
internal static class GenerationManagedAgentIdentity
{
    private const string Prefix = "codex:gassetforge:";

    public static string ForTask(string taskId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        var normalizedTaskId = taskId.Trim();
        var candidate = Prefix + normalizedTaskId;
        try
        {
            return GenerationInputValidator.ValidateAgentId(candidate);
        }
        catch (ArgumentException)
        {
            var digest = Convert.ToHexString(
                    SHA256.HashData(Encoding.UTF8.GetBytes(normalizedTaskId)))
                .ToLowerInvariant();
            return GenerationInputValidator.ValidateAgentId(Prefix + digest);
        }
    }

    public static bool IsManagedClaimantForTask(string taskId, string? agentId)
    {
        if (string.IsNullOrWhiteSpace(taskId) || string.IsNullOrWhiteSpace(agentId))
        {
            return false;
        }

        try
        {
            GenerationInputValidator.ValidateAgentId(agentId);
        }
        catch (ArgumentException)
        {
            return false;
        }

        return string.Equals(
            agentId,
            ForTask(taskId),
            StringComparison.Ordinal);
    }

    public static bool CanResume(GenerationTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        return task.Status switch
        {
            GenerationTaskStatus.Pending => true,
            GenerationTaskStatus.Claimed => IsManagedClaimantForTask(task.Id, task.ClaimedBy),
            _ => false
        };
    }

    public static string ResolveForTask(GenerationTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        return task.Status switch
        {
            GenerationTaskStatus.Pending => ForTask(task.Id),
            GenerationTaskStatus.Claimed when IsManagedClaimantForTask(task.Id, task.ClaimedBy) =>
                task.ClaimedBy!,
            GenerationTaskStatus.Claimed => throw new InvalidOperationException(
                $"任务 {task.Id} 已由非当前 GAssetForge 受管身份 " +
                $"{task.ClaimedBy ?? "unknown"} 认领，不能冒用或抢占。"),
            _ => throw new InvalidOperationException(
                $"任务 {task.Id} 当前状态为 {task.Status}，无需重新派发。")
        };
    }
}
