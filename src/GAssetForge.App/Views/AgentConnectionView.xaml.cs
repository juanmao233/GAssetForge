using GAssetForge.App.ViewModels;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
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
