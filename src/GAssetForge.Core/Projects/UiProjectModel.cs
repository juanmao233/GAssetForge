using System.Text.Json.Nodes;

namespace GAssetForge.Core.Projects;

public sealed class UiProjectModel
{
    public int SchemaVersion { get; set; } = 2;
    public string Id { get; set; } = "gasset-forge-ui-local";
    public string Name { get; set; } = "GAssetForge UI 项目";
    public string ActiveDocumentId { get; set; } = "document-main";
    public Dictionary<string, UiDocumentModel> DocumentsById { get; set; } = [];
    public UiAssetLibraryModel Library { get; set; } = new();
    public Dictionary<string, JsonNode?> WorkflowGraphs { get; set; } = [];
    public UiEditorSessionModel EditorSession { get; set; } = new();
}

public sealed class UiDocumentModel
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public UiCanvasModel Canvas { get; set; } = new();
    public List<string> RootIds { get; set; } = [];
    public Dictionary<string, UiLayerModel> LayersById { get; set; } = [];
}

public sealed class UiCanvasModel
{
    public double Width { get; set; } = 1440;
    public double Height { get; set; } = 960;
    public string Background { get; set; } = "#11151B";
}

public sealed class UiLayerModel
{
    public string Id { get; set; } = string.Empty;
    public string Kind { get; set; } = "component";
    public string Name { get; set; } = string.Empty;
    public string? ParentId { get; set; }
    public List<string> ChildIds { get; set; } = [];
    public bool Visible { get; set; } = true;
    public bool Locked { get; set; }
    public double Opacity { get; set; } = 1;
    public string BlendMode { get; set; } = "normal";
    public UiTransformModel? Transform { get; set; }
    public UiStyleModel? Style { get; set; }
    public UiComponentModel? Component { get; set; }
    public UiTextModel? Text { get; set; }
    public UiShapeModel? Shape { get; set; }
    public string? PrefabSourceId { get; set; }
}

public sealed class UiTransformModel
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public double Rotation { get; set; }
}

public sealed class UiStyleModel
{
    public double Radius { get; set; }
    public string? Fill { get; set; }
    public string? Border { get; set; }
}

public sealed class UiComponentModel
{
    public string Type { get; set; } = "frame";
    public JsonObject Props { get; set; } = [];
}

public sealed class UiTextModel
{
    public string Value { get; set; } = "新建文字";
    public string FontFamily { get; set; } = "Microsoft YaHei UI";
    public double FontSize { get; set; } = 24;
    public int FontWeight { get; set; } = 600;
    public string Color { get; set; } = "#F3F5F7";
    public string Align { get; set; } = "left";
    public double LineHeight { get; set; } = 1.25;
}

public static class UiShapeTypes
{
    public const string Rectangle = "rectangle";
    public const string Circle = "circle";
    public const string Polygon = "polygon";

    public static bool IsSupported(string? type) =>
        string.Equals(type, Rectangle, StringComparison.Ordinal) ||
        string.Equals(type, Circle, StringComparison.Ordinal) ||
        string.Equals(type, Polygon, StringComparison.Ordinal);
}

public sealed class UiShapeModel
{
    public string Type { get; set; } = UiShapeTypes.Rectangle;
    public List<UiShapePointModel>? Points { get; set; }
}

public sealed class UiShapePointModel
{
    public double X { get; set; }
    public double Y { get; set; }
}

public sealed class UiAssetLibraryModel
{
    public int SeedVersion { get; set; } = 2;
    public string ActiveFolderId { get; set; } = "folder-common-styles";
    public List<string> RootFolderIds { get; set; } = [];
    public Dictionary<string, UiAssetFolderModel> FoldersById { get; set; } = [];
    public Dictionary<string, UiPrefabAssetModel> AssetsById { get; set; } = [];
}

public sealed class UiAssetFolderModel
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? ParentId { get; set; }
    public List<string> ChildFolderIds { get; set; } = [];
    public List<string> AssetIds { get; set; } = [];
}

public sealed class UiPrefabAssetModel
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string AssetType { get; set; } = "prefab";
    public string Scope { get; set; } = "selection";
    public string Tone { get; set; } = "blue";
    public string Icon { get; set; } = "#i-cube";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public UiPrefabPayloadModel Payload { get; set; } = new();
}

public sealed class UiPrefabPayloadModel
{
    public int SchemaVersion { get; set; } = 1;
    public List<string> RootIds { get; set; } = [];
    public Dictionary<string, UiLayerModel> LayersById { get; set; } = [];
    public UiBoundsModel Bounds { get; set; } = new();
    public UiCanvasModel? Canvas { get; set; }
    public string? DocumentName { get; set; }
}

public sealed class UiBoundsModel
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
}

public sealed class UiEditorSessionModel
{
    public string? SelectedLayerId { get; set; } = "layer-page";
    public string? SelectedAssetId { get; set; }
    public string ActiveTool { get; set; } = "move";
    public double Zoom { get; set; } = 0.5;
    public List<string> CollapsedLayerIds { get; set; } = [];
    public List<string> ExpandedProjectFolderIds { get; set; } = [];
    public string AgentInstructionDraft { get; set; } = string.Empty;
    public List<UiAgentConversationMessageModel> AgentConversation { get; set; } = [];
}

public sealed class UiAgentConversationMessageModel
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Role { get; set; } = "user";
    public string Text { get; set; } = string.Empty;
    public string State { get; set; } = "completed";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
}
