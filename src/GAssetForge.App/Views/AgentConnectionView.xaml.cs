using GAssetForge.App.ViewModels;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace GAssetForge.App.Views;

public partial class AgentConnectionView : UserControl, IDisposable
{
    private AgentConnectionViewModel? _subscribedViewModel;
    private bool _disposed;

    public AgentConnectionView()
    {
        InitializeComponent();

        DataContextChanged += OnDataContextChanged;
    }

    public AgentConnectionView(AgentConnectionViewModel viewModel)
        : this()
    {
        DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DetachViewModel(_subscribedViewModel);
        DataContextChanged -= OnDataContextChanged;
        GC.SuppressFinalize(this);
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        DetachViewModel(e.OldValue as AgentConnectionViewModel);
        AttachViewModel(e.NewValue as AgentConnectionViewModel);
    }

    private void AttachViewModel(AgentConnectionViewModel? viewModel)
    {
        if (viewModel is null || ReferenceEquals(_subscribedViewModel, viewModel))
        {
            return;
        }

        _subscribedViewModel = viewModel;
        viewModel.Logs.CollectionChanged += OnLogsCollectionChanged;
        viewModel.CopyConfigRequested += OnCopyConfigRequested;
        viewModel.CopyCodexConnectionPromptRequested += OnCopyCodexConnectionPromptRequested;
        ScrollToLatestLog();
    }

    private void DetachViewModel(AgentConnectionViewModel? viewModel)
    {
        if (viewModel is null || !ReferenceEquals(_subscribedViewModel, viewModel))
        {
            return;
        }

        viewModel.Logs.CollectionChanged -= OnLogsCollectionChanged;
        viewModel.CopyConfigRequested -= OnCopyConfigRequested;
        viewModel.CopyCodexConnectionPromptRequested -= OnCopyCodexConnectionPromptRequested;
        _subscribedViewModel = null;
    }

    private void OnLogsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        ScrollToLatestLog();
    }

    private void ScrollToLatestLog()
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (LogListBox.Items.Count > 0)
            {
                LogListBox.ScrollIntoView(LogListBox.Items[^1]);
            }
        });
    }

    private void AgentSettingsScrollViewer_OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer settingsScroller || e.Delta == 0)
        {
            return;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0 && settingsScroller.ScrollableWidth > 0d)
        {
            var horizontalStep = e.Delta > 0 ? -64d : 64d;
            settingsScroller.ScrollToHorizontalOffset(Math.Clamp(
                settingsScroller.HorizontalOffset + horizontalStep,
                0d,
                settingsScroller.ScrollableWidth));
            e.Handled = true;
            return;
        }

        var innerScroller = FindInnerScrollViewer(e.OriginalSource as DependencyObject, settingsScroller);
        if (innerScroller is not null && CanScrollVertically(innerScroller, e.Delta))
        {
            return;
        }

        if (settingsScroller.ScrollableHeight <= 0d)
        {
            return;
        }

        var verticalStep = e.Delta > 0 ? -48d : 48d;
        settingsScroller.ScrollToVerticalOffset(Math.Clamp(
            settingsScroller.VerticalOffset + verticalStep,
            0d,
            settingsScroller.ScrollableHeight));
        e.Handled = true;
    }

    private static ScrollViewer? FindInnerScrollViewer(DependencyObject? source, ScrollViewer outerScroller)
    {
        for (var current = source;
             current is not null && !ReferenceEquals(current, outerScroller);
             current = GetVisualOrLogicalParent(current))
        {
            if (current is ScrollViewer scrollViewer)
            {
                return scrollViewer;
            }
        }

        return null;
    }

    private static bool CanScrollVertically(ScrollViewer scrollViewer, int wheelDelta) =>
        wheelDelta > 0
            ? scrollViewer.VerticalOffset > 0.1d
            : scrollViewer.VerticalOffset < scrollViewer.ScrollableHeight - 0.1d;

    private static DependencyObject? GetVisualOrLogicalParent(DependencyObject element)
    {
        if (element is Visual)
        {
            return VisualTreeHelper.GetParent(element);
        }

        return element is FrameworkContentElement contentElement
            ? contentElement.Parent
            : LogicalTreeHelper.GetParent(element);
    }

    private void OnCopyConfigRequested(object? sender, string configText)
    {
        if (string.IsNullOrWhiteSpace(configText))
        {
            return;
        }

        try
        {
            Clipboard.SetText(configText);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                Window.GetWindow(this),
                $"复制 Agent 配置失败：{exception.Message}",
                "MCP Agent 接入",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void OnCopyCodexConnectionPromptRequested(object? sender, string prompt)
    {
        if (sender is not AgentConnectionViewModel viewModel || string.IsNullOrWhiteSpace(prompt))
        {
            return;
        }

        try
        {
            Clipboard.SetText(prompt);
            viewModel.ReportCodexConnectionPromptCopyResult(copied: true);
        }
        catch (Exception exception)
        {
            viewModel.ReportCodexConnectionPromptCopyResult(copied: false, exception.Message);
            MessageBox.Show(
                Window.GetWindow(this),
                $"复制 Codex 连接指令失败：{exception.Message}",
                "MCP Agent 接入",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }
}
