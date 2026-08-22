using System.Text.Json.Nodes;

namespace GAssetForge.Core.Projects;

public static class DefaultUiProjectFactory
{
    public static UiProjectModel Create()
    {
        var now = DateTimeOffset.Now;
        var document = new UiDocumentModel
        {
            Id = "document-main",
            Name = "主界面 · 战斗准备",
            CreatedAt = now,
            UpdatedAt = now,
            Canvas = new UiCanvasModel { Width = 1440, Height = 960, Background = "#11151B" },
            RootIds = ["group-actions", "group-content", "group-navigation", "layer-page"]
        };

        document.LayersById = new Dictionary<string, UiLayerModel>(StringComparer.Ordinal)
        {
            ["group-actions"] = Group("group-actions", "操作与状态", ["layer-primary", "layer-secondary", "layer-status"]),
            ["group-content"] = Group("group-content", "主要内容", ["layer-title", "layer-card"]),
            ["group-navigation"] = Group("group-navigation", "导航", ["layer-topbar", "layer-bottomnav"]),
            ["layer-page"] = Component("layer-page", "页面容器", null, "page", 0, 0, 1440, 960, 0,
                Props(("title", "战斗准备"), ("subtitle", "第 3 章 · 王城外围")), locked: true),
            ["layer-topbar"] = Component("layer-topbar", "顶部导航", "group-navigation", "topbar", 0, 0, 1440, 112, 0,
                Props(("left", "返回"), ("title", "编队配置"), ("right", "24,800"))),
            ["layer-title"] = Component("layer-title", "主标题", "group-content", "title", 88, 168, 568, 88, 0,
                Props(("title", "出击准备"), ("subtitle", "选择本次战斗的编队与增益"))),
            ["layer-card"] = Component("layer-card", "当前编队卡片", "group-content", "card", 88, 288, 640, 432, 16,
                Props(("label", "当前编队"), ("rows", new JsonArray("先锋 · 剑士 / Lv.24", "远程 · 游侠 / Lv.21", "辅助 · 祭司 / Lv.19")))),
            ["layer-primary"] = Component("layer-primary", "主按钮", "group-actions", "button", 808, 288, 512, 104, 12,
                Props(("label", "开始战斗"), ("variant", "primary"))),
            ["layer-secondary"] = Component("layer-secondary", "次按钮", "group-actions", "button", 808, 416, 512, 104, 12,
                Props(("label", "调整阵容"), ("variant", "secondary"))),
            ["layer-status"] = Component("layer-status", "奖励提示", "group-actions", "status", 808, 584, 512, 136, 14,
                Props(("eyebrow", "今日奖励"), ("title", "体力 × 120"), ("subtitle", "登录后领取"))),
            ["layer-bottomnav"] = Component("layer-bottomnav", "底部导航", "group-navigation", "bottomnav", 0, 848, 1440, 112, 0,
                Props(("items", "主页 · 角色 · 背包 · 商店"), ("activeIndex", 0)))
        };

        var project = new UiProjectModel
        {
            ActiveDocumentId = document.Id,
            DocumentsById = new Dictionary<string, UiDocumentModel>(StringComparer.Ordinal) { [document.Id] = document },
            EditorSession = new UiEditorSessionModel
            {
                SelectedLayerId = "layer-page",
                ActiveTool = "move",
                Zoom = 0.5,
                ExpandedProjectFolderIds = ["folder-common-styles", "folder-pages", "folder-components"]
            }
        };

        project.Library = CreateLibrary(now);
        ProjectValidator.Validate(project);
        return project;
    }

    private static UiAssetLibraryModel CreateLibrary(DateTimeOffset now)
    {
        var assets = new[]
        {
            Prefab(now, "prefab-basic-button", "基础按钮", "blue", "button", "按钮 · 确认", 320, 88, 12,
                Props(("label", "确认"), ("variant", "primary"), ("disabled", false))),
            Prefab(now, "prefab-text-input", "文本输入框", "violet", "input", "输入框 · 单行", 480, 88, 12,
                Props(("label", "账号"), ("value", ""), ("placeholder", "请输入内容"), ("state", "default"))),
            Prefab(now, "prefab-background-panel", "背景框", "gold", "panel", "背景框 · 基础", 640, 360, 20,
                Props(("title", "面板标题"), ("subtitle", "可用于承载一组界面内容"), ("padding", 32), ("surfaceVariant", "base"))),
            Prefab(now, "prefab-slider", "Slider 滑块", "green", "slider", "Slider · 音量", 480, 96, 12,
                Props(("label", "音量"), ("min", 0), ("max", 100), ("step", 1), ("value", 60))),
            Prefab(now, "prefab-toggle", "Toggle 勾选框", "blue", "toggle", "Toggle · 启用音效", 320, 64, 10,
                Props(("label", "启用音效"), ("checked", true), ("disabled", false)))
        };

        var library = new UiAssetLibraryModel
        {
            ActiveFolderId = "folder-common-styles",
            RootFolderIds = ["folder-common-styles", "folder-pages", "folder-components"],
            FoldersById = new Dictionary<string, UiAssetFolderModel>(StringComparer.Ordinal)
            {
                ["folder-common-styles"] = new()
                {
                    Id = "folder-common-styles",
                    Name = "常用样式",
                    AssetIds = assets.Select(asset => asset.Id).ToList()
                },
                ["folder-pages"] = new() { Id = "folder-pages", Name = "Pages" },
                ["folder-components"] = new() { Id = "folder-components", Name = "UI Components" }
            },
            AssetsById = assets.ToDictionary(asset => asset.Id, StringComparer.Ordinal)
        };

        return library;
    }

    private static UiLayerModel Group(string id, string name, List<string> children) => new()
    {
        Id = id,
        Kind = "group",
        Name = name,
        ChildIds = children
    };

    private static UiLayerModel Component(
        string id,
        string name,
        string? parentId,
        string componentType,
        double x,
        double y,
        double width,
        double height,
        double radius,
        JsonObject props,
        bool locked = false) => new()
    {
        Id = id,
        Kind = "component",
        Name = name,
        ParentId = parentId,
        Locked = locked,
        Transform = new UiTransformModel { X = x, Y = y, Width = width, Height = height },
        Style = new UiStyleModel { Radius = radius },
        Component = new UiComponentModel { Type = componentType, Props = props }
    };

    private static UiPrefabAssetModel Prefab(
        DateTimeOffset now,
        string id,
        string name,
        string tone,
        string componentType,
        string layerName,
        double width,
        double height,
        double radius,
        JsonObject props)
    {
        var layerId = $"{id}-layer";
        var layer = Component(layerId, layerName, null, componentType, 0, 0, width, height, radius, props);
        return new UiPrefabAssetModel
        {
            Id = id,
            Name = name,
            Tone = tone,
            CreatedAt = now,
            UpdatedAt = now,
            Payload = new UiPrefabPayloadModel
            {
                RootIds = [layerId],
                LayersById = new Dictionary<string, UiLayerModel>(StringComparer.Ordinal) { [layerId] = layer },
                Bounds = new UiBoundsModel { Width = width, Height = height }
            }
        };
    }

    private static JsonObject Props(params (string Key, object? Value)[] values)
    {
        var result = new JsonObject();
        foreach (var (key, value) in values)
        {
            result[key] = value switch
            {
                null => null,
                JsonNode node => node,
                string text => JsonValue.Create(text),
                bool flag => JsonValue.Create(flag),
                int number => JsonValue.Create(number),
                double number => JsonValue.Create(number),
                _ => JsonValue.Create(value.ToString())
            };
        }

        return result;
    }
}
