using GAssetForge.App.Infrastructure;
using GAssetForge.Core.Projects;

namespace GAssetForge.App.ViewModels;

public sealed class PageUiConversationMessageViewModel : ObservableObject
{
    private readonly UiAgentConversationMessageModel _model;

    public PageUiConversationMessageViewModel(UiAgentConversationMessageModel model)
    {
        _model = model;
    }

    public string Id => _model.Id;
    public string Role => _model.Role;
    public bool IsUser => string.Equals(Role, "user", StringComparison.OrdinalIgnoreCase);
    public bool IsAgent => string.Equals(Role, "agent", StringComparison.OrdinalIgnoreCase);
    public bool IsSystem => string.Equals(Role, "system", StringComparison.OrdinalIgnoreCase);
    public string RoleLabel => IsUser ? "你" : IsAgent ? "Agent" : "系统";
    public string CreatedAtText => _model.CreatedAt.ToLocalTime().ToString("HH:mm:ss");

    public string Text
    {
        get => _model.Text;
        set
        {
            var normalized = value ?? string.Empty;
            if (string.Equals(_model.Text, normalized, StringComparison.Ordinal))
            {
                return;
            }

            _model.Text = normalized;
            OnPropertyChanged();
        }
    }

    public string State
    {
        get => _model.State;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? "completed" : value.Trim();
            if (string.Equals(_model.State, normalized, StringComparison.Ordinal))
            {
                return;
            }

            _model.State = normalized;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StateText));
            OnPropertyChanged(nameof(IsPending));
            OnPropertyChanged(nameof(IsFailed));
        }
    }

    public bool IsPending => string.Equals(State, "pending", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(State, "streaming", StringComparison.OrdinalIgnoreCase);
    public bool IsFailed => string.Equals(State, "failed", StringComparison.OrdinalIgnoreCase);
    public string StateText => State.ToLowerInvariant() switch
    {
        "pending" => "等待 Agent",
        "streaming" => "Agent 正在回复",
        "failed" => "发送失败",
        _ => string.Empty
    };
}
