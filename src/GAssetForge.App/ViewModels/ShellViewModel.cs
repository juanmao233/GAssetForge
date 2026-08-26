using AvalonDock.Themes;
using GAssetForge.App.Infrastructure;
using GAssetForge.App.Services;
using GAssetForge.App.Services.AgentRuntime;
using GAssetForge.Core.Generation;
using GAssetForge.Core.Imaging;
using GAssetForge.Core.Workflows;
using System.ComponentModel;
using System.IO;
using System.Windows.Input;
using System.Windows.Threading;

namespace GAssetForge.App.ViewModels;

public sealed class ShellViewModel : ObservableObject
{
    private readonly ThemeService _themeService;
    private readonly UserSettingsService _settingsService;
    private readonly UserSettingsModel _settings;
    private readonly ProjectWorkspaceService _projectWorkspaceService;
    private readonly IProjectLocationPicker _projectLocationPicker;
    private readonly WorkbenchConsoleService _workbenchConsoleService;
    private readonly IImageProcessingPreviewCache _imageProcessingPreviewCache;
    private readonly IGenerationTaskStore _generationTaskStore;
    private readonly GenerationBatchTurnReconciler _generationBatchTurnReconciler;
    private readonly Dispatcher _dispatcher;
    private readonly object _shutdownLock = new();
    private readonly SemaphoreSlim _dispatchRecoveryGate = new(1, 1);
    private readonly AsyncRelayCommand _createNewProjectCommand;
    private readonly RelayCommand _closeNewProjectCommand;
    private GenerationBatchDispatchCoordinator _agentDispatchCoordinator = null!;
    private WorkflowDefinition _currentWorkflow;
    private object _currentWorkspace;
    private WorkflowPlaceholderViewModel? _placeholderWorkspace;
    private ProjectWorkspace? _currentProject;
    private WorkflowDefinition _selectedNewProjectWorkflow;
    private string _newProjectName = "新项目";
    private string _newProjectParentPath = string.Empty;
    private string _newProjectValidationMessage = string.Empty;
    private bool _isNewProjectDialogOpen;
    private bool _isCreatingNewProject;
    private bool _isApplyingProjectSettings;
    private bool _isShutDown;
    private Task? _shutdownTask;
    private readonly List<GatedCommand> _uiCommands = [];
    private readonly Dictionary<string, DateTimeOffset> _agentActivityStarts = new(StringComparer.Ordinal);

    public ShellViewModel(
        ThemeService themeService,
        UserSettingsService settingsService,
        EditorWorkspaceViewModel editorWorkspace,
        IProjectLocationPicker? projectLocationPicker = null,
        IAssetGenerationSpreadsheetPicker? assetGenerationSpreadsheetPicker = null,
        IAssetGenerationSpreadsheetReader? assetGenerationSpreadsheetReader = null)
    {
        _themeService = themeService;
        _settingsService = settingsService;
        _projectWorkspaceService = new ProjectWorkspaceService();
        _projectLocationPicker = projectLocationPicker ?? new DesktopProjectLocationPicker();
        _dispatcher = Dispatcher.CurrentDispatcher;
        EditorWorkspace = editorWorkspace;
        _settings = settingsService.Load();
        _workbenchConsoleService = new WorkbenchConsoleService(settingsService.Paths);
        _imageProcessingPreviewCache = CreateImageProcessingPreviewCache(settingsService.Paths);
        var colorPickerService = new DesktopColorPickerService();
        CommandConsole = new CommandConsoleViewModel(_workbenchConsoleService);
        Blueprint = new BlueprintViewModel(
            new JsonBlueprintRepository(settingsService.Paths),
            new BlueprintRunWorkspaceService(settingsService.Paths),
            _workbenchConsoleService);
        var startupSettingsChanged = false;
        ProjectWorkspace? initialProject = null;
        if (!string.IsNullOrWhiteSpace(_settings.LastProjectPath) &&
            _projectWorkspaceService.TryLoad(
                _settings.LastProjectPath,
                out var projectManifest,
                out var projectPaths,
                out _))
        {
            initialProject = new ProjectWorkspace(projectManifest, projectPaths);
        }
        else
        {
            startupSettingsChanged = !string.IsNullOrWhiteSpace(_settings.LastProjectPath);
            _settings.LastProjectPath = null;
        }

        var initialWorkflow = WorkflowCatalog.FindDockable(
            initialProject?.Manifest.WorkflowKey ?? _settings.ActiveWorkflowKey);
        if (initialProject is not null)
        {
            if (!Blueprint.Activate(initialProject.Paths.RootDirectory, initialWorkflow.Key))
            {
                startupSettingsChanged |= ClearRejectedProjectAssetPaths(initialProject);
                initialProject = null;
                _settings.LastProjectPath = null;
                startupSettingsChanged = true;
                initialWorkflow = WorkflowCatalog.FindDockable(_settings.ActiveWorkflowKey);
            }
            else if (!AssetRootService.PathEquals(
                         EditorWorkspace.ProjectRoot,
                         initialProject.Paths.RootDirectory))
            {
                try
                {
                    EditorWorkspace.SwitchProject(initialProject.Paths.RootDirectory);
                }
                catch (Exception exception)
                {
                    _workbenchConsoleService.WriteError(
                        "Project",
                        $"启动项目未能完整激活，已回退到本地工作区：{exception.Message}",
                        exception);
                    startupSettingsChanged |= ClearRejectedProjectAssetPaths(initialProject);
                    initialProject = null;
                    _settings.LastProjectPath = null;
                    startupSettingsChanged = true;
                    initialWorkflow = WorkflowCatalog.FindDockable(_settings.ActiveWorkflowKey);
                }
            }
        }

        if (initialProject is null && !Blueprint.Activate(null, initialWorkflow.Key))
        {
            throw new InvalidOperationException("无法激活启动项目或本地蓝图工作区。");
        }

        _currentProject = initialProject;

        var projectTilemap = _currentProject?.Manifest.Tilemap;
        TilemapWorkspace = new TilemapWorkspaceViewModel
        {
            GridUnitSize = projectTilemap?.GridUnitSize ?? _settings.TilemapGridUnitSize,
            ArtImageWidth = projectTilemap?.ArtImageWidth ?? _settings.TilemapArtImageWidth,
            ArtImageHeight = projectTilemap?.ArtImageHeight ?? _settings.TilemapArtImageHeight,
            DiamondAngle = projectTilemap?.DiamondAngle ?? _settings.TilemapDiamondAngle,
            RotationAngle = projectTilemap?.RotationAngle ?? _settings.TilemapRotationAngle
        };
        ArtStyleLibrary = new ArtStyleLibraryViewModel(settingsService.Paths);
        BackgroundRemoval = new BackgroundRemovalViewModel(
            new ImageSharpBackgroundRemovalService(),
            new DesktopBackgroundRemovalInputPicker(),
            new DesktopBackgroundRemovalOutputPicker(),
            _settings.BackgroundRemovalPresetKey,
            _settings.BackgroundRemovalCustomColorHex,
            _settings.BackgroundRemovalTransparentThreshold,
            _settings.BackgroundRemovalOpaqueThreshold,
            _settings.BackgroundRemovalEdgeContract,
            _settings.BackgroundRemovalEdgeFeather,
            _settings.BackgroundRemovalDespill,
            _settings.BackgroundRemovalEdgeConnectedOnly,
            _settings.BackgroundRemovalUseFakeTransparencyGrid,
            _settings.BackgroundRemovalCheckerColorAHex,
            _settings.BackgroundRemovalCheckerColorBHex,
            _settings.BackgroundRemovalEnableCheckerGridAlignment,
            _settings.BackgroundRemovalCheckerCellSize,
            _settings.BackgroundRemovalCheckerOffsetX,
            _settings.BackgroundRemovalCheckerOffsetY,
            _settings.BackgroundRemovalCheckerBoundaryTolerance,
            _settings.BackgroundRemovalLastInputDirectory,
            _settings.BackgroundRemovalLastOutputDirectory,
            _workbenchConsoleService,
            previewCache: _imageProcessingPreviewCache,
            colorPickerService: colorPickerService);
        BackgroundRemoval.SettingsChanged += OnBackgroundRemovalSettingsChanged;
        PixelAlignment = new PixelAlignmentViewModel(
            new ImageSharpPixelAlignmentService(),
            new DesktopPixelAlignmentInputPicker(),
            new DesktopPixelAlignmentOutputPicker(),
            _settings.PixelAlignmentMinimumUpscaleFactor,
            _settings.PixelAlignmentColumns,
            _settings.PixelAlignmentRows,
            samplingMode: _settings.PixelAlignmentSamplingMode,
            showGrid: _settings.PixelAlignmentShowGrid,
            enableAutomaticColorIndex: _settings.PixelAlignmentEnableAutomaticColorIndex,
            maximumIndexedColors: _settings.PixelAlignmentMaximumIndexedColors,
            enableAspectRatioCorrection: _settings.PixelAlignmentEnableAspectRatioCorrection,
            targetAspectRatioA: _settings.PixelAlignmentTargetAspectRatioA,
            targetAspectRatioB: _settings.PixelAlignmentTargetAspectRatioB,
            enableRotationCorrection: _settings.PixelAlignmentEnableRotationCorrection,
            rotationDegrees: _settings.PixelAlignmentRotationDegrees,
            enablePerspectiveCorrection: _settings.PixelAlignmentEnablePerspectiveCorrection,
            targetEdgeAngleDegrees: _settings.PixelAlignmentTargetEdgeAngleDegrees,
            lastInputDirectory: _settings.PixelAlignmentLastInputDirectory,
            lastOutputDirectory: _settings.PixelAlignmentLastOutputDirectory,
            workbenchLogSink: _workbenchConsoleService,
            previewCache: _imageProcessingPreviewCache);
        PixelAlignment.SettingsChanged += OnPixelAlignmentSettingsChanged;
        ImageOutline = new ImageOutlineViewModel(
            new ImageSharpImageOutlineService(),
            new DesktopImageOutlineInputPicker(),
            new DesktopImageOutlineOutputPicker(),
            _settings.ImageOutlinePlacement,
            _settings.ImageOutlineWidth,
            _settings.ImageOutlineColorHex,
            _settings.ImageOutlineAlphaThreshold,
            _settings.ImageOutlineExpandCanvas,
            _settings.ImageOutlineLastInputDirectory,
            _settings.ImageOutlineLastOutputDirectory,
            _workbenchConsoleService,
            previewCache: _imageProcessingPreviewCache,
            colorPickerService: colorPickerService);
        ImageOutline.SettingsChanged += OnImageOutlineSettingsChanged;
        IconDesign = new IconDesignViewModel(
            initialSettings: new IconDesignSettingsSnapshot(
                _settings.IconDesignModeKey,
                _settings.IconDesignImageWidth,
                _settings.IconDesignImageHeight,
                _settings.IconDesignCameraAzimuthDegrees,
                _settings.IconDesignCameraElevationDegrees,
                _settings.IconDesignCameraRollDegrees,
                _settings.IconDesignCameraDistance,
                _settings.IconDesignFocalLengthMillimeters));
        IconDesign.SettingsChanged += OnIconDesignSettingsChanged;
        var configuredAssetRoot = _currentProject?.Paths.AssetDirectory ?? _settings.AssetRootPath;
        var configuredCurrentPath = _currentProject is null
            ? _settings.AssetCurrentPath
            : AssetRootService.IsWithinRoot(
                _currentProject.Paths.AssetDirectory,
                _settings.AssetCurrentPath ?? string.Empty)
                ? _settings.AssetCurrentPath
                : _currentProject.Paths.GeneratedAssetDirectory;
        ProjectBrowser = new ProjectBrowserViewModel(
            settingsService.Paths,
            new DesktopFileDialogService(),
            editorWorkspace,
            configuredAssetRoot,
            configuredCurrentPath);
        ProjectBrowser.SetRootSelectionLock(_currentProject is not null);
        ProjectBrowser.ConfiguredRootPathChanged += OnConfiguredAssetRootPathChanged;
        ProjectBrowser.CurrentPathChanged += OnAssetCurrentPathChanged;
        var agentWorkingDirectory = ResolveAgentWorkingDirectory();
        var clearedStaleThread = AgentSessionBookmarkService.ClearIfWorkingDirectoryChanged(
            _settings,
            agentWorkingDirectory);
        var resumeThreadId = AgentSessionBookmarkService.ResolveResumeThreadId(
            _settings,
            agentWorkingDirectory);
        AgentConnection = new AgentConnectionViewModel(
            new McpServerProcessService(settingsService.Paths),
            new CodexAgentRuntimeService(
                string.IsNullOrWhiteSpace(_settings.CodexExecutablePath)
                    ? "codex"
                    : _settings.CodexExecutablePath),
            _settings.McpAgentKey,
            _settings.McpPort,
            agentWorkingDirectory,
            resumeThreadId,
            _workbenchConsoleService,
            _settings.CodexExecutablePath,
            new DesktopCodexExecutablePicker());
        AgentConnection.SettingsChanged += OnAgentConnectionSettingsChanged;
        AgentConnection.SessionChanged += OnAgentSessionChanged;
        AgentConnection.AgentActivityReceived += OnAgentActivityForStructuredLog;
        AgentConnection.PropertyChanged += OnAgentConnectionPropertyChanged;
        ArtStyleLibrary.ConfigurePromptAnalysisService(
            new AgentArtStylePromptAnalysisService(
                new AgentConnectionArtStylePromptAgentClient(AgentConnection)));
        _generationTaskStore = new JsonGenerationTaskStore(settingsService.Paths.GenerationDirectory);
        _generationBatchTurnReconciler = new GenerationBatchTurnReconciler(_generationTaskStore);
        AssetGeneration = new AssetGenerationViewModel(
            settingsService.Paths,
            _generationTaskStore,
            ArtStyleLibrary,
            TilemapWorkspace,
            ProjectBrowser,
            AgentConnection,
            new DesktopAssetGenerationOutputPicker(),
            BackgroundRemoval,
            _settings.AssetGenerationStepKey,
            _settings.AssetGenerationContentPrompt,
            _settings.AssetGenerationNegativePrompt,
            _settings.AssetGenerationOutputPath,
            _settings.AssetGenerationRows,
            _settings.AssetGenerationMaxParallelism,
            _settings.AssetGenerationAutoRetryEnabled,
            _settings.AssetGenerationAutoRetryCount,
            _settings.AssetGenerationPreferUnifiedStylePrompt,
            spreadsheetPicker: assetGenerationSpreadsheetPicker,
            spreadsheetReader: assetGenerationSpreadsheetReader,
            iconDesign: IconDesign);
        AssetGeneration.SettingsChanged += OnAssetGenerationSettingsChanged;
        AssetGeneration.StyleLibraryRequested += OnAssetGenerationStyleLibraryRequested;
        AssetGeneration.AgentConnectionRequested += OnAssetGenerationAgentConnectionRequested;
        AssetGeneration.BackgroundRemovalRequested += OnAssetGenerationBackgroundRemovalRequested;
        AssetGeneration.BatchPublished += OnGenerationBatchPublished;
        _agentDispatchCoordinator = CreateAgentDispatchCoordinator();
        _currentWorkflow = initialWorkflow;
        ProjectBrowser.SetPrefabLibraryAvailability(_currentWorkflow.Kind == WorkflowKind.UiDesign);
        (_currentWorkspace, _placeholderWorkspace) = CreateWorkspaceState(_currentWorkflow);
        _selectedNewProjectWorkflow = _currentWorkflow;
        _newProjectParentPath = ResolveInitialProjectParentPath();
        _newProjectName = CreateAvailableProjectName(_newProjectParentPath);
        _isNewProjectDialogOpen = _currentProject is null && !_settings.HasCompletedWorkflowSelection;

        OpenNewProjectCommand = new RelayCommand(OpenNewProject);
        _closeNewProjectCommand = new RelayCommand(
            () => IsNewProjectDialogOpen = false,
            () => !IsCreatingNewProject);
        CloseNewProjectCommand = _closeNewProjectCommand;
        ChooseNewProjectLocationCommand = new RelayCommand(ChooseNewProjectLocation);
        _createNewProjectCommand = new AsyncRelayCommand(CreateNewProjectAsync, CanCreateNewProject);
        CreateNewProjectCommand = _createNewProjectCommand;
        SelectWorkflowCommand = new RelayCommand<WorkflowDefinition>(workflow =>
        {
            if (workflow is not null)
            {
                SelectWorkflow(workflow);
            }
        });
        ToggleThemeCommand = new RelayCommand(() =>
            SetTheme(CurrentTheme == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark));
        UseDarkThemeCommand = new RelayCommand(() => SetTheme(AppTheme.Dark));
        UseLightThemeCommand = new RelayCommand(() => SetTheme(AppTheme.Light));
        ResetLayoutCommand = new RelayCommand(() => LayoutResetRequested?.Invoke(this, EventArgs.Empty));
        ShowPanelCommand = new RelayCommand<string>(contentId =>
        {
            if (!string.IsNullOrWhiteSpace(contentId))
            {
                PanelShowRequested?.Invoke(this, contentId);
            }
        });
        TogglePanelCommand = new RelayCommand<string>(contentId =>
        {
            if (!string.IsNullOrWhiteSpace(contentId))
            {
                PanelToggleRequested?.Invoke(this, contentId);
            }
        });
        UiSaveCommand = Gate(EditorWorkspace.SaveCommand);
        UiImportCommand = Gate(EditorWorkspace.ImportCommand);
        UiExportCommand = Gate(EditorWorkspace.ExportCommand);
        UiUndoCommand = Gate(EditorWorkspace.UndoCommand);
        UiRedoCommand = Gate(EditorWorkspace.RedoCommand);
        UiDeleteCommand = Gate(EditorWorkspace.DeleteSelectedCommand);
        UiZoomInCommand = Gate(EditorWorkspace.ZoomInCommand);
        UiZoomOutCommand = Gate(EditorWorkspace.ZoomOutCommand);
        UiResetZoomCommand = Gate(EditorWorkspace.ResetZoomCommand);
        RegisterConsoleCommands();
        EditorWorkspace.PropertyChanged += OnEditorWorkspacePropertyChanged;
        TilemapWorkspace.PropertyChanged += OnTilemapWorkspacePropertyChanged;

        _themeService.Apply(_settings.Theme);
        RefreshNewProjectValidation();
        if (clearedStaleThread || startupSettingsChanged)
        {
            SaveSettings();
        }

        CommandConsole.WriteInfo(
            "Workbench",
            $"GAssetForge 已启动 · 项目：{CurrentProjectDisplayName} · 工作流：{CurrentWorkflow.Label}");
        _ = RecoverPendingGenerationDispatchesAsync();
    }

    public event EventHandler? LayoutResetRequested;
    public event EventHandler<string>? PanelShowRequested;
    public event EventHandler<string>? PanelToggleRequested;
    public event Action<WorkflowDefinition>? WorkflowChanged;
    public event Action<WorkflowDefinition>? WorkflowDocumentRequested;

    public IReadOnlyList<WorkflowDefinition> Workflows => WorkflowCatalog.Dockable;

    public EditorWorkspaceViewModel EditorWorkspace { get; }

    public TilemapWorkspaceViewModel TilemapWorkspace { get; }

    public ProjectBrowserViewModel ProjectBrowser { get; }

    public ArtStyleLibraryViewModel ArtStyleLibrary { get; }

    public BackgroundRemovalViewModel BackgroundRemoval { get; }

    public PixelAlignmentViewModel PixelAlignment { get; }

    public ImageOutlineViewModel ImageOutline { get; }

    public IconDesignViewModel IconDesign { get; }

    public AgentConnectionViewModel AgentConnection { get; }

    public AssetGenerationViewModel AssetGeneration { get; }

    public CommandConsoleViewModel CommandConsole { get; }

    public BlueprintViewModel Blueprint { get; }

    public WorkflowDefinition CurrentWorkflow => _currentWorkflow;

    public object CurrentWorkspace => _currentWorkspace;

    public WorkflowPlaceholderViewModel? PlaceholderWorkspace => _placeholderWorkspace;

    public string? CurrentProjectPath => _currentProject?.Paths.RootDirectory;

    public string CurrentProjectDisplayName => _currentProject?.Manifest.Name ?? "未创建项目";

    public WorkflowDefinition SelectedNewProjectWorkflow
    {
        get => _selectedNewProjectWorkflow;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (SetProperty(ref _selectedNewProjectWorkflow, value))
            {
                RefreshNewProjectValidation();
            }
        }
    }

    public string NewProjectName
    {
        get => _newProjectName;
        set
        {
            if (SetProperty(ref _newProjectName, value ?? string.Empty))
            {
                RefreshNewProjectValidation();
            }
        }
    }

    public string NewProjectParentPath
    {
        get => _newProjectParentPath;
        set
        {
            if (SetProperty(ref _newProjectParentPath, value ?? string.Empty))
            {
                RefreshNewProjectValidation();
            }
        }
    }

    public string NewProjectTargetPath
    {
        get
        {
            try
            {
                return string.IsNullOrWhiteSpace(NewProjectParentPath) ||
                       string.IsNullOrWhiteSpace(NewProjectName)
                    ? "请填写项目名称并选择保存位置"
                    : Path.GetFullPath(Path.Combine(NewProjectParentPath, NewProjectName.Trim()));
            }
            catch
            {
                return "项目路径无效";
            }
        }
    }

    public string NewProjectValidationMessage
    {
        get => _newProjectValidationMessage;
        private set => SetProperty(ref _newProjectValidationMessage, value);
    }

    public bool IsUiWorkspace => CurrentWorkflow.Kind == WorkflowKind.UiDesign;
    public bool IsTilemapWorkspace => CurrentWorkflow.Kind == WorkflowKind.Tilemap;
    public bool IsPlaceholderWorkspace => CurrentWorkspace is WorkflowPlaceholderViewModel;

    public bool IsNewProjectDialogOpen
    {
        get => _isNewProjectDialogOpen;
        private set
        {
            if (SetProperty(ref _isNewProjectDialogOpen, value))
            {
                OnPropertyChanged(nameof(IsWorkspaceInteractionEnabled));
                RaiseUiCommandState();
            }
        }
    }

    public bool IsCreatingNewProject
    {
        get => _isCreatingNewProject;
        private set
        {
            if (SetProperty(ref _isCreatingNewProject, value))
            {
                _createNewProjectCommand.RaiseCanExecuteChanged();
                _closeNewProjectCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsWorkspaceInteractionEnabled => !IsNewProjectDialogOpen;

    public AppTheme CurrentTheme => _themeService.CurrentTheme;
    public Theme DockTheme => _themeService.DockTheme;
    public bool IsDarkTheme => CurrentTheme == AppTheme.Dark;
    public bool IsLightTheme => CurrentTheme == AppTheme.Light;
    public string CurrentThemeLabel => IsDarkTheme ? "暗色" : "亮色";
    public string ThemeToggleLabel => IsDarkTheme ? "切换为亮色" : "切换为暗色";
    public string ThemeToggleGlyph => IsDarkTheme ? "☀" : "☾";
    public string WorkspaceStatusText => CurrentWorkflow.Kind switch
    {
        WorkflowKind.UiDesign => EditorWorkspace.StatusText,
        WorkflowKind.Tilemap => TilemapWorkspace.StatusText,
        _ => PlaceholderWorkspace?.StateLabel ?? "业务框架已就绪"
    };

    public ICommand OpenNewProjectCommand { get; }
    public ICommand CloseNewProjectCommand { get; }
    public ICommand ChooseNewProjectLocationCommand { get; }
    public ICommand CreateNewProjectCommand { get; }
    public ICommand SelectWorkflowCommand { get; }
    public ICommand ToggleThemeCommand { get; }
    public ICommand UseDarkThemeCommand { get; }
    public ICommand UseLightThemeCommand { get; }
    public ICommand ResetLayoutCommand { get; }
    public ICommand ShowPanelCommand { get; }
    public ICommand TogglePanelCommand { get; }
    public ICommand UiSaveCommand { get; }
    public ICommand UiImportCommand { get; }
    public ICommand UiExportCommand { get; }
    public ICommand UiUndoCommand { get; }
    public ICommand UiRedoCommand { get; }
    public ICommand UiDeleteCommand { get; }
    public ICommand UiZoomInCommand { get; }
    public ICommand UiZoomOutCommand { get; }
    public ICommand UiResetZoomCommand { get; }

    public void SelectWorkflow(string key) => SelectWorkflow(WorkflowCatalog.FindDockable(key));

    public void SelectWorkflow(WorkflowDefinition workflow)
    {
        ActivateWorkspaceDocument(workflow);
        WorkflowDocumentRequested?.Invoke(workflow);
    }

    public void ActivateWorkspaceDocument(WorkflowDefinition workflow)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        if (workflow.Kind == WorkflowKind.Test)
        {
            throw new ArgumentException("测试模板不注册为正式业务窗口。", nameof(workflow));
        }

        CommitWorkspaceDocumentContext(workflow);
    }

    private void CommitWorkspaceDocumentContext(WorkflowDefinition workflow)
    {
        SetWorkspaceState(workflow);
        ProjectBrowser.SetPrefabLibraryAvailability(workflow.Kind == WorkflowKind.UiDesign);
        OnPropertyChanged(nameof(IsUiWorkspace));
        OnPropertyChanged(nameof(IsTilemapWorkspace));
        OnPropertyChanged(nameof(IsPlaceholderWorkspace));
        OnPropertyChanged(nameof(WorkspaceStatusText));
        RaiseUiCommandState();
        WorkflowChanged?.Invoke(workflow);
    }

    private void OpenNewProject()
    {
        SelectedNewProjectWorkflow = CurrentWorkflow.Kind == WorkflowKind.Test
            ? WorkflowCatalog.UiDesign
            : CurrentWorkflow;
        NewProjectParentPath = ResolveInitialProjectParentPath();
        NewProjectName = CreateAvailableProjectName(NewProjectParentPath);
        IsNewProjectDialogOpen = true;
        RefreshNewProjectValidation();
    }

    private void ChooseNewProjectLocation()
    {
        var selected = _projectLocationPicker.SelectParentDirectory(NewProjectParentPath);
        if (!string.IsNullOrWhiteSpace(selected))
        {
            NewProjectParentPath = selected;
            NewProjectName = CreateAvailableProjectName(selected);
        }
    }

    private bool CanCreateNewProject() =>
        !IsCreatingNewProject && TryValidateNewProjectInput(out _, out _);

    private async Task CreateNewProjectAsync()
    {
        if (!TryValidateNewProjectInput(out _, out var reason))
        {
            NewProjectValidationMessage = reason;
            return;
        }

        IsCreatingNewProject = true;
        var previousProject = _currentProject;
        var previousBlueprintWorkflowKey = previousProject?.Manifest.WorkflowKey ?? _settings.ActiveWorkflowKey;
        var previousBrowserRoot = ProjectBrowser.ConfiguredRootPath;
        var previousBrowserPath = ProjectBrowser.CurrentPath;
        var previousBrowserLock = ProjectBrowser.IsRootSelectionLocked;
        var blueprintSwitched = false;
        var projectCommitted = false;
        var rollbackIncomplete = false;
        try
        {
            NewProjectValidationMessage = "正在验证并保存当前蓝图…";
            if (!Blueprint.SaveNow())
            {
                throw new InvalidOperationException("当前蓝图未能安全保存，尚未创建候选项目目录。 ");
            }

            NewProjectValidationMessage = "正在创建项目目录与初始数据…";
            EditorWorkspace.PrepareForProjectSwitch();
            var workspace = _projectWorkspaceService.Create(
                NewProjectParentPath,
                NewProjectName,
                SelectedNewProjectWorkflow.Key);

            if (!Blueprint.Activate(workspace.Paths.RootDirectory, SelectedNewProjectWorkflow.Key))
            {
                throw new InvalidOperationException("新项目蓝图未能安全激活，项目切换已取消。");
            }

            blueprintSwitched = true;

            ProjectBrowser.SetRootSelectionLock(false);
            if (!ProjectBrowser.TrySetRootPath(workspace.Paths.AssetDirectory) ||
                !ProjectBrowser.SelectDirectory(workspace.Paths.GeneratedAssetDirectory))
            {
                throw new InvalidOperationException("项目已创建，但 Project 面板无法切换到项目 Assets。 ");
            }

            EditorWorkspace.SwitchToPreparedProject(workspace.Paths.RootDirectory);
            _currentProject = workspace;
            projectCommitted = true;
            _settings.LastProjectPath = workspace.Paths.RootDirectory;
            _settings.AssetRootPath = workspace.Paths.AssetDirectory;
            _settings.AssetCurrentPath = workspace.Paths.GeneratedAssetDirectory;

            ProjectBrowser.SetRootSelectionLock(true);
            ApplyProjectTilemapSettings(workspace.Manifest.Tilemap);
            _settings.ActiveWorkflowKey = SelectedNewProjectWorkflow.Key;
            _settings.HasCompletedWorkflowSelection = true;
            ActivateWorkspaceDocument(SelectedNewProjectWorkflow);
            OnPropertyChanged(nameof(CurrentProjectPath));
            OnPropertyChanged(nameof(CurrentProjectDisplayName));
            OnPropertyChanged(nameof(WorkspaceStatusText));
            IsNewProjectDialogOpen = false;
            NewProjectValidationMessage = $"项目已创建：{workspace.Paths.RootDirectory}";
            SaveSettings();
            WorkflowDocumentRequested?.Invoke(SelectedNewProjectWorkflow);
            await ResetAgentDispatchForWorkingDirectoryAsync(workspace.Paths.RootDirectory)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await InvokeOnDispatcherAsync(() =>
            {
                if (!projectCommitted)
                {
                    ProjectBrowser.SetRootSelectionLock(false);
                    if (ProjectBrowser.TrySetRootPath(previousBrowserRoot) &&
                        !string.IsNullOrWhiteSpace(previousBrowserPath))
                    {
                        _ = ProjectBrowser.SelectDirectory(previousBrowserPath);
                    }

                    ProjectBrowser.SetRootSelectionLock(previousBrowserLock);
                    if (blueprintSwitched &&
                        !Blueprint.Activate(previousProject?.Paths.RootDirectory, previousBlueprintWorkflowKey))
                    {
                        rollbackIncomplete = true;
                        _workbenchConsoleService.WriteError(
                            "Blueprint",
                            "新项目激活失败后，旧蓝图上下文也未能重新激活。请立即停止编辑并重启 GAssetForge。 ");
                    }
                }

                ProjectBrowser.SetRootSelectionLock(_currentProject is not null);
                NewProjectValidationMessage = projectCommitted
                    ? $"项目已创建，但后续初始化失败：{exception.Message}"
                    : rollbackIncomplete
                        ? $"创建项目失败，且旧蓝图上下文回滚不完整：{exception.Message}。请立即停止编辑并重启 GAssetForge。"
                    : $"创建项目失败：已保留原项目；{exception.Message}";
            }).ConfigureAwait(false);
        }
        finally
        {
            await InvokeOnDispatcherAsync(() =>
            {
                IsCreatingNewProject = false;
                if (IsNewProjectDialogOpen)
                {
                    RefreshNewProjectValidation(keepFailureMessage: true);
                }
            }).ConfigureAwait(false);
        }
    }

    private bool TryValidateNewProjectInput(out string targetPath, out string reason)
    {
        targetPath = string.Empty;
        reason = string.Empty;
        try
        {
            if (SelectedNewProjectWorkflow is null ||
                Workflows.All(candidate => !Equals(candidate, SelectedNewProjectWorkflow)))
            {
                reason = "请选择一个业务模板。";
                return false;
            }

            if (string.IsNullOrWhiteSpace(NewProjectParentPath))
            {
                reason = "请选择项目保存位置。";
                return false;
            }

            var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(NewProjectParentPath));
            if (!Directory.Exists(parent))
            {
                reason = "项目保存位置不存在或当前不可用。";
                return false;
            }

            var name = ProjectWorkspaceService.ValidateProjectName(NewProjectName);
            targetPath = Path.GetFullPath(Path.Combine(parent, name));
            var targetParent = Directory.GetParent(targetPath)?.FullName;
            if (targetParent is null || !AssetRootService.PathEquals(parent, targetParent))
            {
                reason = "项目必须创建为所选位置的直接子目录。";
                return false;
            }

            if (Directory.Exists(targetPath) || File.Exists(targetPath))
            {
                reason = "目标项目目录已经存在，请修改项目名称。";
                return false;
            }

            return true;
        }
        catch (Exception exception) when (exception is
            ArgumentException or
            IOException or
            UnauthorizedAccessException or
            NotSupportedException or
            PathTooLongException)
        {
            reason = exception.Message;
            return false;
        }
    }

    private void RefreshNewProjectValidation(bool keepFailureMessage = false)
    {
        OnPropertyChanged(nameof(NewProjectTargetPath));
        var isFailureMessage =
            NewProjectValidationMessage.StartsWith("创建项目失败", StringComparison.Ordinal) ||
            NewProjectValidationMessage.StartsWith("项目已创建，但后续初始化失败", StringComparison.Ordinal);
        if (!keepFailureMessage || !isFailureMessage)
        {
            NewProjectValidationMessage = TryValidateNewProjectInput(out _, out var reason)
                ? $"将创建独立项目目录，并首次打开“{SelectedNewProjectWorkflow.Label}”；其它业务窗口仍可随时从“视图”菜单拉起。"
                : reason;
        }

        _createNewProjectCommand?.RaiseCanExecuteChanged();
    }

    private string ResolveInitialProjectParentPath()
    {
        if (_currentProject is not null &&
            Directory.GetParent(_currentProject.Paths.RootDirectory)?.FullName is { } currentParent &&
            Directory.Exists(currentParent))
        {
            return currentParent;
        }

        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return Directory.Exists(documents) ? documents : _settingsService.Paths.ProjectDirectory;
    }

    private bool ClearRejectedProjectAssetPaths(ProjectWorkspace rejectedProject)
    {
        var changed = false;
        if (AssetRootService.IsWithinRoot(
                rejectedProject.Paths.AssetDirectory,
                _settings.AssetRootPath ?? string.Empty))
        {
            _settings.AssetRootPath = null;
            changed = true;
        }

        if (AssetRootService.IsWithinRoot(
                rejectedProject.Paths.AssetDirectory,
                _settings.AssetCurrentPath ?? string.Empty))
        {
            _settings.AssetCurrentPath = null;
            changed = true;
        }

        return changed;
    }

    private string ResolveAgentWorkingDirectory() =>
        _currentProject?.Paths.RootDirectory ?? ProjectBrowser.RootPath;

    private GenerationBatchDispatchCoordinator CreateAgentDispatchCoordinator()
    {
        var coordinator = new GenerationBatchDispatchCoordinator(
            AgentConnection.RunTurnAsync,
            _generationTaskStore,
            new ImageDeliveryPreflight(
                sandboxNetworkAccessConfiguredProvider: () => true));
        coordinator.DispatchProgressChanged += OnAgentDispatchProgressChanged;
        coordinator.DispatchCompleted += OnAgentDispatchCompleted;
        return coordinator;
    }

    private async Task ResetAgentDispatchForWorkingDirectoryAsync(string workingDirectory)
    {
        var wasDispatching = AssetGeneration.IsDispatching;
        var canceledBatchId = AssetGeneration.CurrentDispatchBatchId;
        AssetGeneration.BatchPublished -= OnGenerationBatchPublished;
        var previousCoordinator = _agentDispatchCoordinator;
        previousCoordinator.DispatchProgressChanged -= OnAgentDispatchProgressChanged;
        previousCoordinator.DispatchCompleted -= OnAgentDispatchCompleted;
        try
        {
            await previousCoordinator.DisposeAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            CommandConsole.WriteError(
                "Generation",
                $"项目切换时停止旧 Agent 调度器失败：{exception.Message}",
                exception);
        }

        if (wasDispatching)
        {
            const string cancellationDetail =
                "旧项目的自动调度已停止，未完成任务可继续从原项目本地队列审计或恢复";
            AssetGeneration.ReportDispatchCanceled(canceledBatchId, cancellationDetail);
            CommandConsole.WriteWarning(
                "Generation",
                "项目切换已取消旧 Agent 调度；未完成任务仍保留在原项目本地 Pending 队列。 ");
        }

        AgentSessionBookmarkService.Clear(_settings);
        SaveSettings();
        try
        {
            await AgentConnection.ReconfigureWorkingDirectoryAsync(
                    workingDirectory,
                    resumeThreadId: null)
                .ConfigureAwait(true);
        }
        catch
        {
            // Even if an old child process cannot stop cleanly, immediately invalidate its
            // working directory so it cannot receive tasks for the newly activated project.
            AgentConnection.ConfigureWorkingDirectory(workingDirectory, resumeThreadId: null);
        }
        finally
        {
            if (!_isShutDown)
            {
                _agentDispatchCoordinator = CreateAgentDispatchCoordinator();
                AssetGeneration.BatchPublished += OnGenerationBatchPublished;
                _ = RecoverPendingGenerationDispatchesAsync();
            }
        }
    }

    private static string CreateAvailableProjectName(string parentDirectory)
    {
        const string baseName = "新项目";
        if (!Directory.Exists(parentDirectory))
        {
            return baseName;
        }

        for (var index = 1; index <= 999; index++)
        {
            var candidate = index == 1 ? baseName : $"{baseName} {index}";
            var target = Path.Combine(parentDirectory, candidate);
            if (!Directory.Exists(target) && !File.Exists(target))
            {
                return candidate;
            }
        }

        return $"{baseName} {DateTime.Now:yyyyMMdd-HHmmss}";
    }

    private void ApplyProjectTilemapSettings(ProjectTilemapSettings tilemap)
    {
        _isApplyingProjectSettings = true;
        try
        {
            TilemapWorkspace.GridUnitSize = tilemap.GridUnitSize;
            TilemapWorkspace.ArtImageWidth = tilemap.ArtImageWidth;
            TilemapWorkspace.ArtImageHeight = tilemap.ArtImageHeight;
            TilemapWorkspace.DiamondAngle = tilemap.DiamondAngle;
            TilemapWorkspace.RotationAngle = tilemap.RotationAngle;
        }
        finally
        {
            _isApplyingProjectSettings = false;
        }

        CaptureTilemapSettings();
    }

    public void SetTheme(AppTheme theme)
    {
        if (CurrentTheme == theme)
        {
            return;
        }

        _themeService.Apply(theme);
        _settings.Theme = theme;
        OnPropertyChanged(nameof(CurrentTheme));
        OnPropertyChanged(nameof(DockTheme));
        OnPropertyChanged(nameof(IsDarkTheme));
        OnPropertyChanged(nameof(IsLightTheme));
        OnPropertyChanged(nameof(CurrentThemeLabel));
        OnPropertyChanged(nameof(ThemeToggleLabel));
        OnPropertyChanged(nameof(ThemeToggleGlyph));
        SaveSettings();
    }

    public void Shutdown()
    {
        var shutdownTask = ShutdownAsync();
        if (_dispatcher.CheckAccess() &&
            !shutdownTask.IsCompleted &&
            !_dispatcher.HasShutdownStarted &&
            !_dispatcher.HasShutdownFinished)
        {
            var frame = new DispatcherFrame();
            _ = shutdownTask.ContinueWith(
                completedTask =>
                {
                    if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
                    {
                        frame.Continue = false;
                        return;
                    }

                    _ = _dispatcher.BeginInvoke(
                        DispatcherPriority.Send,
                        new Action(() => frame.Continue = false));
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            Dispatcher.PushFrame(frame);
        }

        shutdownTask.GetAwaiter().GetResult();
    }

    public Task ShutdownAsync()
    {
        lock (_shutdownLock)
        {
            _shutdownTask ??= ShutdownCoreAsync();
            return _shutdownTask;
        }
    }

    private async Task ShutdownCoreAsync()
    {
        await RunOnDispatcherAsync(() =>
        {
            _isShutDown = true;
            AssetGeneration.BatchPublished -= OnGenerationBatchPublished;
            _agentDispatchCoordinator.DispatchProgressChanged -= OnAgentDispatchProgressChanged;
            _agentDispatchCoordinator.DispatchCompleted -= OnAgentDispatchCompleted;
        }).ConfigureAwait(false);
        try
        {
            await _agentDispatchCoordinator.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // Persisted queue tasks remain available even if an active Turn resists cancellation.
        }

        await RunOnDispatcherAsync(() =>
        {
            EditorWorkspace.PropertyChanged -= OnEditorWorkspacePropertyChanged;
            TilemapWorkspace.PropertyChanged -= OnTilemapWorkspacePropertyChanged;
            ProjectBrowser.ConfiguredRootPathChanged -= OnConfiguredAssetRootPathChanged;
            ProjectBrowser.CurrentPathChanged -= OnAssetCurrentPathChanged;
            AgentConnection.SettingsChanged -= OnAgentConnectionSettingsChanged;
            AgentConnection.SessionChanged -= OnAgentSessionChanged;
            AgentConnection.AgentActivityReceived -= OnAgentActivityForStructuredLog;
            AgentConnection.PropertyChanged -= OnAgentConnectionPropertyChanged;
            AssetGeneration.SettingsChanged -= OnAssetGenerationSettingsChanged;
            BackgroundRemoval.SettingsChanged -= OnBackgroundRemovalSettingsChanged;
            PixelAlignment.SettingsChanged -= OnPixelAlignmentSettingsChanged;
            ImageOutline.SettingsChanged -= OnImageOutlineSettingsChanged;
            IconDesign.SettingsChanged -= OnIconDesignSettingsChanged;
            AssetGeneration.StyleLibraryRequested -= OnAssetGenerationStyleLibraryRequested;
            AssetGeneration.AgentConnectionRequested -= OnAssetGenerationAgentConnectionRequested;
            AssetGeneration.BackgroundRemovalRequested -= OnAssetGenerationBackgroundRemovalRequested;
            foreach (var command in _uiCommands)
            {
                command.Dispose();
            }

            _settings.Theme = CurrentTheme;
            _settings.LastProjectPath = CurrentProjectPath;
            _settings.AssetRootPath = ProjectBrowser.ConfiguredRootPath;
            _settings.AssetCurrentPath = ProjectBrowser.CurrentPath;
            _settings.McpAgentKey = AgentConnection.SelectedAgent.Key;
            _settings.McpPort = AgentConnection.Port;
            _settings.CodexExecutablePath = AgentConnection.CodexExecutablePath;
            CaptureAssetGenerationSettings();
            ApplyBackgroundRemovalSettings(BackgroundRemoval.CaptureSettings());
            ApplyPixelAlignmentSettings(PixelAlignment.CaptureSettings());
            ApplyImageOutlineSettings(ImageOutline.CaptureSettings());
            ApplyIconDesignSettings(IconDesign.CaptureSettings());
            CaptureTilemapSettings();
            if (_currentProject is not null)
            {
                try
                {
                    _currentProject = _projectWorkspaceService.UpdateTilemap(
                        _currentProject.Paths.RootDirectory,
                        BuildProjectTilemapSettings());
                }
                catch
                {
                    // Keep shutdown best-effort; atomic project files remain unchanged on failure.
                }
            }

            AssetGeneration.Dispose();
        }).ConfigureAwait(false);

        await AgentConnection.DisposeAsync().ConfigureAwait(false);
        await RunOnDispatcherAsync(() =>
        {
            EditorWorkspace.Shutdown();
            ProjectBrowser.Shutdown();
            ArtStyleLibrary.Shutdown();
            Blueprint.Dispose();
            SaveSettings();
            CommandConsole.Dispose();
            _workbenchConsoleService.Dispose();
        }).ConfigureAwait(false);
    }

    private Task RunOnDispatcherAsync(Action action)
    {
        if (_dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
        {
            return Task.CompletedTask;
        }

        return _dispatcher.InvokeAsync(action, DispatcherPriority.Send).Task;
    }

    private void SetWorkspaceState(WorkflowDefinition workflow)
    {
        var (nextWorkspace, nextPlaceholder) = CreateWorkspaceState(workflow);
        var workflowChanged = !Equals(_currentWorkflow, workflow);
        var workspaceChanged = !ReferenceEquals(_currentWorkspace, nextWorkspace);
        var placeholderChanged = !ReferenceEquals(_placeholderWorkspace, nextPlaceholder);

        _currentWorkflow = workflow;
        _currentWorkspace = nextWorkspace;
        _placeholderWorkspace = nextPlaceholder;

        if (workflowChanged)
        {
            OnPropertyChanged(nameof(CurrentWorkflow));
        }

        if (workspaceChanged)
        {
            OnPropertyChanged(nameof(CurrentWorkspace));
        }

        if (placeholderChanged)
        {
            OnPropertyChanged(nameof(PlaceholderWorkspace));
        }
    }

    private (object Workspace, WorkflowPlaceholderViewModel? Placeholder) CreateWorkspaceState(
        WorkflowDefinition workflow) => workflow.Kind switch
    {
        WorkflowKind.UiDesign => (EditorWorkspace, null),
        WorkflowKind.Tilemap => (TilemapWorkspace, null),
        _ => CreatePlaceholderWorkspace(workflow)
    };

    private static (object Workspace, WorkflowPlaceholderViewModel Placeholder) CreatePlaceholderWorkspace(
        WorkflowDefinition workflow)
    {
        var placeholder = new WorkflowPlaceholderViewModel(workflow);
        return (placeholder, placeholder);
    }

    private GatedCommand Gate(ICommand command)
    {
        var gated = new GatedCommand(command, () => IsUiWorkspace && IsWorkspaceInteractionEnabled);
        _uiCommands.Add(gated);
        return gated;
    }

    private void RaiseUiCommandState()
    {
        foreach (var command in _uiCommands)
        {
            command.RaiseCanExecuteChanged();
        }
    }

    private void OnEditorWorkspacePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(EditorWorkspaceViewModel.StatusText)
            or nameof(EditorWorkspaceViewModel.SaveStateText))
        {
            OnPropertyChanged(nameof(WorkspaceStatusText));
        }
    }

    private void OnTilemapWorkspacePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TilemapWorkspaceViewModel.GridUnitSize)
            or nameof(TilemapWorkspaceViewModel.ArtImageWidth)
            or nameof(TilemapWorkspaceViewModel.ArtImageHeight)
            or nameof(TilemapWorkspaceViewModel.DiamondAngle)
            or nameof(TilemapWorkspaceViewModel.RotationAngle))
        {
            CaptureTilemapSettings();
            if (!_isApplyingProjectSettings && _currentProject is not null)
            {
                try
                {
                    _currentProject = _projectWorkspaceService.UpdateTilemap(
                        _currentProject.Paths.RootDirectory,
                        BuildProjectTilemapSettings());
                }
                catch
                {
                    // Project settings persistence will retry on the next valid change.
                }
            }

            SaveSettings();
        }

        if (IsTilemapWorkspace &&
            (string.IsNullOrEmpty(e.PropertyName) ||
             e.PropertyName == nameof(TilemapWorkspaceViewModel.StatusText)))
        {
            OnPropertyChanged(nameof(WorkspaceStatusText));
        }
    }

    private void OnConfiguredAssetRootPathChanged(string? path)
    {
        _settings.AssetRootPath = path;
        _settings.AssetCurrentPath = ProjectBrowser.CurrentPath;
        SaveSettings();
    }

    private void OnAssetCurrentPathChanged(string path)
    {
        _settings.AssetCurrentPath = path;
        SaveSettings();
    }

    private void OnAgentConnectionSettingsChanged(
        object? sender,
        AgentConnectionSettingsChangedEventArgs e)
    {
        _settings.McpAgentKey = e.AgentKey;
        _settings.McpPort = e.Port;
        _settings.CodexExecutablePath = e.CodexExecutablePath;
        SaveSettings();
    }

    private void OnAgentConnectionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isShutDown ||
            (!string.IsNullOrEmpty(e.PropertyName) &&
             e.PropertyName != nameof(AgentConnectionViewModel.IsConnected)) ||
            !AgentConnection.IsConnected)
        {
            return;
        }

        _ = RecoverPendingGenerationDispatchesAsync();
    }

    private void OnAgentSessionChanged(object? sender, AgentSessionChangedEventArgs e)
    {
        if (_isShutDown ||
            !AgentSessionBookmarkService.PathEquals(e.WorkingDirectory, ResolveAgentWorkingDirectory()))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(e.ThreadId))
        {
            AgentSessionBookmarkService.Clear(_settings);
        }
        else
        {
            AgentSessionBookmarkService.Store(_settings, e.ThreadId, e.WorkingDirectory);
        }

        SaveSettings();
        if (AgentConnection.IsConnected)
        {
            _ = RecoverPendingGenerationDispatchesAsync();
        }
    }

    private async Task RecoverPendingGenerationDispatchesAsync()
    {
        if (_isShutDown || !AgentConnection.IsConnected)
        {
            return;
        }

        await _dispatchRecoveryGate.WaitAsync().ConfigureAwait(true);
        try
        {
            if (_isShutDown || !AgentConnection.IsConnected)
            {
                return;
            }

            var workingDirectory = ResolveAgentWorkingDirectory();
            var pending = await AssetGeneration.GetRecoverablePendingDispatchesAsync()
                .ConfigureAwait(true);
            foreach (var dispatch in pending)
            {
                if (_isShutDown ||
                    !AgentConnection.IsConnected ||
                    !AgentSessionBookmarkService.PathEquals(
                        workingDirectory,
                        ResolveAgentWorkingDirectory()))
                {
                    return;
                }

                try
                {
                    await AssetGeneration.RepairPersistedDispatchWorkspaceAsync(dispatch.BatchId)
                        .ConfigureAwait(true);
                }
                catch (Exception repairException) when (repairException is
                    IOException or UnauthorizedAccessException or InvalidDataException or
                    InvalidOperationException or NotSupportedException or PathTooLongException)
                {
                    CommandConsole.WriteWarning(
                        "Generation",
                        $"批次 {CompactIdentifier(dispatch.BatchId)} 的 Run manifest/prompt 自动修复失败：" +
                        $"{repairException.GetType().Name}: {repairException.Message}；" +
                        "任务仍按冻结约束和路径继续派发。");
                }

                OnGenerationBatchPublished(
                    AssetGeneration,
                    new GenerationBatchPublishedEventArgs(
                        dispatch.BatchId,
                        dispatch.TaskIds,
                        dispatch.IsRetry,
                        dispatchRequested: true));
                CommandConsole.WriteWarning(
                    "Generation",
                    $"已恢复持久化 Pending 批次 {CompactIdentifier(dispatch.BatchId)} 的 Agent 调度；" +
                    "不会创建或覆盖新的任务与产物路径。");
            }

            var retrySources = await AssetGeneration.GetRecoverableAutomaticRetrySourceBatchIdsAsync()
                .ConfigureAwait(true);
            foreach (var sourceBatchId in retrySources)
            {
                if (_isShutDown ||
                    !AgentConnection.IsConnected ||
                    !AgentSessionBookmarkService.PathEquals(
                        workingDirectory,
                        ResolveAgentWorkingDirectory()))
                {
                    return;
                }

                var retry = await AssetGeneration.TryCreateAutomaticRetryBatchAsync(sourceBatchId)
                    .ConfigureAwait(true);
                if (retry.Scheduled)
                {
                    CommandConsole.WriteWarning(
                        "Generation",
                        $"已从持久化失败状态恢复自动重做：源批次 {CompactIdentifier(sourceBatchId)}，" +
                        $"后继批次 {CompactIdentifier(retry.RetryBatchId)}。");
                }
                else if (retry.HadEligibleFailures)
                {
                    CommandConsole.WriteError(
                        "Generation",
                        $"恢复批次 {CompactIdentifier(sourceBatchId)} 的自动重做失败：{retry.Message}");
                }
            }
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or InvalidDataException or
            InvalidOperationException or NotSupportedException or PathTooLongException)
        {
            CommandConsole.WriteError(
                "Generation",
                $"恢复持久化生成调度失败：{exception.Message}",
                exception);
        }
        finally
        {
            _dispatchRecoveryGate.Release();
        }
    }

    private void OnAgentActivityForStructuredLog(object? sender, AgentActivityEventArgs e)
    {
        if (_isShutDown || !ShouldPersistAgentActivity(e))
        {
            return;
        }

        var activityKey = BuildAgentActivityTimingKey(e);
        long? elapsedMilliseconds = null;
        if (e.Category is AgentActivityCategory.TurnStarted or
            AgentActivityCategory.ToolStarted or
            AgentActivityCategory.CommandStarted or
            AgentActivityCategory.ItemStarted)
        {
            _agentActivityStarts[activityKey] = e.Timestamp.ToUniversalTime();
        }
        else if (e.Category is AgentActivityCategory.TurnCompleted or
                 AgentActivityCategory.ToolCompleted or
                 AgentActivityCategory.CommandCompleted or
                 AgentActivityCategory.ItemCompleted or
                 AgentActivityCategory.Error)
        {
            if (_agentActivityStarts.Remove(activityKey, out var startedAtUtc))
            {
                elapsedMilliseconds = Math.Max(
                    0L,
                    (long)(e.Timestamp.ToUniversalTime() - startedAtUtc).TotalMilliseconds);
            }
        }

        var level = e.IsWarning || e.Category == AgentActivityCategory.Error
            ? WorkbenchLogLevel.Warning
            : WorkbenchLogLevel.Information;
        _workbenchConsoleService.WriteStructured(
            level,
            "Agent Activity",
            e.Message,
            new WorkbenchLogContext
            {
                RunId = AssetGeneration.CurrentDispatchBatchId,
                BatchId = AssetGeneration.CurrentDispatchBatchId,
                TaskId = e.TaskId,
                AgentThreadId = e.ThreadId,
                AgentTurnId = e.TurnId,
                AgentItemId = e.ItemId,
                Stage = $"agent.{e.Category}",
                Status = e.IsWarning
                    ? "warning"
                    : e.IsTerminal || e.Category is AgentActivityCategory.ToolCompleted or
                        AgentActivityCategory.CommandCompleted or AgentActivityCategory.ItemCompleted
                        ? "completed"
                        : "started",
                ElapsedMilliseconds = elapsedMilliseconds,
                ErrorCode = e.Category == AgentActivityCategory.Error ? e.ItemType ?? e.EventName : null
            },
            e.Timestamp);
    }

    private static bool ShouldPersistAgentActivity(AgentActivityEventArgs e)
    {
        if (e.Category == AgentActivityCategory.Reasoning)
        {
            return false;
        }

        if (e.Category == AgentActivityCategory.AgentMessage)
        {
            return e.EventName?.Contains("completed", StringComparison.OrdinalIgnoreCase) == true;
        }

        if (e.Category == AgentActivityCategory.Plan && !e.IsTerminal)
        {
            return false;
        }

        return true;
    }

    internal static string BuildAgentActivityTimingKey(AgentActivityEventArgs activity)
    {
        ArgumentNullException.ThrowIfNull(activity);
        var turnId = string.IsNullOrWhiteSpace(activity.TurnId)
            ? "unknown"
            : activity.TurnId.Trim();
        var scope = activity.Category switch
        {
            AgentActivityCategory.TurnStarted or AgentActivityCategory.TurnCompleted => "turn",
            AgentActivityCategory.ToolStarted or AgentActivityCategory.ToolCompleted => "tool",
            AgentActivityCategory.CommandStarted or AgentActivityCategory.CommandCompleted => "command",
            AgentActivityCategory.ItemStarted or AgentActivityCategory.ItemCompleted => "item",
            AgentActivityCategory.Error => ResolveFailedAgentActivityScope(activity.ItemType),
            _ => activity.Category.ToString()
        };
        return scope == "turn" || string.IsNullOrWhiteSpace(activity.ItemId)
            ? $"{turnId}/{scope}"
            : $"{turnId}/{scope}:{activity.ItemId.Trim()}";
    }

    private static string ResolveFailedAgentActivityScope(string? itemType)
    {
        var token = new string((itemType ?? string.Empty)
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());
        return token switch
        {
            "mcptoolcall" or "dynamictoolcall" => "tool",
            "commandexecution" => "command",
            _ => "item"
        };
    }

    private void OnGenerationBatchPublished(
        object? sender,
        GenerationBatchPublishedEventArgs e)
    {
        if (_isShutDown)
        {
            return;
        }

        if (!e.DispatchRequested)
        {
            CommandConsole.WriteInfo(
                "Generation",
                $"批次 {CompactIdentifier(e.BatchId)} 已写入本地队列，共 {e.TaskIds.Count} 个任务；未请求 Agent 调度。");
            return;
        }

        try
        {
            if (!_agentDispatchCoordinator.TryEnqueue(e.BatchId, e.TaskIds, e.IsRetry))
            {
                const string queueStopped = "Agent 调度队列已停止；任务仍保留在本地 Pending 队列。 ";
                AssetGeneration.ReportDispatchFailure(
                    e.BatchId,
                    queueStopped);
                CommandConsole.WriteError("Generation", queueStopped);
                return;
            }

            AssetGeneration.ReportDispatchQueued(
                e.BatchId,
                $"{e.TaskIds.Count} 个任务已排队，等待 {AgentConnection.SelectedAgent.DisplayName} 执行");
            CommandConsole.WriteInfo(
                "Generation",
                $"批次 {CompactIdentifier(e.BatchId)} 已进入 Agent 调度队列 · {e.TaskIds.Count} 个任务 · " +
                $"执行器：{AgentConnection.SelectedAgent.DisplayName}");
        }
        catch (Exception exception)
        {
            AssetGeneration.ReportDispatchFailure(e.BatchId, exception.Message);
            CommandConsole.WriteError(
                "Generation",
                $"批次 {CompactIdentifier(e.BatchId)} 入队失败：{exception.Message}",
                exception);
        }
    }

    private void OnAgentDispatchProgressChanged(
        object? sender,
        GenerationBatchDispatchProgressChangedEventArgs e)
    {
        void ApplyProgress()
        {
            if (_isShutDown || !ReferenceEquals(sender, _agentDispatchCoordinator))
            {
                return;
            }

            AssetGeneration.ReportDispatchProgress(e.Snapshot);
            var batchId = CompactIdentifier(e.Snapshot.Request.BatchId);
            var message =
                $"批次 {batchId} · {e.Snapshot.Stage} · {e.Snapshot.ProgressPercentage:0}% · " +
                $"{e.Snapshot.Instruction} · {e.Snapshot.Detail}";
            var level = e.Snapshot.Stage == GenerationBatchDispatchStage.Failed
                ? WorkbenchLogLevel.Error
                : e.Snapshot.Stage == GenerationBatchDispatchStage.Canceled
                    ? WorkbenchLogLevel.Warning
                    : WorkbenchLogLevel.Information;
            _workbenchConsoleService.WriteStructured(
                level,
                "Generation",
                message,
                new WorkbenchLogContext
                {
                    RunId = e.Snapshot.Request.BatchId,
                    BatchId = e.Snapshot.Request.BatchId,
                    Stage = e.Snapshot.Stage.ToString(),
                    Status = e.Snapshot.Stage is GenerationBatchDispatchStage.Failed
                        ? "failed"
                        : e.Snapshot.Stage is GenerationBatchDispatchStage.Canceled
                            ? "canceled"
                            : e.Snapshot.Stage is GenerationBatchDispatchStage.Completed
                                ? "completed"
                                : "active",
                    ErrorCode = e.Snapshot.Stage == GenerationBatchDispatchStage.Failed
                        ? e.Snapshot.FailureStage?.ToString()
                        : null
                });
        }

        if (_dispatcher.CheckAccess())
        {
            ApplyProgress();
        }
        else if (!_dispatcher.HasShutdownStarted && !_dispatcher.HasShutdownFinished)
        {
            _dispatcher.BeginInvoke((Action)ApplyProgress, DispatcherPriority.DataBind);
        }
    }

    private async void OnAgentDispatchCompleted(
        object? sender,
        GenerationBatchDispatchCompletedEventArgs e)
    {
        if (_isShutDown || !ReferenceEquals(sender, _agentDispatchCoordinator))
        {
            return;
        }

        GenerationBatchTurnReconciliationResult reconciliation;
        try
        {
            reconciliation = await _generationBatchTurnReconciler.ReconcileAsync(
                    e.Request.BatchId,
                    e.Result,
                    e.FailureMessage)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await InvokeOnDispatcherAsync(() =>
            {
                if (_isShutDown || !ReferenceEquals(sender, _agentDispatchCoordinator))
                {
                    return;
                }

                var failure = $"Agent Turn 已结束，但本地任务状态收束失败：{exception.Message}";
                AssetGeneration.ReportDispatchFailure(e.Request.BatchId, failure);
                CommandConsole.WriteError("Generation", failure, exception);
            }).ConfigureAwait(false);
            if (sender is GenerationBatchDispatchCoordinator failedCoordinator)
            {
                failedCoordinator.ReleaseBatch(e.Request.BatchId);
            }

            return;
        }

        AutomaticGenerationRetryResult automaticRetry;
        try
        {
            if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
            {
                automaticRetry = AutomaticGenerationRetryResult.None(
                    "应用正在关闭，未创建自动重做任务。");
            }
            else if (_dispatcher.CheckAccess())
            {
                automaticRetry = await AssetGeneration
                    .TryCreateAutomaticRetryBatchAsync(e.Request.BatchId)
                    .ConfigureAwait(true);
            }
            else
            {
                automaticRetry = await _dispatcher.InvokeAsync(
                        () => AssetGeneration.TryCreateAutomaticRetryBatchAsync(e.Request.BatchId),
                        DispatcherPriority.DataBind)
                    .Task
                    .Unwrap()
                    .ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            automaticRetry = AutomaticGenerationRetryResult.Blocked(
                $"自动重做协调失败：{exception.Message}");
        }

        void ApplyResult()
        {
            if (_isShutDown || !ReferenceEquals(sender, _agentDispatchCoordinator))
            {
                return;
            }

            if (automaticRetry.Scheduled)
            {
                CommandConsole.WriteWarning(
                    "Generation",
                    $"批次 {CompactIdentifier(e.Request.BatchId)} 存在未达标产物；" +
                    $"{automaticRetry.Message} 新批次 {CompactIdentifier(automaticRetry.RetryBatchId)} 已进入调度。");
                _ = AssetGeneration.RefreshAfterDispatchAsync();
                return;
            }

            if (automaticRetry.HadEligibleFailures)
            {
                AssetGeneration.ReportDispatchFailure(e.Request.BatchId, automaticRetry.Message);
                CommandConsole.WriteError(
                    "Generation",
                    $"批次 {CompactIdentifier(e.Request.BatchId)} 的可恢复失败无法自动重做：" +
                    automaticRetry.Message);
                _ = AssetGeneration.RefreshAfterDispatchAsync();
                return;
            }

            if (reconciliation.AutoFailedCount > 0)
            {
                var failure = reconciliation.FailureDetail ??
                              "Agent Turn 已结束，但任务未被 MCP 认领或完成。";
                AssetGeneration.ReportDispatchFailure(e.Request.BatchId, failure);
                CommandConsole.WriteError(
                    "Generation",
                    $"批次 {CompactIdentifier(e.Request.BatchId)} 未进入生成步骤：{failure}");
                _ = AssetGeneration.RefreshAfterDispatchAsync();
                return;
            }

            var turnId = CompactIdentifier(e.Result?.TurnId);
            var threadId = CompactIdentifier(e.Result?.ThreadId);
            var identifiers = new[]
                {
                    string.IsNullOrWhiteSpace(threadId) ? null : $"Thread {threadId}",
                    string.IsNullOrWhiteSpace(turnId) ? null : $"Turn {turnId}"
                }
                .Where(value => value is not null);
            var suffix = string.Join(" · ", identifiers!);
            var stateSummary =
                $"任务状态：Completed {reconciliation.CompletedCount} / Failed {reconciliation.FailedCount} / " +
                $"Claimed {reconciliation.ClaimedCount} / Pending {reconciliation.PendingCount}";
            var detail = string.IsNullOrWhiteSpace(suffix)
                ? $"{AgentConnection.RuntimeModeText} 已完成编排 · {stateSummary}"
                : $"{AgentConnection.RuntimeModeText} · {suffix} 已结束 · {stateSummary}";

            if (!e.IsSuccess)
            {
                var failure = string.Join(
                    " ",
                    new[] { e.FailureMessage, reconciliation.FailureDetail }
                        .Where(value => !string.IsNullOrWhiteSpace(value)));
                AssetGeneration.ReportDispatchFailure(
                    e.Request.BatchId,
                    string.IsNullOrWhiteSpace(failure) ? "Agent Turn 未成功完成。" : failure);
                CommandConsole.WriteError(
                    "Generation",
                    $"批次 {CompactIdentifier(e.Request.BatchId)} 调度失败：{failure}");
            }
            else
            {
                AssetGeneration.ReportDispatchResult(e.Request.BatchId, detail);
                var logMessage =
                    $"批次 {CompactIdentifier(e.Request.BatchId)} 的 Agent Turn 已结束；" +
                    "最终结果由 MCP 任务状态和本地文件校验决定。 " + detail;
                if (reconciliation.AllTerminal && reconciliation.FailedCount == 0)
                {
                    CommandConsole.WriteInfo("Generation", logMessage);
                }
                else
                {
                    CommandConsole.WriteWarning(
                        "Generation",
                        $"{logMessage} {reconciliation.FailureDetail}".Trim());
                }
            }

            _ = AssetGeneration.RefreshAfterDispatchAsync();
        }

        try
        {
            await InvokeOnDispatcherAsync(ApplyResult).ConfigureAwait(false);
        }
        finally
        {
            if (sender is GenerationBatchDispatchCoordinator completedCoordinator)
            {
                completedCoordinator.ReleaseBatch(e.Request.BatchId);
            }
        }
    }

    private Task InvokeOnDispatcherAsync(Action action)
    {
        if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
        {
            return Task.CompletedTask;
        }

        if (_dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return _dispatcher.InvokeAsync(action, DispatcherPriority.DataBind).Task;
    }

    private void OnAssetGenerationSettingsChanged(
        object? sender,
        AssetGenerationSettingsChangedEventArgs e)
    {
        _settings.AssetGenerationStepKey = e.StepKey;
        _settings.AssetGenerationContentPrompt = e.ContentPrompt;
        _settings.AssetGenerationNegativePrompt = e.NegativePrompt;
        _settings.AssetGenerationMaxParallelism = e.MaxParallelism;
        _settings.AssetGenerationAutoRetryEnabled = e.AutoRetryEnabled;
        _settings.AssetGenerationAutoRetryCount = e.AutoRetryCount;
        _settings.AssetGenerationPreferUnifiedStylePrompt = e.PreferUnifiedStylePrompt;
        _settings.AssetGenerationOutputPath = e.OutputDirectory;
        _settings.AssetGenerationRows = e.Rows
            .Select(row => new AssetGenerationDraftRowSettings
            {
                Id = row.Id,
                Description = row.Description,
                Quantity = row.Quantity,
                OutputPath = row.OutputDirectory
            })
            .ToList();
        SaveSettings();
    }

    private void OnBackgroundRemovalSettingsChanged(
        object? sender,
        BackgroundRemovalSettingsChangedEventArgs e)
    {
        ApplyBackgroundRemovalSettings(e.Settings);
        SaveSettings();
    }

    private void ApplyBackgroundRemovalSettings(BackgroundRemovalToolSettingsSnapshot settings)
    {
        _settings.BackgroundRemovalPresetKey = settings.PresetKey;
        _settings.BackgroundRemovalCustomColorHex = settings.CustomColorHex;
        _settings.BackgroundRemovalTransparentThreshold = settings.TransparentThreshold;
        _settings.BackgroundRemovalOpaqueThreshold = settings.OpaqueThreshold;
        _settings.BackgroundRemovalEdgeContract = settings.EdgeContract;
        _settings.BackgroundRemovalEdgeFeather = settings.EdgeFeather;
        _settings.BackgroundRemovalDespill = settings.Despill;
        _settings.BackgroundRemovalEdgeConnectedOnly = settings.EdgeConnectedOnly;
        _settings.BackgroundRemovalUseFakeTransparencyGrid = settings.UseFakeTransparencyGrid;
        _settings.BackgroundRemovalCheckerColorAHex = settings.CheckerColorAHex;
        _settings.BackgroundRemovalCheckerColorBHex = settings.CheckerColorBHex;
        _settings.BackgroundRemovalEnableCheckerGridAlignment = settings.EnableCheckerGridAlignment;
        _settings.BackgroundRemovalCheckerCellSize = settings.CheckerCellSize;
        _settings.BackgroundRemovalCheckerOffsetX = settings.CheckerOffsetX;
        _settings.BackgroundRemovalCheckerOffsetY = settings.CheckerOffsetY;
        _settings.BackgroundRemovalCheckerBoundaryTolerance = settings.CheckerBoundaryTolerance;
        _settings.BackgroundRemovalLastInputDirectory = settings.LastInputDirectory;
        _settings.BackgroundRemovalLastOutputDirectory = settings.LastOutputDirectory;
    }

    private void OnPixelAlignmentSettingsChanged(
        object? sender,
        PixelAlignmentSettingsChangedEventArgs e)
    {
        ApplyPixelAlignmentSettings(e.Settings);
        SaveSettings();
    }

    private void ApplyPixelAlignmentSettings(PixelAlignmentToolSettingsSnapshot settings)
    {
        _settings.PixelAlignmentMinimumUpscaleFactor = settings.MinimumUpscaleFactor;
        _settings.PixelAlignmentColumns = settings.Columns;
        _settings.PixelAlignmentRows = settings.Rows;
        _settings.PixelAlignmentSamplingMode = settings.SamplingMode;
        _settings.PixelAlignmentShowGrid = settings.ShowGrid;
        _settings.PixelAlignmentEnableAutomaticColorIndex = settings.EnableAutomaticColorIndex;
        _settings.PixelAlignmentMaximumIndexedColors = settings.MaximumIndexedColors;
        _settings.PixelAlignmentEnableAspectRatioCorrection = settings.EnableAspectRatioCorrection;
        _settings.PixelAlignmentTargetAspectRatioA = settings.TargetAspectRatioA;
        _settings.PixelAlignmentTargetAspectRatioB = settings.TargetAspectRatioB;
        _settings.PixelAlignmentEnableRotationCorrection = settings.EnableRotationCorrection;
        _settings.PixelAlignmentRotationDegrees = settings.RotationDegrees;
        _settings.PixelAlignmentEnablePerspectiveCorrection = settings.EnablePerspectiveCorrection;
        _settings.PixelAlignmentTargetEdgeAngleDegrees = settings.TargetEdgeAngleDegrees;
        _settings.PixelAlignmentLastInputDirectory = settings.LastInputDirectory;
        _settings.PixelAlignmentLastOutputDirectory = settings.LastOutputDirectory;
    }

    private void OnImageOutlineSettingsChanged(
        object? sender,
        ImageOutlineSettingsChangedEventArgs e)
    {
        ApplyImageOutlineSettings(e.Settings);
        SaveSettings();
    }

    private void ApplyImageOutlineSettings(ImageOutlineToolSettingsSnapshot settings)
    {
        _settings.ImageOutlinePlacement = settings.Placement;
        _settings.ImageOutlineWidth = settings.Width;
        _settings.ImageOutlineColorHex = settings.ColorHex;
        _settings.ImageOutlineAlphaThreshold = settings.AlphaThreshold;
        _settings.ImageOutlineExpandCanvas = settings.ExpandCanvas;
        _settings.ImageOutlineLastInputDirectory = settings.LastInputDirectory;
        _settings.ImageOutlineLastOutputDirectory = settings.LastOutputDirectory;
    }

    private void OnIconDesignSettingsChanged(
        object? sender,
        IconDesignSettingsChangedEventArgs e)
    {
        ApplyIconDesignSettings(e.Settings);
        SaveSettings();
    }

    private void ApplyIconDesignSettings(IconDesignSettingsSnapshot settings)
    {
        _settings.IconDesignModeKey = settings.Mode;
        _settings.IconDesignImageWidth = settings.ImageWidth;
        _settings.IconDesignImageHeight = settings.ImageHeight;
        _settings.IconDesignCameraAzimuthDegrees = settings.CameraAzimuthDegrees;
        _settings.IconDesignCameraElevationDegrees = settings.CameraElevationDegrees;
        _settings.IconDesignCameraRollDegrees = settings.CameraRollDegrees;
        _settings.IconDesignCameraDistance = settings.CameraDistance;
        _settings.IconDesignFocalLengthMillimeters = settings.FocalLengthMillimeters;
    }

    private void OnAssetGenerationStyleLibraryRequested(object? sender, EventArgs e) =>
        PanelShowRequested?.Invoke(this, "art-style-library");

    private void OnAssetGenerationAgentConnectionRequested(object? sender, EventArgs e) =>
        PanelShowRequested?.Invoke(this, "mcp-agent-connection");

    private void OnAssetGenerationBackgroundRemovalRequested(object? sender, EventArgs e) =>
        PanelShowRequested?.Invoke(this, "background-removal");

    private static string? CompactIdentifier(string? identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            return null;
        }

        var trimmed = identifier.Trim();
        return trimmed.Length <= 12 ? trimmed : trimmed[..12];
    }

    private void RegisterConsoleCommands()
    {
        CommandConsole.RegisterCommand(
            "status",
            "status",
            "显示当前项目、工作流、Agent 链路和生成批次状态。",
            HandleConsoleStatusAsync,
            "state");
        CommandConsole.RegisterCommand(
            "show",
            "show <console|project|generation|background|outline|icon|pixel|blueprint|agent|style|tools|canvas|properties|layers>",
            "拉起并聚焦指定的 AvalonDock 停靠页。",
            HandleConsoleShowAsync,
            "panel.show");
        CommandConsole.RegisterCommand(
            "theme",
            "theme [dark|light|toggle]",
            "查看或切换工作台亮暗主题。",
            HandleConsoleThemeAsync);
        CommandConsole.RegisterCommand(
            "agent",
            "agent [status|start|stop]",
            "查看、启动或停止应用内 Agent 与 MCP 链路。",
            HandleConsoleAgentAsync);
        CommandConsole.RegisterCommand(
            "generation",
            "generation [status|timeline|start|publish]",
            "查看素材生成状态/阶段耗时，直接开始生成，或仅发布到本地队列。",
            HandleConsoleGenerationAsync,
            "gen");
    }

    private Task<CommandConsoleResult?> HandleConsoleStatusAsync(
        CommandConsoleInvocation invocation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (invocation.Arguments.Count > 0)
        {
            return ConsoleResult(CommandConsoleResult.Failure("用法：status"));
        }

        var batch = AssetGeneration.HasPublishedBatch
            ? $"{AssetGeneration.CurrentTaskStatusText} · {AssetGeneration.CurrentTaskDetail}"
            : "尚无生成批次";
        return ConsoleResult(CommandConsoleResult.Info(
            $"项目：{CurrentProjectDisplayName} ({CurrentProjectPath ?? "未创建"}){Environment.NewLine}" +
            $"工作流：{CurrentWorkflow.Label}{Environment.NewLine}" +
            $"Agent：{AgentConnection.StatusText}{Environment.NewLine}" +
            $"生成：{batch}"));
    }

    private Task<CommandConsoleResult?> HandleConsoleShowAsync(
        CommandConsoleInvocation invocation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (invocation.Arguments.Count != 1 ||
            !TryResolveConsolePanel(invocation.Arguments[0], out var contentId, out var title))
        {
            return ConsoleResult(CommandConsoleResult.Failure(
                "用法：show <console|project|generation|background|outline|icon|pixel|blueprint|agent|style|tools|canvas|properties|layers>"));
        }

        PanelShowRequested?.Invoke(this, contentId);
        return ConsoleResult(CommandConsoleResult.Success($"已拉起停靠页：{title}"));
    }

    private Task<CommandConsoleResult?> HandleConsoleThemeAsync(
        CommandConsoleInvocation invocation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (invocation.Arguments.Count == 0)
        {
            return ConsoleResult(CommandConsoleResult.Info($"当前主题：{CurrentThemeLabel}"));
        }

        if (invocation.Arguments.Count != 1)
        {
            return ConsoleResult(CommandConsoleResult.Failure("用法：theme [dark|light|toggle]"));
        }

        switch (invocation.Arguments[0].Trim().ToLowerInvariant())
        {
            case "dark":
            case "暗色":
                SetTheme(AppTheme.Dark);
                break;
            case "light":
            case "亮色":
                SetTheme(AppTheme.Light);
                break;
            case "toggle":
            case "切换":
                ToggleThemeCommand.Execute(null);
                break;
            default:
                return ConsoleResult(CommandConsoleResult.Failure("主题只支持 dark、light 或 toggle。"));
        }

        return ConsoleResult(CommandConsoleResult.Success($"主题已切换为：{CurrentThemeLabel}"));
    }

    private Task<CommandConsoleResult?> HandleConsoleAgentAsync(
        CommandConsoleInvocation invocation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var action = invocation.Arguments.Count == 0
            ? "status"
            : invocation.Arguments.Count == 1
                ? invocation.Arguments[0].Trim().ToLowerInvariant()
                : string.Empty;
        switch (action)
        {
            case "status":
            case "状态":
                return ConsoleResult(CommandConsoleResult.Info(
                    $"{AgentConnection.StatusText} · {AgentConnection.RuntimeModeText} · " +
                    $"MCP：{AgentConnection.McpToolStatusText}"));
            case "start":
            case "启动":
                if (!AgentConnection.StartCommand.CanExecute(null))
                {
                    return ConsoleResult(CommandConsoleResult.Failure(
                        $"当前无法启动 Agent：{AgentConnection.StatusText}"));
                }

                AgentConnection.StartCommand.Execute(null);
                return ConsoleResult(CommandConsoleResult.Success("Agent 与 MCP 链路启动请求已提交。"));
            case "stop":
            case "停止":
                if (!AgentConnection.StopCommand.CanExecute(null))
                {
                    return ConsoleResult(CommandConsoleResult.Failure("当前没有可停止的 Agent 会话。"));
                }

                AgentConnection.StopCommand.Execute(null);
                return ConsoleResult(CommandConsoleResult.Success("Agent 与 MCP 链路停止请求已提交。"));
            default:
                return ConsoleResult(CommandConsoleResult.Failure("用法：agent [status|start|stop]"));
        }
    }

    private Task<CommandConsoleResult?> HandleConsoleGenerationAsync(
        CommandConsoleInvocation invocation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var action = invocation.Arguments.Count == 0
            ? "status"
            : invocation.Arguments.Count == 1
                ? invocation.Arguments[0].Trim().ToLowerInvariant()
                : string.Empty;
        switch (action)
        {
            case "status":
            case "状态":
                return ConsoleResult(CommandConsoleResult.Info(
                    $"{AssetGeneration.StatusText} · 配置可发布：{AssetGeneration.CanCreateTask} · " +
                    $"可直接生成：{AssetGeneration.CanStartGeneration}"));
            case "timeline":
            case "时间线":
                return ConsoleResult(CommandConsoleResult.Info(
                    $"批次：{AssetGeneration.CurrentDispatchBatchId}{Environment.NewLine}" +
                    $"阶段：{AssetGeneration.CurrentDispatchStageText} · {AssetGeneration.CurrentStageElapsedText}{Environment.NewLine}" +
                    $"{AssetGeneration.LongestStageText}{Environment.NewLine}" +
                    $"图片：{AssetGeneration.GeneratedImageCount}/{AssetGeneration.RequestedImageCount} 已落盘 · " +
                    $"{AssetGeneration.VerifiedImageCount} 已验证"));
            case "start":
            case "开始":
                if (!AssetGeneration.StartGenerationCommand.CanExecute(null))
                {
                    return ConsoleResult(CommandConsoleResult.Failure(
                        $"当前无法开始生成：{AssetGeneration.ConnectionStatusText}；{AssetGeneration.StatusText}"));
                }

                AssetGeneration.StartGenerationCommand.Execute(null);
                return ConsoleResult(CommandConsoleResult.Success("开始生成请求已提交到应用内 Agent 链路。"));
            case "publish":
            case "发布":
                if (!AssetGeneration.CreateTaskCommand.CanExecute(null))
                {
                    return ConsoleResult(CommandConsoleResult.Failure(
                        $"当前无法发布队列：{AssetGeneration.StatusText}"));
                }

                AssetGeneration.CreateTaskCommand.Execute(null);
                return ConsoleResult(CommandConsoleResult.Success("当前配置已请求写入本地生成队列。"));
            default:
                return ConsoleResult(CommandConsoleResult.Failure(
                    "用法：generation [status|timeline|start|publish]"));
        }
    }

    private static Task<CommandConsoleResult?> ConsoleResult(CommandConsoleResult result) =>
        Task.FromResult<CommandConsoleResult?>(result);

    private static bool TryResolveConsolePanel(
        string alias,
        out string contentId,
        out string title)
    {
        (contentId, title) = alias.Trim().ToLowerInvariant() switch
        {
            "console" or "log" or "日志" => ("command-console", "指令日志"),
            "project" or "项目" => ("ui-project", "项目"),
            "generation" or "gen" or "素材生成" => ("asset-generation", "素材生成"),
            "background" or "chroma" or "cutout" or "抠图" or "背景剔除" =>
                ("background-removal", "背景剔除工具"),
            "outline" or "stroke" or "描边" or "图片描边" =>
                ("image-outline", "图片描边工具"),
            "icon" or "icon-design" or "图标" or "图标制作" =>
                ("icon-design", "Icon 图标制作"),
            "pixel" or "pixel-grid" or "像素对齐" or "像素点对齐" =>
                ("pixel-grid-alignment", "像素点对齐工具"),
            "blueprint" or "graph" or "蓝图" => ("workflow-blueprint", "蓝图窗口"),
            "agent" or "mcp" => ("mcp-agent-connection", "MCP Agent 接入"),
            "style" or "风格" => ("art-style-library", "美术风格素材库"),
            "tools" or "工具" => ("ui-tools", "工具"),
            "canvas" or "main" or "主界面" => ("ui-canvas", "主界面"),
            "properties" or "property" or "属性" => ("ui-properties", "属性"),
            "layers" or "layer" or "图层" => ("ui-layers", "图层"),
            _ => (string.Empty, string.Empty)
        };
        return contentId.Length > 0;
    }

    private IImageProcessingPreviewCache CreateImageProcessingPreviewCache(WorkspacePaths paths)
    {
        try
        {
            var cache = new ImageProcessingPreviewCache(paths);
            var cleanup = cache.CleanupExpiredAndExcessPreviews();
            if (cleanup.RemovedFileCount > 0)
            {
                _workbenchConsoleService.Write(
                    WorkbenchLogLevel.Information,
                    "ImageTools",
                    $"已清理 {cleanup.RemovedFileCount} 个过期图像预览缓存，释放 {cleanup.RemovedByteCount:N0} 字节。");
            }

            return cache;
        }
        catch (Exception exception)
        {
            var fallbackRoot = Path.Combine(
                Path.GetTempPath(),
                "GAssetForge",
                "Workspace",
                ImageProcessingPreviewCache.DefaultDirectoryName);
            _workbenchConsoleService.Write(
                WorkbenchLogLevel.Warning,
                "ImageTools",
                $"工作区预览缓存不可用，已回退到系统临时目录：{exception.Message}",
                exception: exception);
            return new ImageProcessingPreviewCache(fallbackRoot);
        }
    }

    private void SaveSettings()
    {
        try
        {
            _settingsService.Save(_settings);
        }
        catch
        {
            // A settings write must never make the editor unusable. The next
            // successful state change or normal shutdown will retry the save.
        }
    }

    private void CaptureTilemapSettings()
    {
        _settings.TilemapGridUnitSize = TilemapWorkspace.GridUnitSize;
        _settings.TilemapArtImageWidth = TilemapWorkspace.ArtImageWidth;
        _settings.TilemapArtImageHeight = TilemapWorkspace.ArtImageHeight;
        _settings.TilemapDiamondAngle = TilemapWorkspace.DiamondAngle;
        _settings.TilemapRotationAngle = TilemapWorkspace.RotationAngle;
    }

    private ProjectTilemapSettings BuildProjectTilemapSettings() => new()
    {
        GridUnitSize = TilemapWorkspace.GridUnitSize,
        ArtImageWidth = TilemapWorkspace.ArtImageWidth,
        ArtImageHeight = TilemapWorkspace.ArtImageHeight,
        DiamondAngle = TilemapWorkspace.DiamondAngle,
        RotationAngle = TilemapWorkspace.RotationAngle
    };

    private void CaptureAssetGenerationSettings()
    {
        _settings.AssetGenerationStepKey = AssetGeneration.SelectedGenerationStep.Key;
        _settings.AssetGenerationContentPrompt = AssetGeneration.ContentPrompt;
        _settings.AssetGenerationNegativePrompt = AssetGeneration.NegativePrompt;
        _settings.AssetGenerationMaxParallelism = AssetGeneration.MaxParallelism;
        _settings.AssetGenerationAutoRetryEnabled = AssetGeneration.AutoRetryEnabled;
        _settings.AssetGenerationAutoRetryCount = AssetGeneration.AutoRetryCount;
        _settings.AssetGenerationPreferUnifiedStylePrompt = AssetGeneration.PreferUnifiedStylePrompt;
        _settings.AssetGenerationOutputPath = AssetGeneration.OutputDirectory;
        _settings.AssetGenerationRows = AssetGeneration.DraftRows
            .Select(row => new AssetGenerationDraftRowSettings
            {
                Id = row.Id,
                Description = row.Description,
                Quantity = row.Quantity,
                OutputPath = row.OutputDirectory
            })
            .ToList();
    }
}
