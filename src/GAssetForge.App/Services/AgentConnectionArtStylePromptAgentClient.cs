using GAssetForge.App.ViewModels;

namespace GAssetForge.App.Services;

public sealed class AgentConnectionArtStylePromptAgentClient(AgentConnectionViewModel connection)
    : IArtStylePromptAgentClient
{
    private readonly AgentConnectionViewModel _connection =
        connection ?? throw new ArgumentNullException(nameof(connection));

    public bool IsConnected => _connection.IsConnected;

    public string DisplayName => _connection.SelectedAgent.DisplayName;

    public string WorkingDirectory => _connection.WorkingDirectory;

    public async Task<ArtStylePromptAgentTurnResult> RunTurnAsync(
        string prompt,
        CancellationToken cancellationToken = default)
    {
        var result = await _connection.RunTurnAsync(prompt, cancellationToken).ConfigureAwait(false);
        return ArtStylePromptAgentTurnResult.FromAgentTurn(result);
    }
}
