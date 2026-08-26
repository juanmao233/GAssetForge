namespace GAssetForge.App.Services.AgentRuntime;

internal sealed record AgentTurnFailureClassification(
    string Code,
    bool PreservePendingTasks);

internal static class AgentTurnFailureClassifier
{
    internal const string TransportStreamDisconnectedCode =
        "agent_transport_stream_disconnected";

    internal static AgentTurnFailureClassification? Classify(
        AgentTurnResult? turnResult,
        string? explicitFailureMessage)
    {
        if (IsResponsesStreamDisconnected(explicitFailureMessage) ||
            IsResponsesStreamDisconnected(turnResult?.ErrorMessage))
        {
            return new AgentTurnFailureClassification(
                TransportStreamDisconnectedCode,
                PreservePendingTasks: true);
        }

        return null;
    }

    private static bool IsResponsesStreamDisconnected(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        if (message.Contains(
                   "stream disconnected before completion",
                   StringComparison.OrdinalIgnoreCase) ||
            message.Contains(
                   "stream closed before response.completed",
                   StringComparison.OrdinalIgnoreCase) ||
            message.Contains("connection reset by peer", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return message.Contains("response.completed", StringComparison.OrdinalIgnoreCase) &&
               (message.Contains("before", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("missing", StringComparison.OrdinalIgnoreCase)) &&
               (message.Contains("stream", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("connection", StringComparison.OrdinalIgnoreCase));
    }
}
