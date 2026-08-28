namespace GAssetForge.Core.Projects;

public static class ProjectValidator
{
    public const int MaximumAgentInstructionDraftLength = 4_000;
    public const int MaximumAgentConversationMessages = 100;
    public const int MaximumAgentConversationMessageLength = 16_000;

    public static void Validate(UiProjectModel project)
    {
        if (project.SchemaVersion != 2)
        {
            throw new InvalidDataException($"不支持的项目版本：{project.SchemaVersion}。");
        }

        if (!project.DocumentsById.TryGetValue(project.ActiveDocumentId, out var document))
        {
            throw new InvalidDataException("缺少活动文档。");
        }

        if (document.Canvas.Width <= 0 || document.Canvas.Height <= 0)
        {
            throw new InvalidDataException("画布尺寸无效。");
        }

        var visited = new HashSet<string>(StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);

        void Visit(string id, string? expectedParent)
        {
            if (visited.Contains(id))
            {
                throw new InvalidDataException($"图层被重复挂载：{id}。");
            }

            if (!document.LayersById.TryGetValue(id, out var layer))
            {
                throw new InvalidDataException($"图层不存在：{id}。");
            }

            if (!string.Equals(layer.ParentId, expectedParent, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"图层父级引用无效：{id}。");
            }

            if (!visiting.Add(id))
            {
                throw new InvalidDataException($"图层存在循环引用：{id}。");
            }

            if (layer.Transform is { } transform &&
                (transform.Width <= 0 || transform.Height <= 0 || !double.IsFinite(transform.X) || !double.IsFinite(transform.Y)))
            {
                throw new InvalidDataException($"图层尺寸或位置无效：{id}。");
            }

            foreach (var childId in layer.ChildIds)
            {
                Visit(childId, id);
            }

            visiting.Remove(id);
            visited.Add(id);
        }

        foreach (var rootId in document.RootIds)
        {
            Visit(rootId, null);
        }

        if (visited.Count != document.LayersById.Count)
        {
            throw new InvalidDataException("项目包含未挂载图层。");
        }

        if (!project.Library.FoldersById.ContainsKey(project.Library.ActiveFolderId))
        {
            throw new InvalidDataException("活动资源文件夹不存在。");
        }

        ValidateEditorSession(project.EditorSession);
    }

    private static void ValidateEditorSession(UiEditorSessionModel session)
    {
        ArgumentNullException.ThrowIfNull(session);
        session.AgentInstructionDraft ??= string.Empty;
        session.AgentConversation ??= [];

        if (session.AgentInstructionDraft.Length > MaximumAgentInstructionDraftLength)
        {
            throw new InvalidDataException(
                $"页面 UI Agent 指令草稿超过 {MaximumAgentInstructionDraftLength} 字符限制。");
        }

        if (session.AgentConversation.Count > MaximumAgentConversationMessages)
        {
            throw new InvalidDataException(
                $"页面 UI Agent 对话超过 {MaximumAgentConversationMessages} 条限制。");
        }

        var validRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "user",
            "agent",
            "system"
        };
        var validStates = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "pending",
            "streaming",
            "completed",
            "failed"
        };

        foreach (var message in session.AgentConversation)
        {
            if (message is null || string.IsNullOrWhiteSpace(message.Id))
            {
                throw new InvalidDataException("页面 UI Agent 对话包含无效消息标识。");
            }

            message.Text ??= string.Empty;
            message.Role ??= "system";
            message.State ??= "completed";
            if (message.Text.Length > MaximumAgentConversationMessageLength)
            {
                throw new InvalidDataException(
                    $"页面 UI Agent 单条消息超过 {MaximumAgentConversationMessageLength} 字符限制。");
            }

            if (!validRoles.Contains(message.Role))
            {
                throw new InvalidDataException($"页面 UI Agent 消息角色无效：{message.Role}。");
            }

            if (!validStates.Contains(message.State))
            {
                throw new InvalidDataException($"页面 UI Agent 消息状态无效：{message.State}。");
            }
        }
    }
}
