using AvalonDock;
using AvalonDock.Layout;
using GAssetForge.App.Services;
using GAssetForge.App.ViewModels;
using GAssetForge.App.Views;
using GAssetForge.Core.Workflows;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace GAssetForge.App;

public partial class MainWindow : Window
{
    public const string ToolsContentId = "ui-tools";
    public const string CanvasContentId = "ui-canvas";
    public const string TilemapWorkspaceContentId = "workspace-tilemap";
    public const string CardDesignWorkspaceContentId = "workspace-card-design";
    public const string ParallelPerspectiveWorkspaceContentId = "workspace-parallel-perspective";
    public const string PropertiesContentId = "ui-properties";
    public const string LayersContentId = "ui-layers";
    public const string ProjectContentId = "ui-project";
    public const string StyleLibraryContentId = "art-style-library";
    public const string AssetGenerationContentId = "asset-generation";
    public const string BackgroundRemovalContentId = "background-removal";
    public const string PixelAlignmentContentId = "pixel-grid-alignment";
    public const string BlueprintContentId = "workflow-blueprint";
    public const string AgentConnectionContentId = "mcp-agent-connection";
    public const string CommandConsoleContentId = "command-console";

    private readonly ShellViewModel _viewModel;
    private readonly AvalonDockLayoutService _layoutService;
    private readonly Dictionary<string, ContentControl> _panelHosts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ContentControl> _documentHosts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FrameworkElement> _sharedPanelViews = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FrameworkElement> _uiPanelViews = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FrameworkElement> _tilemapPanelViews = new(StringComparer.Ordinal);
    private BlueprintToolPaletteView _blueprintToolPaletteView = null!;
    private FrameworkElement? _workflowToolPaletteView;
    private string _activeToolContextContentId = CanvasContentId;
    private LayoutAnchorable? _observedBlueprintAnchorable;
    private bool _isLoaded;
    private bool _isShutdownInProgress;
    private bool _isShutdownComplete;

    public MainWindow(ShellViewModel viewModel, AvalonDockLayoutService layoutService)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _layoutService = layoutService;
        DataContext = viewModel;

        CreatePanelHosts();
        RefreshPanelContents();
        BuildDefaultLayout();

        Loaded += OnLoaded;
        ContentRendered += OnContentRendered;
        Closing += OnClosing;
        PreviewKeyDown += OnPreviewKeyDown;
        _viewModel.LayoutResetRequested += OnLayoutResetRequested;
        _viewModel.PanelShowRequested += OnPanelShowRequested;
        _viewModel.PanelToggleRequested += OnPanelToggleRequested;
        _viewModel.WorkflowDocumentRequested += OnWorkflowDocumentRequested;
        _viewModel.PropertyChanged += OnShellPropertyChanged;
        DockManager.DocumentClosed += OnDocumentClosed;
        DockManager.ActiveContentChanged += OnDockManagerActiveContentChanged;
    }

    private IReadOnlyDictionary<string, object> PanelContent =>
        _panelHosts
            .Concat(_documentHosts)
            .ToDictionary(pair => pair.Key, pair => (object)pair.Value, StringComparer.Ordinal);

    private void CreatePanelHosts()
    {
        _panelHosts[ToolsContentId] = CreateHost();
        _panelHosts[PropertiesContentId] = CreateHost();
        _panelHosts[LayersContentId] = CreateHost();
        _panelHosts[ProjectContentId] = CreateHost();
        _panelHosts[StyleLibraryContentId] = CreateHost();
        _panelHosts[AssetGenerationContentId] = CreateHost();
        _panelHosts[BackgroundRemovalContentId] = CreateHost();
        _panelHosts[PixelAlignmentContentId] = CreateHost();
        _panelHosts[BlueprintContentId] = CreateHost();
        _panelHosts[AgentConnectionContentId] = CreateHost();
        _panelHosts[CommandConsoleContentId] = CreateHost();

        _documentHosts[CanvasContentId] = CreateHost();
        _documentHosts[TilemapWorkspaceContentId] = CreateHost();
        _documentHosts[CardDesignWorkspaceContentId] = CreateHost();
        _documentHosts[ParallelPerspectiveWorkspaceContentId] = CreateHost();

        _uiPanelViews[ToolsContentId] = new ToolPaletteView { DataContext = _viewModel.EditorWorkspace };
        _blueprintToolPaletteView = new BlueprintToolPaletteView { DataContext = _viewModel.Blueprint };
        _uiPanelViews[PropertiesContentId] = new PropertiesView { DataContext = _viewModel.EditorWorkspace };
        _uiPanelViews[LayersContentId] = new LayersView { DataContext = _viewModel.EditorWorkspace };
        _sharedPanelViews[ProjectContentId] = new ProjectBrowserView { DataContext = _viewModel.ProjectBrowser };
        _sharedPanelViews[StyleLibraryContentId] = new ArtStyleLibraryView
        {
            DataContext = _viewModel.ArtStyleLibrary
        };
        _sharedPanelViews[AssetGenerationContentId] = new AssetGenerationView
        {
            DataContext = _viewModel.AssetGeneration
        };
        _sharedPanelViews[BackgroundRemovalContentId] = new BackgroundRemovalView
        {
            DataContext = _viewModel.BackgroundRemoval
        };
        _sharedPanelViews[PixelAlignmentContentId] = new PixelAlignmentView
        {
            DataContext = _viewModel.PixelAlignment
        };
        _sharedPanelViews[BlueprintContentId] = new BlueprintView
        {
            DataContext = _viewModel.Blueprint
        };
        _sharedPanelViews[AgentConnectionContentId] = new AgentConnectionView
        {
            DataContext = _viewModel.AgentConnection
        };
        _sharedPanelViews[CommandConsoleContentId] = new CommandConsoleView
        {
            DataContext = _viewModel.CommandConsole
        };
        _tilemapPanelViews[PropertiesContentId] = new TilemapPropertiesView
        {
            DataContext = _viewModel.TilemapWorkspace
        };

        _documentHosts[CanvasContentId].Content = new CanvasDocumentView
        {
            DataContext = _viewModel.EditorWorkspace
        };
        _documentHosts[TilemapWorkspaceContentId].Content = new TilemapWorkspaceView
        {
            DataContext = _viewModel.TilemapWorkspace
        };
        _documentHosts[CardDesignWorkspaceContentId].Content = new WorkflowPlaceholderView
        {
            DataContext = new WorkflowPlaceholderViewModel(
                WorkflowCatalog.Dockable.Single(workflow => workflow.Kind == WorkflowKind.CardDesign))
        };
        _documentHosts[ParallelPerspectiveWorkspaceContentId].Content = new WorkflowPlaceholderView
        {
            DataContext = new WorkflowPlaceholderViewModel(
                WorkflowCatalog.Dockable.Single(workflow => workflow.Kind == WorkflowKind.ParallelPerspective))
        };
    }

    private static ContentControl CreateHost() => new()
    {
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
        VerticalContentAlignment = VerticalAlignment.Stretch
    };

    private void RefreshPanelContents()
    {
        foreach (var (contentId, host) in _panelHosts)
        {
            if (_sharedPanelViews.TryGetValue(contentId, out var sharedPanelView))
            {
                host.Content = sharedPanelView;
                continue;
            }

            if (_viewModel.CurrentWorkflow.Kind == WorkflowKind.UiDesign)
            {
                host.Content = _uiPanelViews[contentId];
                continue;
            }

            if (_viewModel.CurrentWorkflow.Kind == WorkflowKind.Tilemap &&
                _tilemapPanelViews.TryGetValue(contentId, out var tilemapPanelView))
            {
                host.Content = tilemapPanelView;
                continue;
            }

            host.Content = CreateUnavailablePanel(GetPanelTitle(contentId));
        }

        _workflowToolPaletteView = _panelHosts[ToolsContentId].Content as FrameworkElement;
        RefreshActiveToolPalette();
        UpdateLayoutTitles();
    }

    private FrameworkElement CreateUnavailablePanel(string panelName)
    {
        var border = new Border
        {
            Padding = new Thickness(18),
            BorderThickness = new Thickness(0),
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock
                    {
                        Text = panelName,
                        FontSize = 15,
                        FontWeight = FontWeights.SemiBold
                    },
                    new TextBlock
                    {
                        Text = $"{_viewModel.CurrentWorkflow.Label} 尚未接入此面板。当前已保留稳定停靠位置，后续可直接替换其业务模块。",
                        Margin = new Thickness(0, 10, 0, 0),
                        TextWrapping = TextWrapping.Wrap,
                        Opacity = 0.7
                    }
                }
            }
        };
        border.SetResourceReference(BackgroundProperty, "SurfaceBrush");
        return border;
    }

    private void BuildDefaultLayout()
    {
        ObserveBlueprintVisibility(null);
        _activeToolContextContentId = CanvasContentId;
        var tools = CreateAnchorable(ToolsContentId);
        var properties = CreateAnchorable(PropertiesContentId);
        var layers = CreateAnchorable(LayersContentId);
        var project = CreateAnchorable(ProjectContentId);
        var styleLibrary = CreateAnchorable(StyleLibraryContentId);
        var assetGeneration = CreateAnchorable(AssetGenerationContentId);
        var backgroundRemoval = CreateAnchorable(BackgroundRemovalContentId);
        var pixelAlignment = CreateAnchorable(PixelAlignmentContentId);
        var blueprint = CreateAnchorable(BlueprintContentId);
        var agentConnection = CreateAnchorable(AgentConnectionContentId);
        var commandConsole = CreateAnchorable(CommandConsoleContentId);
        var workflowDocuments = WorkflowCatalog.Dockable
            .Select(CreateWorkflowDocument)
            .ToArray();
        var activeDocument = workflowDocuments.First(document =>
            string.Equals(
                document.ContentId,
                GetWorkflowDocumentContentId(_viewModel.CurrentWorkflow),
                StringComparison.Ordinal));
        activeDocument.IsSelected = true;
        activeDocument.IsActive = true;

        var toolsPane = new LayoutAnchorablePane(tools)
        {
            DockWidth = new GridLength(112)
        };
        var documentPane = new LayoutDocumentPane
        {
            ShowHeader = true
        };
        foreach (var document in workflowDocuments)
        {
            documentPane.Children.Add(document);
        }
        var projectPane = new LayoutAnchorablePane
        {
            DockHeight = new GridLength(340)
        };
        projectPane.Children.Add(project);
        projectPane.Children.Add(styleLibrary);
        projectPane.Children.Add(assetGeneration);
        projectPane.Children.Add(backgroundRemoval);
        projectPane.Children.Add(pixelAlignment);
        projectPane.Children.Add(blueprint);
        projectPane.Children.Add(commandConsole);
        var agentConnectionPane = new LayoutAnchorablePane(agentConnection)
        {
            DockHeight = new GridLength(560)
        };
        var propertiesPane = new LayoutAnchorablePane(properties)
        {
            DockHeight = new GridLength(0.52, GridUnitType.Star)
        };
        var layersPane = new LayoutAnchorablePane(layers)
        {
            DockHeight = new GridLength(0.48, GridUnitType.Star)
        };

        var center = new LayoutPanel
        {
            Orientation = Orientation.Vertical
        };
        center.Children.Add(documentPane);
        center.Children.Add(agentConnectionPane);
        center.Children.Add(projectPane);

        var right = new LayoutPanel
        {
            Orientation = Orientation.Vertical,
            DockWidth = new GridLength(310)
        };
        right.Children.Add(propertiesPane);
        right.Children.Add(layersPane);

        var rootPanel = new LayoutPanel
        {
            Orientation = Orientation.Horizontal
        };
        rootPanel.Children.Add(toolsPane);
        rootPanel.Children.Add(center);
        rootPanel.Children.Add(right);

        DockManager.Layout = new LayoutRoot { RootPanel = rootPanel };
        ObserveBlueprintVisibility(blueprint);
        agentConnection.Hide();
    }

    private LayoutDocument CreateWorkflowDocument(WorkflowDefinition workflow)
    {
        var contentId = GetWorkflowDocumentContentId(workflow);
        return new LayoutDocument
        {
            ContentId = contentId,
            Title = GetWorkflowDocumentTitle(workflow),
            Content = _documentHosts[contentId],
            CanClose = true,
            CanFloat = true,
            CanMove = true,
            FloatingWidth = 1100,
            FloatingHeight = 760
        };
    }

    private LayoutAnchorable CreateAnchorable(string contentId)
    {
        var (floatingWidth, floatingHeight) = contentId switch
        {
            ToolsContentId => (260d, 480d),
            PropertiesContentId => (340d, 620d),
            LayersContentId => (360d, 620d),
            ProjectContentId => (760d, 360d),
            StyleLibraryContentId => (980d, 720d),
            AssetGenerationContentId => (1120d, 780d),
            BackgroundRemovalContentId => (1180d, 760d),
            PixelAlignmentContentId => (1180d, 760d),
            BlueprintContentId => (1180d, 760d),
            AgentConnectionContentId => (860d, 560d),
            CommandConsoleContentId => (1080d, 620d),
            _ => (360d, 520d)
        };

        return new LayoutAnchorable
        {
            ContentId = contentId,
            Title = GetPanelTitle(contentId),
            Content = _panelHosts[contentId],
            CanClose = false,
            CanHide = true,
            CanFloat = true,
            CanMove = true,
            CanAutoHide = true,
            CanDockAsTabbedDocument = true,
            FloatingWidth = floatingWidth,
            FloatingHeight = floatingHeight
        };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_isLoaded)
        {
            return;
        }

        _isLoaded = true;
        ObserveBlueprintVisibility(null);
        if (!_layoutService.TryRestore(DockManager, PanelContent))
        {
            BuildDefaultLayout();
            _viewModel.CommandConsole.WriteInfo("Layout", "未找到兼容的已保存布局，已加载默认停靠布局。");
        }
        else if (string.Equals(_layoutService.LastRecoverySource, "backup", StringComparison.Ordinal))
        {
            _viewModel.CommandConsole.WriteWarning("Layout", "主布局不可用，已从备份恢复 AvalonDock 布局。");
        }

        ApplyDockingCapabilities();
        ObserveBlueprintVisibility(FindContent(BlueprintContentId) as LayoutAnchorable);
        SyncActiveWorkflowContextFromLayout();
        SyncActiveToolContextFromLayout();
        RefreshPanelContents();
        SyncToggleMenuStates();
        if (_viewModel.IsNewProjectDialogOpen)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, FocusNewProjectDialog);
        }
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_isShutdownComplete)
        {
            return;
        }

        e.Cancel = true;
        if (_isShutdownInProgress)
        {
            return;
        }

        _isShutdownInProgress = true;
        try
        {
            try
            {
                _layoutService.Save(DockManager);
            }
            catch (Exception exception)
            {
                Debug.WriteLine($"AvalonDock 布局保存失败：{exception}");
                _viewModel.CommandConsole.WriteError(
                    "Layout",
                    $"AvalonDock 布局保存失败：{exception.Message}",
                    exception);
            }

            ObserveBlueprintVisibility(null);
            foreach (var disposableView in _sharedPanelViews.Values.OfType<IDisposable>())
            {
                try
                {
                    disposableView.Dispose();
                }
                catch (Exception exception)
                {
                    Debug.WriteLine($"停靠视图关闭失败：{exception}");
                }
            }

            await _viewModel.ShutdownAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"GAssetForge 异步关闭失败：{exception}");
        }
        finally
        {
            _viewModel.LayoutResetRequested -= OnLayoutResetRequested;
            _viewModel.PanelShowRequested -= OnPanelShowRequested;
            _viewModel.PanelToggleRequested -= OnPanelToggleRequested;
            _viewModel.WorkflowDocumentRequested -= OnWorkflowDocumentRequested;
            _viewModel.PropertyChanged -= OnShellPropertyChanged;
            DockManager.DocumentClosed -= OnDocumentClosed;
            DockManager.ActiveContentChanged -= OnDockManagerActiveContentChanged;
            ContentRendered -= OnContentRendered;
            PreviewKeyDown -= OnPreviewKeyDown;
            _isShutdownComplete = true;
            _isShutdownInProgress = false;
            if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
            {
                _ = Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(Close));
            }
        }
    }

    private void OnContentRendered(object? sender, EventArgs e)
    {
        if (_viewModel.IsNewProjectDialogOpen)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, FocusNewProjectDialog);
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_viewModel.IsNewProjectDialogOpen)
        {
            return;
        }

        if (e.Key == Key.Escape)
        {
            if (_viewModel.CloseNewProjectCommand.CanExecute(null))
            {
                _viewModel.CloseNewProjectCommand.Execute(null);
            }

            e.Handled = true;
            return;
        }

        if (Keyboard.FocusedElement is DependencyObject focused && NewProjectOverlay.IsAncestorOf(focused))
        {
            return;
        }

        FocusNewProjectDialog();
        if (e.Key is Key.Tab or Key.Enter or Key.Space)
        {
            e.Handled = true;
        }
    }

    private void OnLayoutResetRequested(object? sender, EventArgs e)
    {
        try
        {
            _layoutService.Clear();
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"AvalonDock 布局清理失败：{exception}");
            _viewModel.CommandConsole.WriteError("Layout", $"AvalonDock 布局清理失败：{exception.Message}", exception);
        }

        BuildDefaultLayout();
        RefreshPanelContents();
        SyncToggleMenuStates();
    }

    private void OnPanelShowRequested(object? sender, string contentId)
    {
        ShowPanel(contentId);
    }

    private void OnPanelToggleRequested(object? sender, string contentId)
    {
        var content = FindContent(contentId);
        if (content is null)
        {
            ShowPanel(contentId);
            return;
        }

        if (content is LayoutDocument visibleDocument)
        {
            visibleDocument.Close();
            SyncToggleMenuStates();
            return;
        }

        if (content is LayoutAnchorable { IsHidden: false, IsVisible: true } visibleAnchorable)
        {
            visibleAnchorable.Hide();
            SyncToggleMenuStates();
            return;
        }

        ShowPanel(contentId);
    }

    private void ShowPanel(string contentId)
    {
        var content = FindContent(contentId) ?? RestoreMissingContent(contentId);
        if (content is null)
        {
            return;
        }

        if (content is LayoutAnchorable { IsHidden: true } hiddenAnchorable)
        {
            hiddenAnchorable.Show();
        }

        if (content is LayoutAnchorable anchorable)
        {
            anchorable.IsVisible = true;
        }

        content.IsSelected = true;
        content.IsActive = true;
        DockManager.ActiveContent = content.Content;
        ActivateFloatingWindow(content);
        SyncToggleMenuStates();
        if (string.Equals(contentId, CommandConsoleContentId, StringComparison.Ordinal) &&
            _sharedPanelViews.TryGetValue(CommandConsoleContentId, out var consolePanel) &&
            consolePanel is CommandConsoleView consoleView)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Input, consoleView.FocusCommandInput);
        }
    }

    private void ViewMenu_OnSubmenuOpened(object sender, RoutedEventArgs e) =>
        SyncToggleMenuStates();

    private void SyncToggleMenuStates()
    {
        ShowUiWorkspaceMenuItem.IsChecked = FindContent(CanvasContentId) is LayoutDocument;
        ShowTilemapWorkspaceMenuItem.IsChecked = FindContent(TilemapWorkspaceContentId) is LayoutDocument;
        ShowCardDesignWorkspaceMenuItem.IsChecked = FindContent(CardDesignWorkspaceContentId) is LayoutDocument;
        ShowParallelPerspectiveWorkspaceMenuItem.IsChecked =
            FindContent(ParallelPerspectiveWorkspaceContentId) is LayoutDocument;
        var styleLibrary = FindContent(StyleLibraryContentId) as LayoutAnchorable;
        ShowStyleLibraryMenuItem.IsChecked = styleLibrary is { IsHidden: false, IsVisible: true };
        var assetGeneration = FindContent(AssetGenerationContentId) as LayoutAnchorable;
        ShowAssetGenerationMenuItem.IsChecked = assetGeneration is { IsHidden: false, IsVisible: true };
        var backgroundRemoval = FindContent(BackgroundRemovalContentId) as LayoutAnchorable;
        ShowBackgroundRemovalMenuItem.IsChecked = backgroundRemoval is { IsHidden: false, IsVisible: true };
        var pixelAlignment = FindContent(PixelAlignmentContentId) as LayoutAnchorable;
        ShowPixelAlignmentMenuItem.IsChecked = pixelAlignment is { IsHidden: false, IsVisible: true };
        var blueprint = FindContent(BlueprintContentId) as LayoutAnchorable;
        ShowBlueprintMenuItem.IsChecked = blueprint is { IsHidden: false, IsVisible: true };
        var commandConsole = FindContent(CommandConsoleContentId) as LayoutAnchorable;
        ShowCommandConsoleMenuItem.IsChecked = commandConsole is { IsHidden: false, IsVisible: true };
        var agentConnection = FindContent(AgentConnectionContentId) as LayoutAnchorable;
        ShowAgentConnectionMenuItem.IsChecked = agentConnection is { IsHidden: false, IsVisible: true };
    }

    private void OnWorkflowDocumentRequested(WorkflowDefinition workflow)
    {
        ShowPanel(GetWorkflowDocumentContentId(workflow));
    }

    private void OnDocumentClosed(object? sender, DocumentClosedEventArgs e)
    {
        if (!IsWorkflowDocument(e.Document.ContentId))
        {
            return;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            EnsurePrimaryDocumentPane();
            SyncActiveWorkflowContextFromLayout();
            SyncToggleMenuStates();
            DockManager.Focus();
        });
    }

    private LayoutContent? RestoreMissingContent(string contentId)
    {
        if (!_panelHosts.ContainsKey(contentId) && !_documentHosts.ContainsKey(contentId))
        {
            return null;
        }

        try
        {
            if (TryGetWorkflowForDocument(contentId, out var workflow))
            {
                var document = CreateWorkflowDocument(workflow);
                var documentPane = EnsurePrimaryDocumentPane();
                if (documentPane is null)
                {
                    return null;
                }

                documentPane.Children.Add(document);
                return document;
            }

            var restored = CreateAnchorable(contentId);
            restored.AddToLayout(DockManager, contentId switch
            {
                ToolsContentId => AnchorableShowStrategy.Left,
                ProjectContentId => AnchorableShowStrategy.Bottom,
                StyleLibraryContentId => AnchorableShowStrategy.Bottom,
                AssetGenerationContentId => AnchorableShowStrategy.Bottom,
                BackgroundRemovalContentId => AnchorableShowStrategy.Bottom,
                PixelAlignmentContentId => AnchorableShowStrategy.Bottom,
                BlueprintContentId => AnchorableShowStrategy.Bottom,
                AgentConnectionContentId => AnchorableShowStrategy.Bottom,
                CommandConsoleContentId => AnchorableShowStrategy.Bottom,
                _ => AnchorableShowStrategy.Right
            });
            if (string.Equals(contentId, BlueprintContentId, StringComparison.Ordinal))
            {
                ObserveBlueprintVisibility(restored);
            }

            return restored;
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"恢复停靠面板 {contentId} 失败：{exception}");
            _viewModel.CommandConsole.WriteError(
                "Layout",
                $"恢复停靠面板 {contentId} 失败：{exception.Message}",
                exception);
            return null;
        }
    }

    private void ApplyDockingCapabilities()
    {
        foreach (var documentPane in DockManager.Layout.Descendents().OfType<LayoutDocumentPane>())
        {
            documentPane.ShowHeader = true;
        }

        var contents = DockManager.Layout.Descendents()
            .OfType<LayoutContent>()
            .Concat(DockManager.Layout.Hidden)
            .Distinct();

        foreach (var content in contents)
        {
            if (content is LayoutDocument document && IsWorkflowDocument(document.ContentId))
            {
                document.CanClose = true;
                document.CanFloat = true;
                document.CanMove = true;
                continue;
            }

            if (content is LayoutAnchorable anchorable &&
                IsKnownPanel(anchorable.ContentId))
            {
                anchorable.CanClose = false;
                anchorable.CanHide = true;
                anchorable.CanFloat = true;
                anchorable.CanMove = true;
                anchorable.CanAutoHide = true;
                anchorable.CanDockAsTabbedDocument = true;
            }
        }
    }

    private LayoutDocumentPane? EnsurePrimaryDocumentPane()
    {
        var existingPane = DockManager.Layout.Descendents()
            .OfType<LayoutDocumentPane>()
            .FirstOrDefault(pane => !pane.IsHostedInFloatingWindow);
        if (existingPane is not null)
        {
            existingPane.ShowHeader = true;
            return existingPane;
        }

        var rootPanel = DockManager.Layout.RootPanel;
        if (rootPanel is null)
        {
            return null;
        }

        var documentPane = new LayoutDocumentPane
        {
            ShowHeader = true
        };
        var centerPanel = rootPanel.Children
            .OfType<LayoutPanel>()
            .FirstOrDefault(panel => panel.Descendents()
                .OfType<LayoutContent>()
                .Any(content => content.ContentId is ProjectContentId or StyleLibraryContentId or AssetGenerationContentId or BackgroundRemovalContentId or PixelAlignmentContentId or BlueprintContentId or AgentConnectionContentId or CommandConsoleContentId));
        centerPanel ??= rootPanel.Children.Count > 1
            ? rootPanel.Children[1] as LayoutPanel
            : null;

        if (centerPanel is not null)
        {
            centerPanel.Children.Insert(0, documentPane);
            return documentPane;
        }

        var insertionIndex = rootPanel.Orientation == Orientation.Horizontal && rootPanel.Children.Count > 0
            ? Math.Min(1, rootPanel.Children.Count)
            : 0;
        rootPanel.Children.Insert(insertionIndex, documentPane);
        return documentPane;
    }

    private void ActivateFloatingWindow(LayoutContent content)
    {
        var floatingWindow = DockManager.FloatingWindows.FirstOrDefault(window =>
            ReferenceEquals(window.Model, content) ||
            window.Model is ILayoutContainer container &&
            container.Descendents().Any(element => ReferenceEquals(element, content)));
        if (floatingWindow is null)
        {
            return;
        }

        if (floatingWindow.WindowState == WindowState.Minimized)
        {
            floatingWindow.WindowState = WindowState.Normal;
        }

        if (!floatingWindow.IsVisible)
        {
            floatingWindow.Show();
        }

        floatingWindow.Activate();
    }

    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ShellViewModel.CurrentWorkspace) or nameof(ShellViewModel.CurrentWorkflow))
        {
            RefreshPanelContents();
        }

        if (e.PropertyName == nameof(ShellViewModel.CurrentWorkflow))
        {
            UpdateLayoutTitles();
        }

        if (e.PropertyName == nameof(ShellViewModel.IsNewProjectDialogOpen))
        {
            if (_viewModel.IsNewProjectDialogOpen)
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, FocusNewProjectDialog);
            }
            else
            {
                DockManager.Focus();
            }
        }
    }

    private void OnDockManagerActiveContentChanged(object? sender, EventArgs e)
    {
        var contentId = DockManager.Layout.ActiveContent?.ContentId;
        if (TryGetWorkflowForDocument(contentId, out var workflow))
        {
            _activeToolContextContentId = contentId!;
            _viewModel.ActivateWorkspaceDocument(workflow);
            RefreshActiveToolPalette();
            return;
        }

        if (string.Equals(contentId, BlueprintContentId, StringComparison.Ordinal))
        {
            _activeToolContextContentId = BlueprintContentId;
            RefreshActiveToolPalette();
            return;
        }

        if (contentId is StyleLibraryContentId or AssetGenerationContentId or
            BackgroundRemovalContentId or PixelAlignmentContentId or AgentConnectionContentId)
        {
            _activeToolContextContentId = GetWorkflowDocumentContentId(_viewModel.CurrentWorkflow);
            RefreshActiveToolPalette();
            return;
        }

        if (string.Equals(_activeToolContextContentId, BlueprintContentId, StringComparison.Ordinal) &&
            FindContent(BlueprintContentId) is LayoutAnchorable { IsHidden: true })
        {
            _activeToolContextContentId = CanvasContentId;
            RefreshActiveToolPalette();
        }
    }

    private void ObserveBlueprintVisibility(LayoutAnchorable? anchorable)
    {
        if (ReferenceEquals(_observedBlueprintAnchorable, anchorable))
        {
            return;
        }

        if (_observedBlueprintAnchorable is not null)
        {
            _observedBlueprintAnchorable.IsVisibleChanged -= OnBlueprintVisibilityChanged;
        }

        _observedBlueprintAnchorable = anchorable;
        if (_observedBlueprintAnchorable is not null)
        {
            _observedBlueprintAnchorable.IsVisibleChanged += OnBlueprintVisibilityChanged;
        }
    }

    private void OnBlueprintVisibilityChanged(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(sender, _observedBlueprintAnchorable) ||
            _observedBlueprintAnchorable is null)
        {
            return;
        }

        if (_observedBlueprintAnchorable.IsVisible)
        {
            if (string.Equals(
                    DockManager.Layout.ActiveContent?.ContentId,
                    BlueprintContentId,
                    StringComparison.Ordinal))
            {
                _activeToolContextContentId = BlueprintContentId;
                RefreshActiveToolPalette();
            }

            return;
        }

        if (string.Equals(_activeToolContextContentId, BlueprintContentId, StringComparison.Ordinal))
        {
            _activeToolContextContentId = GetWorkflowDocumentContentId(_viewModel.CurrentWorkflow);
            RefreshActiveToolPalette();
        }
    }

    private void RefreshActiveToolPalette()
    {
        if (!_panelHosts.TryGetValue(ToolsContentId, out var host))
        {
            return;
        }

        host.Content = string.Equals(
                _activeToolContextContentId,
                BlueprintContentId,
                StringComparison.Ordinal)
            ? _blueprintToolPaletteView
            : _workflowToolPaletteView;
    }

    private void SyncActiveToolContextFromLayout()
    {
        _activeToolContextContentId = string.Equals(
            DockManager.Layout.ActiveContent?.ContentId,
            BlueprintContentId,
            StringComparison.Ordinal)
            ? BlueprintContentId
            : GetWorkflowDocumentContentId(_viewModel.CurrentWorkflow);
    }

    private void SyncActiveWorkflowContextFromLayout()
    {
        var contentId = DockManager.Layout.ActiveContent?.ContentId;
        if (!TryGetWorkflowForDocument(contentId, out var workflow))
        {
            var selectedDocument = DockManager.Layout.Descendents()
                .OfType<LayoutDocument>()
                .FirstOrDefault(document => document.IsSelected && IsWorkflowDocument(document.ContentId));
            if (selectedDocument is null ||
                !TryGetWorkflowForDocument(selectedDocument.ContentId, out workflow))
            {
                return;
            }
        }

        _viewModel.ActivateWorkspaceDocument(workflow);
    }

    private LayoutContent? FindContent(string contentId)
    {
        var visible = DockManager.Layout.Descendents()
            .OfType<LayoutContent>()
            .FirstOrDefault(content => string.Equals(content.ContentId, contentId, StringComparison.Ordinal));
        if (visible is not null)
        {
            return visible;
        }

        return DockManager.Layout.Hidden
            .FirstOrDefault(content => string.Equals(content.ContentId, contentId, StringComparison.Ordinal));
    }

    private void UpdateLayoutTitles()
    {
        foreach (var workflow in WorkflowCatalog.Dockable)
        {
            var document = FindContent(GetWorkflowDocumentContentId(workflow));
            if (document is not null)
            {
                document.Title = GetWorkflowDocumentTitle(workflow);
            }
        }
    }

    private string GetWorkflowDocumentTitle(WorkflowDefinition workflow) => workflow.Kind switch
    {
        WorkflowKind.UiDesign => _viewModel.EditorWorkspace.DocumentTitle,
        WorkflowKind.Tilemap => workflow.Label,
        WorkflowKind.CardDesign => workflow.Label,
        WorkflowKind.ParallelPerspective => workflow.Label,
        _ => "业务窗口"
    };

    private static string GetWorkflowDocumentContentId(WorkflowDefinition workflow) => workflow.Kind switch
    {
        WorkflowKind.UiDesign => CanvasContentId,
        WorkflowKind.Tilemap => TilemapWorkspaceContentId,
        WorkflowKind.CardDesign => CardDesignWorkspaceContentId,
        WorkflowKind.ParallelPerspective => ParallelPerspectiveWorkspaceContentId,
        _ => throw new ArgumentException("测试模板不注册为正式业务窗口。", nameof(workflow))
    };

    private static bool TryGetWorkflowForDocument(
        string? contentId,
        out WorkflowDefinition workflow)
    {
        var kind = contentId switch
        {
            CanvasContentId => WorkflowKind.UiDesign,
            TilemapWorkspaceContentId => WorkflowKind.Tilemap,
            CardDesignWorkspaceContentId => WorkflowKind.CardDesign,
            ParallelPerspectiveWorkspaceContentId => WorkflowKind.ParallelPerspective,
            _ => (WorkflowKind?)null
        };
        workflow = kind is null
            ? WorkflowCatalog.UiDesign
            : WorkflowCatalog.Dockable.Single(candidate => candidate.Kind == kind.Value);
        return kind is not null;
    }

    private static bool IsWorkflowDocument(string? contentId) =>
        TryGetWorkflowForDocument(contentId, out _);

    private static string GetPanelTitle(string contentId) => contentId switch
    {
        ToolsContentId => "工具",
        CanvasContentId => "页面 UI 设计工具",
        TilemapWorkspaceContentId => "Tilemap 制作",
        CardDesignWorkspaceContentId => "卡牌设计",
        ParallelPerspectiveWorkspaceContentId => "平行透视角物体制作",
        PropertiesContentId => "属性",
        LayersContentId => "图层",
        ProjectContentId => "项目",
        StyleLibraryContentId => "美术风格素材库",
        AssetGenerationContentId => "素材生成",
        BackgroundRemovalContentId => "背景剔除工具",
        PixelAlignmentContentId => "像素点对齐工具",
        BlueprintContentId => "蓝图窗口",
        AgentConnectionContentId => "MCP Agent 接入",
        CommandConsoleContentId => "指令日志",
        _ => "工作区"
    };

    private static bool IsKnownPanel(string? contentId) => contentId is
        ToolsContentId or PropertiesContentId or LayersContentId or ProjectContentId or StyleLibraryContentId or AssetGenerationContentId or BackgroundRemovalContentId or PixelAlignmentContentId or BlueprintContentId or AgentConnectionContentId or CommandConsoleContentId;

    private void FocusNewProjectDialog()
    {
        if (!_viewModel.IsNewProjectDialogOpen)
        {
            return;
        }

        if (!IsActive)
        {
            Activate();
        }

        NewProjectOverlay.Focus();
        NewProjectNameTextBox.SelectAll();
        FocusManager.SetFocusedElement(NewProjectOverlay, NewProjectNameTextBox);
        NewProjectNameTextBox.Focus();
        Keyboard.Focus(NewProjectNameTextBox);
    }
}
