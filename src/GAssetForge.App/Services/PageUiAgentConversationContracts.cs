namespace GAssetForge.App.Services;

public sealed record PageUiAgentConversationState(
    bool IsConnected,
    bool IsBusy,
    string AgentDisplayName,
    string StatusText,
    string StatusDetail);

public sealed record PageUiAgentConversationResult(
    bool Succeeded,
    string AgentText,
    string? ErrorMessage);

public interface IPageUiAgentConversationGateway
{
    event EventHandler? StateChanged;

    PageUiAgentConversationState GetState();

    Task<PageUiAgentConversationResult> RunTurnAsync(
        string prompt,
        Action<string> onAgentTextChanged,
        CancellationToken cancellationToken);
}
