using GAssetForge.App.Services;
using GAssetForge.App.ViewModels;
using GAssetForge.App.Views;
using GAssetForge.Core.Projects;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

namespace GAssetForge.SmokeTests;

internal static class PageUiShapeToolSmokeTests
{
    public static void Run(string temporaryRoot)
    {
        RectangleCreationUsesOneUndoTransaction(temporaryRoot);
        ShapeKindsAndGeometryRoundTrip(temporaryRoot);
        ShapeValidationRejectsInvalidGeometry();
        ToolPaletteExposesAllShapeKinds(temporaryRoot);
    }

    private static void RectangleCreationUsesOneUndoTransaction(string temporaryRoot)
    {
        var editor = CreateEditor(temporaryRoot, "page-ui-shape-undo");
        try
        {
            var initialLayerCount = editor.ActiveDocument.LayersById.Count;
            editor.SelectToolCommand.Execute(UiShapeTypes.Rectangle);
            Equal(EditorTool.Rectangle, editor.ActiveTool, "矩形工具选择");

            editor.CreateShapeFromDrag(UiShapeTypes.Rectangle, 420, 360, 120, 160);
            Equal(initialLayerCount + 1, editor.ActiveDocument.LayersById.Count, "矩形拖拽仅创建一个图层");
            var created = editor.SelectedLayer
                ?? throw new InvalidOperationException("矩形创建后未选中新图层。");
            var createdId = created.Id;
            Equal(UiShapeTypes.Rectangle, created.ShapeType, "矩形图层类型");
            Equal(120d, created.X, "反向拖拽归一化后的 X");
            Equal(160d, created.Y, "反向拖拽归一化后的 Y");
            Equal(300d, created.Width, "反向拖拽归一化后的宽度");
            Equal(200d, created.Height, "反向拖拽归一化后的高度");
            Equal(createdId, editor.ActiveDocument.RootIds[0], "新图形位于顶层最前方");
            True(editor.IsDirty, "创建图形后项目应标记为未保存");

            True(editor.UndoCommand.CanExecute(null), "创建图形后应可撤销");
            editor.UndoCommand.Execute(null);
            False(editor.ActiveDocument.LayersById.ContainsKey(createdId), "一次撤销应移除整个图形");
            False(editor.UndoCommand.CanExecute(null), "一次图形创建不得污染出第二条历史");

            True(editor.RedoCommand.CanExecute(null), "撤销图形后应可重做");
            editor.RedoCommand.Execute(null);
            True(editor.ActiveDocument.LayersById.TryGetValue(createdId, out var restored), "重做应恢复同一图形 ID");
            Equal(UiShapeTypes.Rectangle, restored?.Shape?.Type, "重做后的图形类型");
            Equal(300d, restored?.Transform?.Width ?? double.NaN, "重做后的图形宽度");
        }
        finally
        {
            editor.Shutdown();
        }
    }

    private static void ShapeKindsAndGeometryRoundTrip(string temporaryRoot)
    {
        var editor = CreateEditor(temporaryRoot, "page-ui-shape-roundtrip");
        string rectangleId;
        string circleId;
        string polygonId;
        UiShapePointModel[] polygonPoints;
        var projectPath = editor.ProjectFilePath;

        try
        {
            editor.SelectToolCommand.Execute(UiShapeTypes.Rectangle);
            editor.CreateDefaultShapeAt(UiShapeTypes.Rectangle, editor.CanvasWidth - 10, editor.CanvasHeight - 10);
            rectangleId = editor.SelectedLayer?.Id
                ?? throw new InvalidOperationException("默认矩形创建失败。");
            Equal(160d, editor.SelectedLayer.Width, "默认矩形宽度");
            Equal(100d, editor.SelectedLayer.Height, "默认矩形高度");
            True(editor.SelectedLayer.X + editor.SelectedLayer.Width <= editor.CanvasWidth,
                "默认矩形不得越过画布右边界");
            True(editor.SelectedLayer.Y + editor.SelectedLayer.Height <= editor.CanvasHeight,
                "默认矩形不得越过画布下边界");

            editor.SelectToolCommand.Execute(UiShapeTypes.Circle);
            editor.CreateShapeFromDrag(UiShapeTypes.Circle, 500, 500, 420, 460);
            circleId = editor.SelectedLayer?.Id
                ?? throw new InvalidOperationException("圆形创建失败。");
            Equal(UiShapeTypes.Circle, editor.SelectedLayer.ShapeType, "圆形图层类型");
            Equal(80d, editor.SelectedLayer.Width, "圆形拖拽边长");
            Equal(editor.SelectedLayer.Width, editor.SelectedLayer.Height, "圆形必须保持等宽高");

            editor.SelectToolCommand.Execute(UiShapeTypes.Polygon);
            editor.CreateShapeFromDrag(UiShapeTypes.Polygon, 100, 100, 280, 240);
            polygonId = editor.SelectedLayer?.Id
                ?? throw new InvalidOperationException("多边形创建失败。");
            var polygon = editor.SelectedLayer.Model.Shape
                ?? throw new InvalidOperationException("多边形缺少 Shape 负载。");
            var points = polygon.Points
                ?? throw new InvalidOperationException("多边形缺少顶点。");
            Equal(UiShapeTypes.Polygon, polygon.Type, "多边形图层类型");
            Equal(6, points.Count, "首版规则多边形应为六边形");
            True(points.All(point =>
                    double.IsFinite(point.X) && double.IsFinite(point.Y) &&
                    point.X is >= 0 and <= 1 && point.Y is >= 0 and <= 1),
                "多边形应使用有限的归一化本地点");
            polygonPoints = points.Select(point => new UiShapePointModel { X = point.X, Y = point.Y }).ToArray();

            Equal(polygonId, editor.ActiveDocument.RootIds[0], "最后创建的多边形应位于顶层最前方");
            Equal(circleId, editor.ActiveDocument.RootIds[1], "圆形应位于多边形之后");
            Equal(rectangleId, editor.ActiveDocument.RootIds[2], "矩形应位于圆形之后");
            ProjectValidator.Validate(editor.Project);
            editor.SaveImmediately();
        }
        finally
        {
            editor.Shutdown();
        }

        True(File.Exists(projectPath), "保存形状后项目文件应存在");
        var reloaded = CreateEditor(temporaryRoot, "page-ui-shape-roundtrip");
        try
        {
            Equal(EditorTool.Polygon, reloaded.ActiveTool, "图形工具会话应随项目重载");
            Equal(polygonId, reloaded.SelectedLayer?.Id, "重载后应保留最后选中的图形");
            Equal(UiShapeTypes.Rectangle,
                reloaded.ActiveDocument.LayersById[rectangleId].Shape?.Type,
                "重载后的矩形类型");
            var reloadedCircle = reloaded.ActiveDocument.LayersById[circleId];
            Equal(UiShapeTypes.Circle, reloadedCircle.Shape?.Type, "重载后的圆形类型");
            Equal(reloadedCircle.Transform?.Width, reloadedCircle.Transform?.Height, "重载后的圆形仍等宽高");
            var reloadedPoints = reloaded.ActiveDocument.LayersById[polygonId].Shape?.Points
                ?? throw new InvalidOperationException("重载后的多边形顶点缺失。");
            Equal(polygonPoints.Length, reloadedPoints.Count, "重载后的多边形顶点数");
            for (var index = 0; index < polygonPoints.Length; index++)
            {
                Equal(polygonPoints[index].X, reloadedPoints[index].X, $"多边形顶点 {index} X 顺序");
                Equal(polygonPoints[index].Y, reloadedPoints[index].Y, $"多边形顶点 {index} Y 顺序");
            }

            ProjectValidator.Validate(reloaded.Project);
        }
        finally
        {
            reloaded.Shutdown();
        }
    }

    private static void ShapeValidationRejectsInvalidGeometry()
    {
        var valid = DefaultUiProjectFactory.Create();
        AddPolygon(valid, "shape-valid", CreateTriangle());
        ProjectValidator.Validate(valid);

        var tooFewPoints = ProjectJson.Clone(valid);
        tooFewPoints.DocumentsById[tooFewPoints.ActiveDocumentId]
            .LayersById["shape-valid"].Shape!.Points = CreateTriangle().Take(2).ToList();
        ThrowsInvalidData(() => ProjectValidator.Validate(tooFewPoints), "少于三个顶点的多边形必须被拒绝");

        var nonFiniteTransform = ProjectJson.Clone(valid);
        nonFiniteTransform.DocumentsById[nonFiniteTransform.ActiveDocumentId]
            .LayersById["shape-valid"].Transform!.Rotation = double.NaN;
        ThrowsInvalidData(() => ProjectValidator.Validate(nonFiniteTransform), "非有限旋转必须被拒绝");

        var hiddenInvalidPrefab = ProjectJson.Clone(valid);
        var prefab = hiddenInvalidPrefab.Library.AssetsById.Values.First();
        var prefabRoot = prefab.Payload.LayersById[prefab.Payload.RootIds[0]];
        prefabRoot.Kind = "shape";
        prefabRoot.Component = null;
        prefabRoot.Text = null;
        prefabRoot.Style ??= new UiStyleModel();
        prefabRoot.Shape = new UiShapeModel
        {
            Type = UiShapeTypes.Polygon,
            Points = [new() { X = 0, Y = 0 }, new() { X = 1, Y = 1 }]
        };
        ThrowsInvalidData(() => ProjectValidator.Validate(hiddenInvalidPrefab),
            "Prefab 内的非法多边形不得绕过项目校验");
    }

    private static void ToolPaletteExposesAllShapeKinds(string temporaryRoot)
    {
        var editor = CreateEditor(temporaryRoot, "page-ui-shape-tools");
        var palette = new ToolPaletteView { DataContext = editor };
        var host = new Window
        {
            Width = 180,
            Height = 600,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None,
            Content = palette
        };

        try
        {
            host.Show();
            host.UpdateLayout();
            PumpDispatcher();

            AssertShapeTool(palette, editor, "RectangleToolButton", UiShapeTypes.Rectangle, EditorTool.Rectangle);
            AssertShapeTool(palette, editor, "CircleToolButton", UiShapeTypes.Circle, EditorTool.Circle);
            AssertShapeTool(palette, editor, "PolygonToolButton", UiShapeTypes.Polygon, EditorTool.Polygon);

            var canvas = new CanvasDocumentView { DataContext = editor };
            True(canvas.Resources["RectangleShapeTemplate"] is DataTemplate, "画布应提供矩形矢量模板");
            True(canvas.Resources["CircleShapeTemplate"] is DataTemplate, "画布应提供圆形矢量模板");
            True(canvas.Resources["PolygonShapeTemplate"] is DataTemplate, "画布应提供多边形矢量模板");
            True(canvas.FindName("ShapeCreationOverlay") is Canvas, "画布应提供无命中的图形预览层");
        }
        finally
        {
            host.Close();
            editor.Shutdown();
        }
    }

    private static void AssertShapeTool(
        ToolPaletteView palette,
        EditorWorkspaceViewModel editor,
        string elementName,
        string commandParameter,
        EditorTool expectedTool)
    {
        var button = palette.FindName(elementName) as ToggleButton
            ?? throw new InvalidOperationException($"工具栏缺少 {elementName}。");
        True(ReferenceEquals(editor.SelectToolCommand, button.Command), $"{elementName} Command 绑定");
        Equal(commandParameter, button.CommandParameter?.ToString(), $"{elementName} CommandParameter");
        button.Command.Execute(button.CommandParameter);
        PumpDispatcher();
        Equal(expectedTool, editor.ActiveTool, $"{elementName} 激活工具");
        True(button.IsChecked == true, $"{elementName} 激活状态");
    }

    private static EditorWorkspaceViewModel CreateEditor(string temporaryRoot, string directoryName)
    {
        var paths = new WorkspacePaths(Path.Combine(temporaryRoot, directoryName));
        return new EditorWorkspaceViewModel(
            new JsonProjectRepository(paths),
            new DesktopFileDialogService(),
            new ProjectHistoryService());
    }

    private static void AddPolygon(UiProjectModel project, string id, List<UiShapePointModel> points)
    {
        var document = project.DocumentsById[project.ActiveDocumentId];
        document.LayersById[id] = new UiLayerModel
        {
            Id = id,
            Kind = "shape",
            Name = "验证多边形",
            Transform = new UiTransformModel { X = 10, Y = 10, Width = 100, Height = 100 },
            Style = new UiStyleModel { Fill = "#367FD8", Border = "#83B9F7" },
            Shape = new UiShapeModel { Type = UiShapeTypes.Polygon, Points = points }
        };
        document.RootIds.Insert(0, id);
    }

    private static List<UiShapePointModel> CreateTriangle() =>
    [
        new() { X = 0.5, Y = 0 },
        new() { X = 1, Y = 1 },
        new() { X = 0, Y = 1 }
    ];

    private static void PumpDispatcher() =>
        Dispatcher.CurrentDispatcher.Invoke(
            DispatcherPriority.Background,
            new Action(() => { }));

    private static void ThrowsInvalidData(Action action, string message)
    {
        try
        {
            action();
        }
        catch (InvalidDataException)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }

    private static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void False(bool condition, string message) => True(!condition, message);

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message}：期望 {expected}，实际 {actual}。");
        }
    }
}
