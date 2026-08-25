using GAssetForge.App.Services;
using GAssetForge.App.ViewModels;
using GAssetForge.App.Views;
using GAssetForge.Core.Generation;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace GAssetForge.SmokeTests;

internal static class AssetGenerationSpreadsheetWorkflowSmokeTests
{
    public static void Run(string temporaryRoot)
    {
        var testRoot = Path.Combine(
            temporaryRoot,
            "asset-generation-spreadsheet-workflow",
            Guid.NewGuid().ToString("N"));
        var paths = new WorkspacePaths(testRoot);
        var settingsService = new UserSettingsService(paths);
        var editor = new EditorWorkspaceViewModel(
            new JsonProjectRepository(paths),
            new DesktopFileDialogService(),
            new ProjectHistoryService());
        var picker = new ScriptedSpreadsheetPicker();
        var reader = new ScriptedSpreadsheetReader();
        var shell = new ShellViewModel(
            new ThemeService(),
            settingsService,
            editor,
            assetGenerationSpreadsheetPicker: picker,
            assetGenerationSpreadsheetReader: reader);

        try
        {
            var generation = shell.AssetGeneration;
            var inheritedOutputDirectory = Path.Combine(paths.GeneratedAssetDirectory, "Imported");
            Directory.CreateDirectory(inheritedOutputDirectory);
            var originalDraft = generation.DraftRows.Single();
            originalDraft.Description = "现有任务";
            originalDraft.OutputDirectory = inheritedOutputDirectory;
            generation.SelectedDraftRow = originalDraft;

            var firstPath = Path.Combine(testRoot, "descriptions.xlsx");
            var firstWorkbook = new AssetGenerationSpreadsheetWorkbook(
                firstPath,
                [
                    new AssetGenerationSpreadsheetSheet(
                        "Tilemap",
                        [
                            new AssetGenerationSpreadsheetColumn(0, "编号", "column_1"),
                            new AssetGenerationSpreadsheetColumn(1, "物品描述", "column_2"),
                            new AssetGenerationSpreadsheetColumn(2, "标签", "column_3")
                        ],
                        [
                            new AssetGenerationSpreadsheetRow(2, ["T-001", "苔藓石质宝箱", "prop"]),
                            new AssetGenerationSpreadsheetRow(3, ["T-002", "   ", "skip"])
                        ]),
                    new AssetGenerationSpreadsheetSheet(
                        "Icons",
                        [
                            new AssetGenerationSpreadsheetColumn(0, "Description", "column_1"),
                            new AssetGenerationSpreadsheetColumn(1, "Output Path", "column_2")
                        ],
                        [
                            new AssetGenerationSpreadsheetRow(
                                2,
                                ["青铜罗盘图标", @"C:\Outside\MustNotBeImported"])
                        ])
                ]);
            picker.Enqueue(firstPath);
            var pendingRead = new TaskCompletionSource<AssetGenerationSpreadsheetWorkbook>();
            reader.Enqueue(pendingRead.Task);

            var previewView = new AssetGenerationView { DataContext = generation };
            previewView.ApplyTemplate();
            previewView.Measure(new Size(480, 720));
            previewView.Arrange(new Rect(0, 0, 480, 720));
            previewView.UpdateLayout();
            PumpDispatcher();
            var inlineStatus = previewView.FindName("AssetGenerationSpreadsheetInlineStatusText") as TextBlock
                ?? throw new InvalidOperationException("Excel 工作流测试缺少始终可见的导入状态文本。");

            generation.ImportDescriptionSpreadsheetCommand.Execute(null);
            WaitUntil(
                () => generation.IsSpreadsheetImportBusy,
                TimeSpan.FromSeconds(5),
                "Excel 导入未进入正在读取状态");
            False(generation.HasSpreadsheetPreview, "首次读取完成前不得伪造 Excel 预览");
            True(generation.SpreadsheetImportStatusText.Contains("正在读取", StringComparison.Ordinal),
                "慢表格读取阶段应提供明确的就地状态");
            inlineStatus.GetBindingExpression(TextBlock.TextProperty)?.UpdateTarget();
            True(inlineStatus.Visibility == Visibility.Visible &&
                 inlineStatus.Text.Contains("正在读取", StringComparison.Ordinal),
                "尚无预览时正在读取状态仍应在步骤 2 内可见");

            pendingRead.SetResult(firstWorkbook);
            WaitUntil(
                () => generation.HasSpreadsheetPreview && !generation.IsSpreadsheetImportBusy,
                TimeSpan.FromSeconds(5),
                "Excel 导入未建立预览状态");

            Equal("descriptions.xlsx", generation.ImportedSpreadsheetFileName, "导入文件名");
            Equal(2, generation.ImportedWorksheets.Count, "工作表选择列表");
            Equal("Tilemap", generation.SelectedImportedWorksheet?.Name, "默认工作表");
            Equal("物品描述", generation.SelectedDescriptionColumn?.Header, "中文描述列自动匹配");
            Equal(2, generation.ImportedRows.Count, "Excel 预览数据行");
            True(generation.ImportedRows[0].CanImport, "有效描述行应允许导入");
            False(generation.ImportedRows[1].CanImport, "空描述行应显示为不可导入");
            generation.ImportedRows[1].IsSelected = true;
            False(generation.ImportedRows[1].IsSelected, "无效描述行的复选框状态不得被手工置为选中");

            previewView.Measure(new Size(480, 720));
            previewView.Arrange(new Rect(0, 0, 480, 720));
            previewView.UpdateLayout();
            PumpDispatcher();
            var pageScroller = previewView.FindName("AssetGenerationPageScrollViewer") as ScrollViewer
                ?? throw new InvalidOperationException("Excel 工作流测试缺少页面滚动容器。");
            var previewGrid = previewView.FindName("AssetGenerationSpreadsheetPreviewGrid") as DataGrid
                ?? throw new InvalidOperationException("Excel 工作流测试缺少预览 DataGrid。");
            previewGrid.ApplyTemplate();
            previewGrid.UpdateLayout();
            var previewGridScroller = FindVisualChild<ScrollViewer>(previewGrid)
                ?? throw new InvalidOperationException("Excel 预览 DataGrid 缺少内部滚动容器。");
            var importButton = previewView.FindName("ImportGenerationSpreadsheetButton") as Button
                ?? throw new InvalidOperationException("Excel 工作流测试缺少导入按钮。");
            var addRowButton = previewView.FindName("AddGenerationRowButton") as Button
                ?? throw new InvalidOperationException("Excel 工作流测试缺少新建行按钮。");
            var summaryText = previewView.FindName("AssetGenerationSpreadsheetSummaryText") as TextBlock
                ?? throw new InvalidOperationException("Excel 工作流测试缺少预览摘要。");
            var applyHintText = previewView.FindName("AssetGenerationSpreadsheetApplyHintText") as TextBlock
                ?? throw new InvalidOperationException("Excel 工作流测试缺少应用提示。");
            var appendButton = previewView.FindName("AppendSelectedWorkbookRowsButton") as Button
                ?? throw new InvalidOperationException("Excel 工作流测试缺少追加按钮。");
            var replaceButton = previewView.FindName("ReplaceWithSelectedWorkbookRowsButton") as Button
                ?? throw new InvalidOperationException("Excel 工作流测试缺少替换按钮。");
            True(pageScroller.ScrollableWidth <= 0.5d,
                "预览打开后 480px 宽素材生成页仍不得产生页面级横向滚动");
            True(previewGridScroller.ScrollableWidth > 0d,
                "Excel 动态列超过可视宽度时应由预览表格自身提供横向滚动");
            AssertInsideHorizontalBounds(importButton, previewView, "480px 预览状态下 Excel 导入按钮");
            AssertInsideHorizontalBounds(addRowButton, previewView, "480px 预览状态下新建任务行按钮");
            AssertInsideHorizontalBounds(appendButton, previewView, "480px 预览状态下追加任务按钮");
            AssertInsideHorizontalBounds(replaceButton, previewView, "480px 预览状态下替换任务按钮");
            False(GetBounds(summaryText, previewView).IntersectsWith(GetBounds(appendButton, previewView)),
                "窄宽预览摘要不得与追加按钮重叠");
            False(GetBounds(applyHintText, previewView).IntersectsWith(GetBounds(replaceButton, previewView)),
                "窄宽预览提示不得与替换按钮重叠");
            Equal(6, previewGrid.Columns.Count, "三列 Excel 数据应与三列固定状态列共同显示");
            var customizedColumn = previewGrid.Columns[3];
            customizedColumn.Width = new DataGridLength(333d);
            customizedColumn.DisplayIndex = previewGrid.Columns.Count - 1;
            generation.SelectedDescriptionColumn = generation.ImportedColumns[0];
            PumpDispatcher();
            True(previewGrid.Columns.Contains(customizedColumn),
                "切换描述列不得全量重建 Excel 动态列");
            Equal(333d, customizedColumn.Width.Value,
                "切换描述列后应保留用户拖动设置的列宽");
            Equal(previewGrid.Columns.Count - 1, customizedColumn.DisplayIndex,
                "切换描述列后应保留用户调整的列顺序");
            generation.SelectedDescriptionColumn = generation.ImportedColumns[1];
            PumpDispatcher();

            generation.SelectAllImportedRowsCommand.Execute(null);
            Equal(1, generation.SelectedImportedRowCount, "全选只应包含有效描述行");
            True(generation.AppendSelectedImportedRowsCommand.CanExecute(null),
                "有效 Excel 描述应允许追加为任务条");
            generation.AppendSelectedImportedRowsCommand.Execute(null);
            Equal(2, generation.DraftRows.Count, "追加 Excel 描述后的任务行数");
            var appendedDraft = generation.DraftRows.Last();
            Equal("苔藓石质宝箱", appendedDraft.Description, "追加后的物品描述");
            Equal(1, appendedDraft.Quantity, "Excel 导入数量默认值");
            Equal(inheritedOutputDirectory, appendedDraft.OutputDirectory, "追加行继承当前任务输出目录");

            var persistedAfterAppend = new UserSettingsService(paths).Load();
            True(persistedAfterAppend.AssetGenerationRows.Any(row =>
                    string.Equals(row.Description, "苔藓石质宝箱", StringComparison.Ordinal)),
                "追加后的任务行应进入现有素材生成草稿持久化");

            generation.SelectedImportedWorksheet = generation.ImportedWorksheets[1];
            PumpDispatcher();
            False(previewGrid.Columns.Contains(customizedColumn),
                "切换工作表时应按新工作表结构重建动态列");
            Equal(5, previewGrid.Columns.Count, "两列工作表应显示两列动态 Excel 数据");
            Equal("Description", generation.SelectedDescriptionColumn?.Header,
                "切换工作表后应自动匹配英文描述列");
            Equal(1, generation.ImportedRows.Count, "切换工作表应重建 Excel 预览行");
            generation.SelectAllImportedRowsCommand.Execute(null);
            generation.ReplaceDraftRowsWithImportedRowsCommand.Execute(null);
            Equal(1, generation.DraftRows.Count, "替换任务表应原子移除旧任务行");
            Equal("青铜罗盘图标", generation.DraftRows[0].Description, "替换后的物品描述");
            Equal(inheritedOutputDirectory, generation.DraftRows[0].OutputDirectory,
                "Excel 中其它路径列不得控制任务输出目录");

            picker.Enqueue(null);
            generation.ImportDescriptionSpreadsheetCommand.Execute(null);
            WaitUntil(
                () => !generation.IsSpreadsheetImportBusy,
                TimeSpan.FromSeconds(5),
                "取消 Excel 选择后导入命令未结束");
            Equal("descriptions.xlsx", generation.ImportedSpreadsheetFileName,
                "取消导入不得清除当前 Excel 预览");

            picker.Enqueue(Path.Combine(testRoot, "broken.xlsx"));
            reader.Enqueue(new InvalidDataException("模拟损坏工作簿"));
            generation.ImportDescriptionSpreadsheetCommand.Execute(null);
            WaitUntil(
                () => !generation.IsSpreadsheetImportBusy &&
                      generation.SpreadsheetImportStatusText.Contains("导入失败", StringComparison.Ordinal),
                TimeSpan.FromSeconds(5),
                "损坏 Excel 导入未收束为可展示错误");
            Equal("descriptions.xlsx", generation.ImportedSpreadsheetFileName,
                "读取失败不得破坏上一次成功预览");
            Equal("青铜罗盘图标", generation.DraftRows.Single().Description,
                "读取失败不得部分覆盖现有任务表");

            var oversizedPath = Path.Combine(testRoot, "too-many-descriptions.xlsx");
            var oversizedRows = Enumerable.Range(1, AssetGenerationViewModel.MaximumDraftRowCount + 1)
                .Select(index => new AssetGenerationSpreadsheetRow(index + 1, [$"批量描述 {index}"]))
                .ToArray();
            picker.Enqueue(oversizedPath);
            reader.Enqueue(new AssetGenerationSpreadsheetWorkbook(
                oversizedPath,
                [
                    new AssetGenerationSpreadsheetSheet(
                        "Oversized",
                        [new AssetGenerationSpreadsheetColumn(0, "描述", "column_1")],
                        oversizedRows)
                ]));
            generation.ImportDescriptionSpreadsheetCommand.Execute(null);
            WaitUntil(
                () => !generation.IsSpreadsheetImportBusy &&
                      string.Equals(generation.ImportedSpreadsheetFileName, "too-many-descriptions.xlsx",
                          StringComparison.Ordinal),
                TimeSpan.FromSeconds(5),
                "超量描述工作簿未进入预览");
            generation.SelectAllImportedRowsCommand.Execute(null);
            False(generation.AppendSelectedImportedRowsCommand.CanExecute(null),
                "超过批次上限时追加命令必须禁用");
            False(generation.ReplaceDraftRowsWithImportedRowsCommand.CanExecute(null),
                "超过批次上限时替换命令必须禁用");
            generation.ReplaceDraftRowsWithImportedRowsCommand.Execute(null);
            Equal(1, generation.DraftRows.Count, "超量替换必须整体拒绝且保留原任务表");
            Equal("青铜罗盘图标", generation.DraftRows[0].Description,
                "超量替换不得留下部分导入行");

            generation.ClearSpreadsheetPreviewCommand.Execute(null);
            False(generation.HasSpreadsheetPreview, "关闭预览应释放瞬时 Excel 状态");
            Equal(1, generation.DraftRows.Count, "关闭预览不得删除已导入任务条");

            picker.Enqueue(Path.Combine(testRoot, "first-import-failure.xlsx"));
            reader.Enqueue(new InvalidDataException("首次读取失败 smoke"));
            generation.ImportDescriptionSpreadsheetCommand.Execute(null);
            WaitUntil(
                () => !generation.IsSpreadsheetImportBusy &&
                      generation.SpreadsheetImportStatusText.Contains("导入失败", StringComparison.Ordinal),
                TimeSpan.FromSeconds(5),
                "无现有预览时的 Excel 失败状态未收束");
            inlineStatus.GetBindingExpression(TextBlock.TextProperty)?.UpdateTarget();
            True(!generation.HasSpreadsheetPreview &&
                 inlineStatus.Visibility == Visibility.Visible &&
                 inlineStatus.Text.Contains("导入失败", StringComparison.Ordinal),
                "首次导入失败时错误必须在步骤 2 内直接可见");
            Equal("青铜罗盘图标", generation.DraftRows.Single().Description,
                "首次导入失败不得影响已建立的任务草稿");
        }
        finally
        {
            shell.Shutdown();
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    private static void WaitUntil(Func<bool> condition, TimeSpan timeout, string message)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.Background);
            Thread.Sleep(10);
        }

        throw new InvalidOperationException(message);
    }

    private static void PumpDispatcher() =>
        Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.ApplicationIdle);

    private static void AssertInsideHorizontalBounds(
        FrameworkElement element,
        FrameworkElement ancestor,
        string message)
    {
        var bounds = GetBounds(element, ancestor);
        True(element.ActualWidth > 0d &&
             bounds.Left >= -0.5d &&
             bounds.Right <= ancestor.ActualWidth + 0.5d,
            $"{message}应完整位于可视范围内，实际 {bounds.Left:0.0}..{bounds.Right:0.0} / " +
            $"{ancestor.ActualWidth:0.0}");
    }

    private static Rect GetBounds(FrameworkElement element, FrameworkElement ancestor) =>
        element.TransformToAncestor(ancestor).TransformBounds(new Rect(element.RenderSize));

    private static T? FindVisualChild<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                return match;
            }

            var nested = FindVisualChild<T>(child);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
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
            throw new InvalidOperationException($"{message}：预期 {expected}，实际 {actual}。");
        }
    }

    private sealed class ScriptedSpreadsheetPicker : IAssetGenerationSpreadsheetPicker
    {
        private readonly Queue<string?> _paths = new();

        public void Enqueue(string? path) => _paths.Enqueue(path);

        public string? SelectSpreadsheet(string? initialDirectory) =>
            _paths.Count > 0 ? _paths.Dequeue() : null;
    }

    private sealed class ScriptedSpreadsheetReader : IAssetGenerationSpreadsheetReader
    {
        private readonly Queue<Func<CancellationToken, Task<AssetGenerationSpreadsheetWorkbook>>> _results = new();

        public void Enqueue(AssetGenerationSpreadsheetWorkbook workbook) =>
            _results.Enqueue(_ => Task.FromResult(workbook));

        public void Enqueue(Task<AssetGenerationSpreadsheetWorkbook> workbookTask) =>
            _results.Enqueue(_ => workbookTask);

        public void Enqueue(Exception exception) =>
            _results.Enqueue(_ => Task.FromException<AssetGenerationSpreadsheetWorkbook>(exception));

        public Task<AssetGenerationSpreadsheetWorkbook> ReadAsync(
            string path,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_results.Count == 0)
            {
                throw new InvalidOperationException("测试读取器没有排队结果。");
            }

            return _results.Dequeue()(cancellationToken);
        }
    }
}
