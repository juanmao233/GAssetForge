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

        if (project.DocumentsById is null ||
            !project.DocumentsById.TryGetValue(project.ActiveDocumentId, out _))
        {
            throw new InvalidDataException("缺少活动文档。");
        }

        foreach (var (documentId, document) in project.DocumentsById)
        {
            ValidateDocument(documentId, document);
        }

        if (project.Library is null ||
            !project.Library.FoldersById.ContainsKey(project.Library.ActiveFolderId))
        {
            throw new InvalidDataException("活动资源文件夹不存在。");
        }

        foreach (var (assetId, asset) in project.Library.AssetsById)
        {
            ValidatePrefab(assetId, asset);
        }

        ValidateEditorSession(project.EditorSession);
    }

    private static void ValidateDocument(string documentId, UiDocumentModel document)
    {
        if (document is null || document.Canvas is null ||
            !IsFinitePositive(document.Canvas.Width) || !IsFinitePositive(document.Canvas.Height))
        {
            throw new InvalidDataException($"画布尺寸无效：{documentId}。");
        }

        ValidateLayerGraph(document.RootIds, document.LayersById, $"文档 {documentId}");
    }

    private static void ValidatePrefab(string assetId, UiPrefabAssetModel asset)
    {
        if (asset?.Payload is null)
        {
            throw new InvalidDataException($"Prefab 负载无效：{assetId}。");
        }

        var bounds = asset.Payload.Bounds;
        if (bounds is null ||
            !double.IsFinite(bounds.X) || !double.IsFinite(bounds.Y) ||
            !IsFinitePositive(bounds.Width) || !IsFinitePositive(bounds.Height))
        {
            throw new InvalidDataException($"Prefab 边界无效：{assetId}。");
        }

        ValidateLayerGraph(asset.Payload.RootIds, asset.Payload.LayersById, $"Prefab {assetId}");
    }

    private static void ValidateLayerGraph(
        IReadOnlyList<string>? rootIds,
        IReadOnlyDictionary<string, UiLayerModel>? layersById,
        string context)
    {
        if (rootIds is null || layersById is null)
        {
            throw new InvalidDataException($"{context} 的图层结构为空。");
        }

        var visited = new HashSet<string>(StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);

        void Visit(string id, string? expectedParent)
        {
            if (visited.Contains(id))
            {
                throw new InvalidDataException($"图层被重复挂载：{id}。");
            }

            if (!layersById.TryGetValue(id, out var layer))
            {
                throw new InvalidDataException($"{context} 的图层不存在：{id}。");
            }

            if (!string.Equals(layer.ParentId, expectedParent, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"图层父级引用无效：{id}。");
            }

            if (!visiting.Add(id))
            {
                throw new InvalidDataException($"图层存在循环引用：{id}。");
            }

            ValidateLayer(id, layer);

            foreach (var childId in layer.ChildIds ?? [])
            {
                Visit(childId, id);
            }

            visiting.Remove(id);
            visited.Add(id);
        }

        foreach (var rootId in rootIds)
        {
            Visit(rootId, null);
        }

        if (visited.Count != layersById.Count)
        {
            throw new InvalidDataException($"{context} 包含未挂载图层。");
        }
    }

    private static void ValidateLayer(string id, UiLayerModel layer)
    {
        if (!double.IsFinite(layer.Opacity) || layer.Opacity is < 0 or > 1)
        {
            throw new InvalidDataException($"图层透明度无效：{id}。");
        }

        if (layer.Transform is { } transform &&
            (!double.IsFinite(transform.X) || !double.IsFinite(transform.Y) ||
             !IsFinitePositive(transform.Width) || !IsFinitePositive(transform.Height) ||
             !double.IsFinite(transform.Rotation)))
        {
            throw new InvalidDataException($"图层尺寸、位置或旋转无效：{id}。");
        }

        if (layer.Style is { } style &&
            (!double.IsFinite(style.Radius) || style.Radius < 0))
        {
            throw new InvalidDataException($"图层样式无效：{id}。");
        }

        var isShapeLayer = string.Equals(layer.Kind, "shape", StringComparison.Ordinal);
        if (isShapeLayer != (layer.Shape is not null))
        {
            throw new InvalidDataException($"图形图层负载无效：{id}。");
        }

        if (!isShapeLayer)
        {
            return;
        }

        if (layer.Transform is null || layer.Style is null || layer.Component is not null || layer.Text is not null)
        {
            throw new InvalidDataException($"图形图层结构无效：{id}。");
        }

        ValidateShape(id, layer.Shape!);
    }

    private static void ValidateShape(string id, UiShapeModel shape)
    {
        if (!UiShapeTypes.IsSupported(shape.Type))
        {
            throw new InvalidDataException($"图形类型无效：{id}。");
        }

        if (!string.Equals(shape.Type, UiShapeTypes.Polygon, StringComparison.Ordinal))
        {
            if (shape.Points is { Count: > 0 })
            {
                throw new InvalidDataException($"非多边形图层不得包含顶点：{id}。");
            }

            return;
        }

        if (shape.Points is not { Count: >= 3 and <= 64 } points)
        {
            throw new InvalidDataException($"多边形顶点数量无效：{id}。");
        }

        foreach (var point in points)
        {
            if (point is null || !double.IsFinite(point.X) || !double.IsFinite(point.Y) ||
                point.X is < 0 or > 1 || point.Y is < 0 or > 1)
            {
                throw new InvalidDataException($"多边形顶点无效：{id}。");
            }
        }

        var twiceArea = 0d;
        for (var index = 0; index < points.Count; index++)
        {
            var current = points[index];
            var next = points[(index + 1) % points.Count];
            twiceArea += current.X * next.Y - next.X * current.Y;
        }

        if (Math.Abs(twiceArea) <= 0.000001)
        {
            throw new InvalidDataException($"多边形面积无效：{id}。");
        }
    }

    private static bool IsFinitePositive(double value) => double.IsFinite(value) && value > 0;

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
