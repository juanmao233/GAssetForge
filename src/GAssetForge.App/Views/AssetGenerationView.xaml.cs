using GAssetForge.App.ViewModels;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Globalization;
using System.Windows.Threading;

namespace GAssetForge.App.Views;

public partial class AssetGenerationView : UserControl
{
    private const int SpreadsheetPreviewFixedColumnCount = 3;
    private AssetGenerationViewModel? _subscribedViewModel;

    public AssetGenerationView()
    {
        InitializeComponent();
        AssetGenerationRequestGrid.ItemContainerGenerator.ItemsChanged += OnRequestGridItemsChanged;
        DataContextChanged += OnAssetGenerationDataContextChanged;
    }

    private void OnAssetGenerationDataContextChanged(
        object sender,
        DependencyPropertyChangedEventArgs e)
    {
        if (_subscribedViewModel is not null)
        {
            _subscribedViewModel.PropertyChanged -= OnAssetGenerationViewModelPropertyChanged;
        }

        _subscribedViewModel = e.NewValue as AssetGenerationViewModel;
        if (_subscribedViewModel is not null)
        {
            _subscribedViewModel.PropertyChanged += OnAssetGenerationViewModelPropertyChanged;
        }

        RebuildSpreadsheetPreviewColumns();
    }

    private void OnAssetGenerationViewModelPropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        // A description-column change only changes row matching. Recreating DataGrid columns here
        // would discard the Excel-like Width and DisplayIndex choices the user just made.
        if (e.PropertyName == nameof(AssetGenerationViewModel.ImportedColumns))
        {
            Dispatcher.BeginInvoke(
                DispatcherPriority.DataBind,
                new Action(RebuildSpreadsheetPreviewColumns));
        }
    }

    private void RebuildSpreadsheetPreviewColumns()
    {
        while (AssetGenerationSpreadsheetPreviewGrid.Columns.Count > SpreadsheetPreviewFixedColumnCount)
        {
            AssetGenerationSpreadsheetPreviewGrid.Columns.RemoveAt(
                AssetGenerationSpreadsheetPreviewGrid.Columns.Count - 1);
        }

        if (DataContext is not AssetGenerationViewModel viewModel)
        {
            return;
        }

        foreach (var column in viewModel.ImportedColumns)
        {
            AssetGenerationSpreadsheetPreviewGrid.Columns.Add(
                new DataGridTextColumn
                {
                    Header = column.Header,
                    IsReadOnly = true,
                    MinWidth = 100d,
                    Width = new DataGridLength(
                        ReferenceEquals(column, viewModel.SelectedDescriptionColumn) ? 260d : 160d),
                    Binding = new Binding($"Cells[{column.Index}]")
                    {
                        Mode = BindingMode.OneWay
                    },
                    ClipboardContentBinding = new Binding($"Cells[{column.Index}]")
                });
        }
    }

    private void OnRequestGridLoadingRow(object sender, DataGridRowEventArgs e) =>
        e.Row.Header = (e.Row.GetIndex() + 1).ToString(CultureInfo.InvariantCulture);

    private void OnRequestGridItemsChanged(object? sender, ItemsChangedEventArgs e) =>
        AssetGenerationRequestGrid.Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            new Action(UpdateRequestGridRowHeaders));

    private void UpdateRequestGridRowHeaders()
    {
        for (var index = 0; index < AssetGenerationRequestGrid.Items.Count; index++)
        {
            if (AssetGenerationRequestGrid.ItemContainerGenerator.ContainerFromIndex(index) is DataGridRow row)
            {
                row.Header = (index + 1).ToString(CultureInfo.InvariantCulture);
            }
        }
    }

    private void OnCopyCodexConnectionPromptClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not AssetGenerationViewModel viewModel)
        {
            return;
        }

        viewModel.ShowCodexConnectionPromptPreview();
        CopyManualFallbackPrompt(viewModel, viewModel.CodexConnectionPrompt, "Codex 连接指令");
    }

    private void OnCopyCurrentBatchPromptClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not AssetGenerationViewModel viewModel ||
            !viewModel.TryShowCurrentBatchPromptPreview())
        {
            return;
        }

        CopyManualFallbackPrompt(viewModel, viewModel.CurrentBatchManualPrompt, "当前批次任务指令");
    }

    private static void CopyManualFallbackPrompt(
        AssetGenerationViewModel viewModel,
        string prompt,
        string label)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            viewModel.ReportManualFallbackCopyResult(label, copied: false, "当前没有可复制的有效提示");
            return;
        }

        try
        {
            Clipboard.SetText(prompt);
            viewModel.ReportManualFallbackCopyResult(label, copied: true);
        }
        catch (Exception exception)
        {
            viewModel.ReportManualFallbackCopyResult(label, copied: false, exception.Message);
        }
    }
}
