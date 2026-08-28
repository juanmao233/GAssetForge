using GAssetForge.App.Services;
using GAssetForge.App.ViewModels;
using GAssetForge.App.Views;
using GAssetForge.Core.ArtWorkspaces;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace GAssetForge.SmokeTests;

internal static class ArtWorkspaceInstructionReferenceUiSmokeTests
{
    public static void Run(string temporaryRoot)
    {
        RotatedImageRegionUsesStableLocalAndSourcePixelCoordinates();
        ToolAndPropertyPanelsExposeSafeInstructionReferenceContext(temporaryRoot);
    }

    private static void RotatedImageRegionUsesStableLocalAndSourcePixelCoordinates()
    {
        var imageId = Guid.NewGuid().ToString("N");
        var region = ArtWorkspaceImageRegionGeometry.FromWorldPoints(
            imageId,
            imageX: 100d,
            imageY: 200d,
            imageWidth: 200d,
            imageHeight: 100d,
            rotationDegrees: 90d,
            startWorldX: 230d,
            startWorldY: 200d,
            endWorldX: 170d,
            endWorldY: 300d);

        Near(0.25d, region.X, "90° 旋转图片框选的本地 X");
        Near(0.2d, region.Y, "90° 旋转图片框选的本地 Y");
        Near(0.5d, region.Width, "90° 旋转图片框选的本地宽度");
        Near(0.6d, region.Height, "90° 旋转图片框选的本地高度");

        var reverse = ArtWorkspaceImageRegionGeometry.FromWorldPoints(
            imageId,
            imageX: 100d,
            imageY: 200d,
            imageWidth: 200d,
            imageHeight: 100d,
            rotationDegrees: 90d,
            startWorldX: 170d,
            startWorldY: 300d,
            endWorldX: 230d,
            endWorldY: 200d);
        Near(region.X, reverse.X, "反向拖拽必须得到相同本地 X");
        Near(region.Y, reverse.Y, "反向拖拽必须得到相同本地 Y");
        Near(region.Width, reverse.Width, "反向拖拽必须得到相同宽度");
        Near(region.Height, reverse.Height, "反向拖拽必须得到相同高度");

        var pixels = ArtWorkspaceImageRegionGeometry.GetSourcePixelBounds(400, 200, region);
        Equal(100, pixels.X, "源像素框 X");
        Equal(40, pixels.Y, "源像素框 Y");
        Equal(200, pixels.Width, "源像素框宽度");
        Equal(120, pixels.Height, "源像素框高度");

        var genuinelyBelowBoundary = new ArtWorkspaceImageRegionSelection
        {
            ImageId = imageId,
            X = 0.199995d,
            Y = 0.1d,
            Width = 0.2d,
            Height = 0.2d
        };
        var conservativePixels = ArtWorkspaceImageRegionGeometry.GetSourcePixelBounds(
            200,
            100,
            genuinelyBelowBoundary);
        Equal(39, conservativePixels.X,
            "明显低于整数像素边界的坐标仍必须向外 Floor，不能被容差错误吸附");

        var corners = ArtWorkspaceImageRegionGeometry.GetWorldCorners(
            imageId,
            imageX: 100d,
            imageY: 200d,
            imageWidth: 200d,
            imageHeight: 100d,
            rotationDegrees: 90d,
            region);
        Equal(4, corners.Count, "冻结局部区域应有四个画布角点");
        Near(230d, corners[0].X, "旋转区域左上角画布 X");
        Near(200d, corners[0].Y, "旋转区域左上角画布 Y");
        Near(170d, corners[2].X, "旋转区域右下角画布 X");
        Near(300d, corners[2].Y, "旋转区域右下角画布 Y");

        var clamped = ArtWorkspaceImageRegionGeometry.FromWorldPoints(
            imageId,
            imageX: 0d,
            imageY: 0d,
            imageWidth: 100d,
            imageHeight: 100d,
            rotationDegrees: 0d,
            startWorldX: -30d,
            startWorldY: -20d,
            endWorldX: 140d,
            endWorldY: 130d);
        Near(0d, clamped.X, "越界拖拽应夹紧到图片左边界");
        Near(0d, clamped.Y, "越界拖拽应夹紧到图片上边界");
        Near(1d, clamped.Width, "越界拖拽应夹紧到完整图片宽度");
        Near(1d, clamped.Height, "越界拖拽应夹紧到完整图片高度");
    }

    private static void ToolAndPropertyPanelsExposeSafeInstructionReferenceContext(string temporaryRoot)
    {
        var root = Path.Combine(
            temporaryRoot,
            "art-workspace-instruction-reference-ui",
            Guid.NewGuid().ToString("N"));
        var fixtureDirectory = Path.Combine(root, "fixtures");
        var projectParent = Path.Combine(root, "projects");
        Directory.CreateDirectory(fixtureDirectory);
        Directory.CreateDirectory(projectParent);
        var fixturePath = Path.Combine(fixtureDirectory, "private-reference.png");
        SavePngFixture(fixturePath, 8, 4);

        var paths = new WorkspacePaths(Path.Combine(root, "desktop-data"));
        var project = new ProjectWorkspaceService().Create(
            projectParent,
            "InstructionReferenceUi",
            "ui");
        using var console = new WorkbenchConsoleService(paths);
        using var viewModel = new ArtWorkspaceViewModel(
            new JsonArtWorkspaceRepository(paths),
            new EmptyImagePicker(),
            console);

        True(viewModel.Activate(project.Paths.RootDirectory),
            "引用 UI Smoke 应激活正式项目");
        False(viewModel.ImageRegionSelectToolCommand.CanExecute(null),
            "尚未选择图片时图像框选工具必须禁用");
        Equal(1, viewModel.AddImagesAt([fixturePath], 120d, 180d),
            "引用 UI Smoke 应导入一张受管图片");
        var image = viewModel.Images.Single();
        True(viewModel.ImageRegionSelectToolCommand.CanExecute(null),
            "可见且文件存在的当前图片应允许框选");
        True(viewModel.SetSelectedImageRegionNormalized(
                0.25d,
                0.25d,
                0.5d,
                0.5d,
                commit: true),
            "引用 UI Smoke 应建立局部框选");

        var safeSummary = $"引用图片“{image.Name}” · 局部区域";
        var conversationMessage = new ArtWorkspaceConversationMessageViewModel(
            new ArtWorkspaceConversationMessage
            {
                Role = ArtWorkspaceMessageRole.User,
                Text = "只调整这里的明暗关系。",
                CreatedAtUtc = DateTimeOffset.UtcNow,
                State = "captured",
                ReferenceImageId = image.Id,
                ReferenceSummary = safeSummary
            });
        viewModel.Conversation.Add(conversationMessage);

        var propertiesView = new ArtWorkspacePropertiesView { DataContext = viewModel };
        var toolPalette = new ArtWorkspaceToolPaletteView { DataContext = viewModel };
        var host = new Grid();
        host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(420d) });
        host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140d) });
        Grid.SetColumn(propertiesView, 0);
        Grid.SetColumn(toolPalette, 1);
        host.Children.Add(propertiesView);
        host.Children.Add(toolPalette);
        var window = new Window
        {
            Width = 620d,
            Height = 900d,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.ToolWindow,
            Content = host
        };

        try
        {
            window.Show();
            window.UpdateLayout();
            PumpDispatcher();

            var referenceText = propertiesView.FindName("ArtWorkspaceInstructionReferenceText") as TextBlock
                ?? throw new InvalidOperationException(
                    "美术工作区属性页缺少稳定的当前指令图片引用文本控件。");
            Equal(
                nameof(ArtWorkspaceViewModel.SelectedImageReferenceText),
                GetBindingPath(referenceText, TextBlock.TextProperty),
                "当前指令图片引用文本绑定");
            True(referenceText.Text.Contains(image.Name, StringComparison.Ordinal) &&
                 referenceText.Text.Contains("局部区域", StringComparison.Ordinal),
                "当前指令引用 chip 应显示图片名称与局部语义");
            False(referenceText.Text.Contains(image.AbsolutePath, StringComparison.OrdinalIgnoreCase),
                "当前指令引用 chip 不得泄露受管图片绝对路径");
            False(referenceText.Text.Contains("0.25", StringComparison.Ordinal) ||
                  referenceText.Text.Contains("120", StringComparison.Ordinal) ||
                  referenceText.Text.Contains("180", StringComparison.Ordinal),
                "当前指令引用 chip 不得向用户暴露归一化或画布坐标");

            var summaryText = FindVisualDescendants<TextBlock>(propertiesView)
                .FirstOrDefault(candidate =>
                    ReferenceEquals(candidate.DataContext, conversationMessage) &&
                    string.Equals(candidate.Text, safeSummary, StringComparison.Ordinal))
                ?? throw new InvalidOperationException(
                    "对话历史未渲染独立的图片/区域引用摘要。");
            Equal(
                nameof(ArtWorkspaceConversationMessageViewModel.ReferenceSummary),
                GetBindingPath(summaryText, TextBlock.TextProperty),
                "对话历史引用摘要绑定");
            Equal("只调整这里的明暗关系。", conversationMessage.Text,
                "用户可编辑指令正文必须与结构化引用摘要分离");
            False(conversationMessage.Text.Contains(image.AbsolutePath, StringComparison.OrdinalIgnoreCase),
                "用户可见指令正文不得混入本地路径");

            var selectTool = toolPalette.FindName("ArtWorkspaceToolSelectButton") as ToggleButton
                ?? throw new InvalidOperationException("美术工作区工具栏缺少选择移动 ToggleButton。");
            var regionTool = toolPalette.FindName("ArtWorkspaceToolRegionSelectButton") as ToggleButton
                ?? throw new InvalidOperationException("美术工作区工具栏缺少图像框选 ToggleButton。");
            var clearRegion = toolPalette.FindName("ArtWorkspaceClearRegionButton") as Button
                ?? throw new InvalidOperationException("美术工作区工具栏缺少取消框选按钮。");
            Equal(
                nameof(ArtWorkspaceViewModel.SelectMoveToolCommand),
                GetBindingPath(selectTool, ButtonBase.CommandProperty),
                "选择移动工具命令绑定");
            Equal(
                nameof(ArtWorkspaceViewModel.IsSelectMoveToolActive),
                GetBindingPath(selectTool, ToggleButton.IsCheckedProperty),
                "选择移动工具选中状态绑定");
            Equal(
                nameof(ArtWorkspaceViewModel.ImageRegionSelectToolCommand),
                GetBindingPath(regionTool, ButtonBase.CommandProperty),
                "图像框选工具命令绑定");
            Equal(
                nameof(ArtWorkspaceViewModel.IsImageRegionSelectToolActive),
                GetBindingPath(regionTool, ToggleButton.IsCheckedProperty),
                "图像框选工具选中状态绑定");
            Equal(
                nameof(ArtWorkspaceViewModel.HasSelectedImage),
                GetBindingPath(regionTool, UIElement.IsEnabledProperty),
                "图像框选工具图片选择门禁绑定");
            Equal(
                nameof(ArtWorkspaceViewModel.HasSelectedImageRegion),
                GetBindingPath(clearRegion, UIElement.IsEnabledProperty),
                "取消框选按钮状态绑定");
            True(regionTool.IsEnabled && clearRegion.IsEnabled,
                "当前存在有效图片和区域时两个框选操作均应可用");

            image.IsVisible = false;
            PumpDispatcher();
            False(viewModel.HasSelectedImageRegion,
                "隐藏当前图片时必须清理其局部框选");
            False(viewModel.ImageRegionSelectToolCommand.CanExecute(null),
                "隐藏当前图片时图像框选工具必须禁用");
            True(referenceText.Text.Contains("已隐藏", StringComparison.Ordinal),
                "隐藏图片仍被选中时引用 chip 应明确说明不会提交");
            False(referenceText.Text.Contains(image.AbsolutePath, StringComparison.OrdinalIgnoreCase),
                "隐藏状态提示仍不得泄露绝对路径");

            image.IsVisible = true;
            PumpDispatcher();
            True(viewModel.ImageRegionSelectToolCommand.CanExecute(null),
                "重新显示当前图片后图像框选工具应恢复可用");
            File.Delete(image.AbsolutePath);
            False(viewModel.ImageRegionSelectToolCommand.CanExecute(null),
                "受管图片文件缺失时图像框选工具必须 fail-closed");

            viewModel.SelectImage(null);
            PumpDispatcher();
            False(viewModel.ImageRegionSelectToolCommand.CanExecute(null),
                "清空当前图片选择后图像框选工具必须禁用");
            Equal("本轮未指定图片指代", referenceText.Text,
                "清空图片选择后引用 chip 应回到无指代状态");
        }
        finally
        {
            window.Close();
        }
    }

    private static string? GetBindingPath(DependencyObject target, DependencyProperty property) =>
        BindingOperations.GetBindingExpression(target, property)?.ParentBinding.Path?.Path;

    private static IEnumerable<T> FindVisualDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var descendant in FindVisualDescendants<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private static void SavePngFixture(string path, int width, int height)
    {
        var stride = width * 4;
        var pixels = new byte[stride * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var offset = y * stride + x * 4;
                pixels[offset] = (byte)(60 + x * 4);
                pixels[offset + 1] = (byte)(100 + y * 5);
                pixels[offset + 2] = 180;
                pixels[offset + 3] = byte.MaxValue;
            }
        }

        var bitmap = BitmapSource.Create(
            width,
            height,
            96d,
            96d,
            PixelFormats.Bgra32,
            palette: null,
            pixels,
            stride);
        bitmap.Freeze();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        encoder.Save(stream);
    }

    private static void PumpDispatcher() =>
        Dispatcher.CurrentDispatcher.Invoke(
            DispatcherPriority.ApplicationIdle,
            new Action(() => { }));

    private static void Near(double expected, double actual, string message)
    {
        if (!double.IsFinite(actual) || Math.Abs(expected - actual) > 0.000001d)
        {
            throw new InvalidOperationException($"{message}：预期 {expected}，实际 {actual}。");
        }
    }

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message}：预期 {expected}，实际 {actual}。");
        }
    }

    private static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void False(bool condition, string message) => True(!condition, message);

    private sealed class EmptyImagePicker : IArtWorkspaceImagePicker
    {
        public IReadOnlyList<string> SelectImages(string? initialDirectory) => [];
    }
}
