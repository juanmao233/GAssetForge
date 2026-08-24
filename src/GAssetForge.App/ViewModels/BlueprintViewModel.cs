using GAssetForge.App.Infrastructure;
using GAssetForge.App.Services;
using GAssetForge.Core.Blueprints;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace GAssetForge.App.ViewModels;

public enum BlueprintEditorTool
{
    Pointer,
    Add,
    Connect,
    MoveNode
}

public sealed record BlueprintNodeTypeOption(
    BlueprintNodeType Type,
    string Label,
    string Description);

public sealed record BlueprintPlacementPreview(
    BlueprintNodeTypeOption NodeType,
    double X,
    double Y,
    double Width,
    double Height);

public sealed record BlueprintConnectionPreview(
    string NodeId,
    string PortId,
    BlueprintPortDirection Direction,
    double PointerX,
    double PointerY,
    Geometry Geometry);

public sealed class BlueprintViewModel : ObservableObject, IDisposable
{
    private const double MinimumZoom = 0.25d;
    private const double MaximumZoom = 2.5d;

    private readonly JsonBlueprintRepository _repository;
    private readonly IBlueprintExecutionDispatcher _executionDispatcher;
    private readonly WorkbenchConsoleService _console;
    private readonly DispatcherTimer _saveTimer;
    private BlueprintGraph _graph = null!;
    private BlueprintEditorTool _activeTool = BlueprintEditorTool.Pointer;
    private BlueprintNodeTypeOption _selectedAddNodeType;
    private BlueprintPortViewModel? _pendingConnectionPort;
    private BlueprintPlacementPreview? _placementPreview;
    private BlueprintConnectionPreview? _connectionPreview;
    private string? _projectRoot;
    private string _workflowKey = string.Empty;
    private string _currentFilePath = string.Empty;
    private string _statusMessage = "蓝图工作区正在初始化";
    private string _lastRunDirectory = string.Empty;
    private bool _hasPendingSave;
    private bool _hasDeferredViewportChange;
    private readonly HashSet<string> _deferredMovedNodeIds = new(StringComparer.Ordinal);
    private bool _isExecuting;
    private bool _disposed;

    public BlueprintViewModel(
        JsonBlueprintRepository repository,
        IBlueprintExecutionDispatcher executionDispatcher,
        WorkbenchConsoleService console)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _executionDispatcher = executionDispatcher ?? throw new ArgumentNullException(nameof(executionDispatcher));
        _console = console ?? throw new ArgumentNullException(nameof(console));
        AddNodeTypes =
        [
            new(BlueprintNodeType.Description, "说明蓝图", "输入 Agent 需要理解或执行的说明文字"),
            new(BlueprintNodeType.Tool, "工具类蓝图", "调用像素点对齐、背景剔除等稳定 Harness 工具"),
            new(BlueprintNodeType.FunctionCollection, "功能集合蓝图", "收集多个节点并保存为可引用功能"),
            new(BlueprintNodeType.Logic, "逻辑类蓝图", "使用自然语言 If 条件选择 True / False 分支")
        ];
        _selectedAddNodeType = AddNodeTypes[0];
        SelectToolCommand = new RelayCommand<string>(SelectTool);
        SelectAddNodeTypeCommand = new RelayCommand<BlueprintNodeTypeOption>(SelectAddNodeType);
        ResetViewportCommand = new RelayCommand(ResetViewportToOrigin);
        ClearSelectionCommand = new RelayCommand(() => SelectNode(null, additive: false));
        SaveCommand = new RelayCommand(() => SaveNow());
        _saveTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(450)
        };
        _saveTimer.Tick += OnSaveTimerTick;
    }

    public string Header => "Agent 工作流蓝图";

    public string Description =>
        "节点图负责定义顺序、工具合同和 Agent 说明；执行时会冻结为版本化 Harness 计划。";

    public ObservableCollection<BlueprintNodeViewModel> Nodes { get; } = [];

    public ObservableCollection<BlueprintConnectionViewModel> Connections { get; } = [];

    public IReadOnlyList<BlueprintNodeTypeOption> AddNodeTypes { get; }

    public IReadOnlyList<BlueprintToolDefinition> ToolDefinitions => BlueprintToolCatalog.All;

    public BlueprintEditorTool ActiveTool
    {
        get => _activeTool;
        private set
        {
            if (!SetProperty(ref _activeTool, value))
            {
                return;
            }

            CancelTransientInteractionCore();
            OnPropertyChanged(nameof(IsPointerTool));
            OnPropertyChanged(nameof(IsAddTool));
            OnPropertyChanged(nameof(IsConnectTool));
            OnPropertyChanged(nameof(IsMoveNodeTool));
            OnPropertyChanged(nameof(ActiveToolLabel));
            OnPropertyChanged(nameof(StatusText));
        }
    }

    public bool IsPointerTool => ActiveTool == BlueprintEditorTool.Pointer;

    public bool IsAddTool => ActiveTool == BlueprintEditorTool.Add;

    public bool IsConnectTool => ActiveTool == BlueprintEditorTool.Connect;

    public bool IsMoveNodeTool => ActiveTool == BlueprintEditorTool.MoveNode;

    public string ActiveToolLabel => ActiveTool switch
    {
        BlueprintEditorTool.Pointer => "默认鼠标",
        BlueprintEditorTool.Add => "添加蓝图",
        BlueprintEditorTool.Connect => "连线",
        BlueprintEditorTool.MoveNode => "对象移动",
        _ => "工具"
    };

    public BlueprintNodeTypeOption SelectedAddNodeType
    {
        get => _selectedAddNodeType;
        set
        {
            var requested = value ?? AddNodeTypes[0];
            var next = AddNodeTypes.FirstOrDefault(option => option.Type == requested.Type)
                ?? AddNodeTypes[0];
            if (!SetProperty(ref _selectedAddNodeType, next))
            {
                return;
            }

            OnPropertyChanged(nameof(SelectedAddNodeTypeLabel));
            if (PlacementPreview is { } preview)
            {
                PlacementPreview = preview with { NodeType = next };
            }
        }
    }

    public string SelectedAddNodeTypeLabel => SelectedAddNodeType.Label;

    public BlueprintPlacementPreview? PlacementPreview
    {
        get => _placementPreview;
        private set
        {
            if (SetProperty(ref _placementPreview, value))
            {
                OnPropertyChanged(nameof(HasPlacementPreview));
            }
        }
    }

    public bool HasPlacementPreview => PlacementPreview is not null;

    public BlueprintConnectionPreview? ConnectionPreview
    {
        get => _connectionPreview;
        private set => SetProperty(ref _connectionPreview, value);
    }

    public bool HasPendingConnection => _pendingConnectionPort is not null;

    public double Zoom => _graph?.Viewport.Zoom ?? 1d;

    public double PanX => _graph?.Viewport.PanX ?? 0d;

    public double PanY => _graph?.Viewport.PanY ?? 0d;

    public string ZoomText => $"{Zoom:P0}";

    public string StatusText =>
        $"{Nodes.Count} 个节点 · {Connections.Count} 条连接 · {ActiveToolLabel} · {_statusMessage}";

    public string PersistenceText => string.IsNullOrWhiteSpace(_currentFilePath)
        ? "尚未激活项目级蓝图"
        : _hasPendingSave
            ? $"待保存 · {_currentFilePath}"
            : $"已保存 · {_currentFilePath}";

    public string LastRunDirectory
    {
        get => _lastRunDirectory;
        private set => SetProperty(ref _lastRunDirectory, value);
    }

    public bool IsExecuting
    {
        get => _isExecuting;
        private set
        {
            if (SetProperty(ref _isExecuting, value))
            {
                foreach (var node in Nodes)
                {
                    node.RaiseExecutionCommandState();
                }
            }
        }
    }

    public ICommand SelectToolCommand { get; }

    public ICommand SelectAddNodeTypeCommand { get; }

    public ICommand ResetViewportCommand { get; }

    public ICommand ClearSelectionCommand { get; }

    public ICommand SaveCommand { get; }

    public bool Activate(string? projectRoot, string workflowKey)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var targetProjectRoot = string.IsNullOrWhiteSpace(projectRoot)
            ? null
            : Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot));
        if (_graph is not null &&
            string.Equals(_projectRoot, targetProjectRoot, PathComparison) &&
            string.Equals(_workflowKey, workflowKey, StringComparison.Ordinal))
        {
            return true;
        }

        if (!SaveNow())
        {
            _statusMessage = "当前蓝图未能安全保存，已取消切换以避免项目串写";
            NotifySummaryChanged();
            return false;
        }

        BlueprintLoadResult result;
        try
        {
            result = _repository.LoadOrCreate(targetProjectRoot, workflowKey);
        }
        catch (Exception exception)
        {
            _statusMessage = $"蓝图切换失败，已保留当前图：{exception.Message}";
            _console.WriteError("Blueprint", _statusMessage, exception);
            NotifySummaryChanged();
            return false;
        }

        _projectRoot = targetProjectRoot;
        _workflowKey = workflowKey;
        _currentFilePath = result.FilePath;
        LoadGraph(result.Graph);
        _statusMessage = result.RecoverySource switch
        {
            "backup" => "已从备份恢复",
            "safe-default" => "原文件损坏，已使用未落盘安全默认图",
            "default" => "已创建默认起始蓝图",
            _ => "蓝图已加载"
        };
        if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
        {
            _console.WriteWarning("Blueprint", result.ErrorMessage);
        }

        NotifySummaryChanged();
        return true;
    }

    public BlueprintNodeViewModel AddNode(double x, double y) =>
        AddNode(SelectedAddNodeType, x, y);

    public void BeginPlacementPreview(double x, double y)
    {
        EnsureActivated();
        if (!IsAddTool)
        {
            ActiveTool = BlueprintEditorTool.Add;
        }

        PlacementPreview = CreatePlacementPreview(SelectedAddNodeType, x, y);
        _statusMessage = $"正在预放置{SelectedAddNodeType.Label}；移动鼠标调整位置，再次左键确认";
        OnPropertyChanged(nameof(StatusText));
    }

    public void UpdatePlacementPreview(double x, double y)
    {
        if (PlacementPreview is not { } preview)
        {
            return;
        }

        PlacementPreview = CreatePlacementPreview(preview.NodeType, x, y);
    }

    public BlueprintNodeViewModel? CommitPlacementPreview(double x, double y)
    {
        if (PlacementPreview is not { } preview)
        {
            return null;
        }

        preview = CreatePlacementPreview(preview.NodeType, x, y);
        PlacementPreview = null;
        return AddNode(preview.NodeType, preview.X, preview.Y);
    }

    public bool CancelTransientInteraction()
    {
        var cancelled = CancelTransientInteractionCore();
        if (cancelled)
        {
            _statusMessage = "已取消当前蓝图操作";
            OnPropertyChanged(nameof(StatusText));
        }

        return cancelled;
    }

    public void ResetViewportToOrigin()
    {
        SetViewport(0d, 0d, 1d, commit: true);
        _statusMessage = "已返回蓝图坐标原点 (0, 0)";
        OnPropertyChanged(nameof(StatusText));
    }

    public void SelectAddNodeType(BlueprintNodeTypeOption? option)
    {
        if (option is null || !AddNodeTypes.Contains(option))
        {
            return;
        }

        SelectedAddNodeType = option;
        if (ActiveTool == BlueprintEditorTool.Add)
        {
            NotifyActiveToolSelection();
        }
        else
        {
            ActiveTool = BlueprintEditorTool.Add;
        }

        _statusMessage = $"已选择{option.Label}；在画布左键预览，再次左键确认";
        OnPropertyChanged(nameof(StatusText));
    }

    private BlueprintNodeViewModel AddNode(BlueprintNodeTypeOption nodeType, double x, double y)
    {
        EnsureActivated();
        var node = BlueprintGraphFactory.CreateNode(nodeType.Type, x, y);
        node.X = Math.Clamp(node.X, 0d, CanvasWidth - node.Width);
        node.Y = Math.Clamp(node.Y, 0d, CanvasHeight - MaximumEstimatedNodeHeight);
        if (node.Type == BlueprintNodeType.FunctionCollection)
        {
            node.FunctionMemberNodeIds = Nodes
                .Where(candidate => candidate.IsSelected && !candidate.IsStart)
                .Select(candidate => candidate.Id)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToList();
        }

        _graph.Nodes.Add(node);
        var viewModel = CreateNodeViewModel(node);
        Nodes.Add(viewModel);
        SelectNode(viewModel, additive: false);
        _statusMessage = $"已添加{nodeType.Label}";
        MarkChanged();
        NotifySummaryChanged();
        return viewModel;
    }

    public void SelectNode(BlueprintNodeViewModel? node, bool additive)
    {
        if (!additive)
        {
            foreach (var candidate in Nodes)
            {
                candidate.IsSelected = ReferenceEquals(candidate, node);
            }
        }
        else if (node is not null)
        {
            node.IsSelected = !node.IsSelected;
        }

        _statusMessage = node is null ? "未选择节点" : $"已选择：{node.Title}";
        OnPropertyChanged(nameof(StatusText));
    }

    public void MoveNode(BlueprintNodeViewModel node, double x, double y, bool commit)
    {
        ArgumentNullException.ThrowIfNull(node);
        var targetX = Math.Round(Math.Clamp(x, 0d, CanvasWidth - node.Width), 1);
        var targetY = Math.Round(Math.Clamp(y, 0d, CanvasHeight - MaximumEstimatedNodeHeight), 1);
        var changed = !targetX.Equals(node.X) || !targetY.Equals(node.Y);
        if (changed)
        {
            node.SetPosition(targetX, targetY);
            RefreshConnections(node.Id);
        }

        if (!commit)
        {
            if (changed && _deferredMovedNodeIds.Add(node.Id))
            {
                MarkChanged(scheduleSave: false);
            }

            return;
        }

        if (changed || _deferredMovedNodeIds.Remove(node.Id))
        {
            _statusMessage = $"已整理节点：{node.Title}";
            MarkChanged();
        }
    }

    public void SetViewport(double panX, double panY, double zoom, bool commit)
    {
        EnsureActivated();
        var targetPanX = Math.Round(panX, 2);
        var targetPanY = Math.Round(panY, 2);
        var targetZoom = Math.Clamp(zoom, MinimumZoom, MaximumZoom);
        var changed = !targetPanX.Equals(_graph.Viewport.PanX) ||
                      !targetPanY.Equals(_graph.Viewport.PanY) ||
                      !targetZoom.Equals(_graph.Viewport.Zoom);
        if (changed)
        {
            _graph.Viewport.PanX = targetPanX;
            _graph.Viewport.PanY = targetPanY;
            _graph.Viewport.Zoom = targetZoom;
            OnPropertyChanged(nameof(PanX));
            OnPropertyChanged(nameof(PanY));
            OnPropertyChanged(nameof(Zoom));
            OnPropertyChanged(nameof(ZoomText));
        }

        if (!commit)
        {
            if (changed && !_hasDeferredViewportChange)
            {
                _hasDeferredViewportChange = true;
                MarkChanged(scheduleSave: false);
            }

            return;
        }

        if (changed || _hasDeferredViewportChange)
        {
            _hasDeferredViewportChange = false;
            _statusMessage = "视口已更新";
            MarkChanged();
        }
    }

    public void HandlePortClicked(BlueprintPortViewModel port)
    {
        ArgumentNullException.ThrowIfNull(port);
        if (!IsConnectTool)
        {
            _statusMessage = "请先选择连线工具";
            OnPropertyChanged(nameof(StatusText));
            return;
        }

        if (_pendingConnectionPort is null)
        {
            BeginPendingConnection(port);
            return;
        }

        if (ReferenceEquals(_pendingConnectionPort, port))
        {
            CancelPendingConnection();
            _statusMessage = "已取消待连接端点";
            OnPropertyChanged(nameof(StatusText));
            return;
        }

        if (port.Direction == _pendingConnectionPort.Direction)
        {
            CancelPendingConnection();
            BeginPendingConnection(port);
            _statusMessage = $"已改选{GetDirectionLabel(port.Direction)}端点 {port.Node.Title}.{port.Name}，请选择相反方向端点";
            OnPropertyChanged(nameof(StatusText));
            return;
        }

        var output = port.Direction == BlueprintPortDirection.Output
            ? port
            : _pendingConnectionPort;
        var input = port.Direction == BlueprintPortDirection.Input
            ? port
            : _pendingConnectionPort;
        TryCreateConnection(output, input);
    }

    public void CancelPendingConnectionSelection()
    {
        if (!CancelPendingConnection())
        {
            return;
        }

        _statusMessage = "已取消待连接端点";
        OnPropertyChanged(nameof(StatusText));
    }

    public void UpdatePendingConnectionPointer(double x, double y)
    {
        if (_pendingConnectionPort is not { } port)
        {
            return;
        }

        var pointer = new Point(x, y);
        ConnectionPreview = new BlueprintConnectionPreview(
            port.Node.Id,
            port.Id,
            port.Direction,
            x,
            y,
            BlueprintConnectionGeometry.CreatePreview(port, pointer));
    }

    public void AddPort(BlueprintNodeViewModel node, BlueprintPortDirection direction)
    {
        ArgumentNullException.ThrowIfNull(node);
        CancelPendingConnection();
        if (node.IsStart && direction == BlueprintPortDirection.Input)
        {
            _statusMessage = "起始蓝图不能添加输入端点";
            OnPropertyChanged(nameof(StatusText));
            return;
        }

        var existing = direction == BlueprintPortDirection.Input ? node.InputPorts : node.OutputPorts;
        var prefix = direction == BlueprintPortDirection.Input ? "输入" : "输出";
        var nameIndex = 1;
        string name;
        do
        {
            name = $"{prefix} {nameIndex++}";
        }
        while (existing.Any(port => string.Equals(port.Name, name, StringComparison.OrdinalIgnoreCase)));

        var order = existing.Select(port => port.Order).DefaultIfEmpty(-1).Max() + 1;
        var port = BlueprintGraphFactory.CreatePort(name, direction, order);
        node.Model.Ports.Add(port);
        node.RebuildPorts();
        _statusMessage = $"已为 {node.Title} 添加{name}端点";
        MarkChanged();
        RefreshConnections(node.Id);
    }

    public void RemovePort(BlueprintPortViewModel port)
    {
        ArgumentNullException.ThrowIfNull(port);
        if (!port.CanRemove)
        {
            _statusMessage = "该系统必需端点不能删除";
            OnPropertyChanged(nameof(StatusText));
            return;
        }

        CancelPendingConnection();
        var connected = Connections.Where(connection =>
                string.Equals(connection.Model.SourceNodeId, port.Node.Id, StringComparison.Ordinal) &&
                string.Equals(connection.Model.SourcePortId, port.Id, StringComparison.Ordinal) ||
                string.Equals(connection.Model.TargetNodeId, port.Node.Id, StringComparison.Ordinal) &&
                string.Equals(connection.Model.TargetPortId, port.Id, StringComparison.Ordinal))
            .ToArray();
        foreach (var connection in connected)
        {
            _graph.Connections.Remove(connection.Model);
            Connections.Remove(connection);
        }

        port.Node.Model.Ports.Remove(port.Model);
        port.Node.RebuildPorts();
        RefreshConnections(port.Node.Id);
        _statusMessage = $"已删除端点：{port.Node.Title}.{port.Name}";
        MarkChanged();
        NotifySummaryChanged();
    }

    public void RemoveNode(BlueprintNodeViewModel node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (node.IsStart)
        {
            _statusMessage = "系统起始蓝图不可删除";
            OnPropertyChanged(nameof(StatusText));
            return;
        }

        CancelPendingConnection();
        var connectionModels = _graph.Connections
            .Where(connection =>
                string.Equals(connection.SourceNodeId, node.Id, StringComparison.Ordinal) ||
                string.Equals(connection.TargetNodeId, node.Id, StringComparison.Ordinal))
            .ToArray();
        foreach (var connection in connectionModels)
        {
            _graph.Connections.Remove(connection);
            var viewModel = Connections.First(item => ReferenceEquals(item.Model, connection));
            Connections.Remove(viewModel);
        }

        foreach (var collection in Nodes.Where(candidate => candidate.IsFunctionCollection))
        {
            collection.Model.FunctionMemberNodeIds.RemoveAll(memberId =>
                string.Equals(memberId, node.Id, StringComparison.Ordinal));
            collection.NotifyFunctionMembersChanged();
        }

        _graph.Nodes.Remove(node.Model);
        Nodes.Remove(node);
        _statusMessage = $"已删除节点：{node.Title}";
        MarkChanged();
        NotifySummaryChanged();
    }

    public void RemoveConnection(BlueprintConnectionViewModel connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        _graph.Connections.Remove(connection.Model);
        Connections.Remove(connection);
        _statusMessage = "已删除连线";
        MarkChanged();
        NotifySummaryChanged();
    }

    public void CaptureFunctionMembers(BlueprintNodeViewModel functionNode)
    {
        ArgumentNullException.ThrowIfNull(functionNode);
        var members = Nodes
            .Where(node => node.IsSelected && !node.IsStart && !ReferenceEquals(node, functionNode))
            .Select(node => node.Id)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToList();
        functionNode.Model.FunctionMemberNodeIds = members;
        functionNode.NotifyFunctionMembersChanged();
        _statusMessage = members.Count == 0
            ? "功能集合已保存为空集合；可 Ctrl 多选节点后再次收集"
            : $"功能集合已保存 {members.Count} 个成员节点";
        MarkChanged();
    }

    public async Task ExecuteAsync(string startNodeId)
    {
        if (IsExecuting)
        {
            return;
        }

        IsExecuting = true;
        try
        {
            if (!SaveNow())
            {
                return;
            }
            var frozenGraph = BlueprintJson.Clone(_graph);
            var plan = BlueprintExecutionPlanner.Compile(frozenGraph, startNodeId);
            _console.WriteStructured(
                WorkbenchLogLevel.Information,
                "Blueprint",
                $"开始编译蓝图执行计划：{frozenGraph.Title}",
                new WorkbenchLogContext
                {
                    BlueprintGraphId = frozenGraph.Id,
                    BlueprintNodeId = startNodeId,
                    Stage = "blueprint.plan.compile",
                    Status = "running"
                });
            var receipt = await _executionDispatcher
                .DispatchAsync(plan, _projectRoot)
                .ConfigureAwait(true);
            LastRunDirectory = receipt.RunDirectory;
            _statusMessage = $"执行计划已发布：{receipt.RunId}";
            _console.WriteStructured(
                WorkbenchLogLevel.Success,
                "Blueprint",
                $"蓝图计划已落盘：{receipt.RunDirectory}",
                new WorkbenchLogContext
                {
                    RunId = receipt.RunId,
                    BlueprintGraphId = frozenGraph.Id,
                    BlueprintNodeId = startNodeId,
                    Stage = "blueprint.plan.persisted",
                    Status = receipt.Status,
                    ObservedPath = receipt.RunDirectory
                });
        }
        catch (Exception exception)
        {
            _statusMessage = $"蓝图发布失败：{exception.Message}";
            _console.WriteError("Blueprint", _statusMessage, exception);
        }
        finally
        {
            IsExecuting = false;
            OnPropertyChanged(nameof(StatusText));
        }
    }

    public bool SaveNow()
    {
        if (_disposed || _graph is null || string.IsNullOrWhiteSpace(_workflowKey) || !_hasPendingSave)
        {
            return true;
        }

        try
        {
            BlueprintGraphValidator.Validate(_graph);
            _repository.Save(_projectRoot, _workflowKey, _graph);
            _hasPendingSave = false;
            _hasDeferredViewportChange = false;
            _deferredMovedNodeIds.Clear();
            _saveTimer.Stop();
            _statusMessage = "蓝图已保存";
            NotifySummaryChanged();
            return true;
        }
        catch (Exception exception)
        {
            _statusMessage = $"暂未保存：{exception.Message}";
            _console.WriteWarning("Blueprint", _statusMessage);
            NotifySummaryChanged();
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        CancelTransientInteractionCore();
        _ = SaveNow();
        _disposed = true;
        _saveTimer.Stop();
        _saveTimer.Tick -= OnSaveTimerTick;
    }

    internal void NodeChanged(BlueprintNodeViewModel node)
    {
        RefreshConnections(node.Id);
        MarkChanged();
    }

    private void LoadGraph(BlueprintGraph graph)
    {
        BlueprintGraphValidator.Validate(graph);
        CancelTransientInteractionCore();
        _hasDeferredViewportChange = false;
        _deferredMovedNodeIds.Clear();
        _graph = graph;
        Nodes.Clear();
        Connections.Clear();
        foreach (var node in graph.Nodes)
        {
            Nodes.Add(CreateNodeViewModel(node));
        }

        foreach (var connection in graph.Connections)
        {
            Connections.Add(new BlueprintConnectionViewModel(this, connection));
        }

        RefreshConnections();
        _hasPendingSave = false;
        OnPropertyChanged(nameof(PanX));
        OnPropertyChanged(nameof(PanY));
        OnPropertyChanged(nameof(Zoom));
        OnPropertyChanged(nameof(ZoomText));
    }

    private BlueprintNodeViewModel CreateNodeViewModel(BlueprintNode node) => new(this, node);

    private void SelectTool(string? key)
    {
        var next = key?.Trim().ToLowerInvariant() switch
        {
            "pointer" or "mouse" => BlueprintEditorTool.Pointer,
            "add" => BlueprintEditorTool.Add,
            "connect" or "wire" => BlueprintEditorTool.Connect,
            "move" or "arrange" => BlueprintEditorTool.MoveNode,
            _ => ActiveTool
        };
        if (next == ActiveTool)
        {
            NotifyActiveToolSelection();
        }
        else
        {
            ActiveTool = next;
        }

        _statusMessage = $"已切换到{ActiveToolLabel}工具";
        OnPropertyChanged(nameof(StatusText));
    }

    private void BeginPendingConnection(BlueprintPortViewModel port)
    {
        _pendingConnectionPort = port;
        port.IsPendingConnection = true;
        var anchor = BlueprintConnectionGeometry.GetPortAnchor(port);
        UpdatePendingConnectionPointer(anchor.X, anchor.Y);
        _statusMessage = $"已选择{GetDirectionLabel(port.Direction)}端点 {port.Node.Title}.{port.Name}，请选择相反方向端点";
        OnPropertyChanged(nameof(HasPendingConnection));
        OnPropertyChanged(nameof(StatusText));
    }

    private void TryCreateConnection(BlueprintPortViewModel source, BlueprintPortViewModel target)
    {
        var model = new BlueprintConnection
        {
            Id = Guid.NewGuid().ToString("N"),
            SourceNodeId = source.Node.Id,
            SourcePortId = source.Id,
            TargetNodeId = target.Node.Id,
            TargetPortId = target.Id,
            Order = _graph.Connections
                .Where(connection => string.Equals(
                    connection.SourceNodeId,
                    source.Node.Id,
                    StringComparison.Ordinal))
                .Select(connection => connection.Order)
                .DefaultIfEmpty(-1)
                .Max() + 1
        };
        _graph.Connections.Add(model);
        try
        {
            BlueprintGraphValidator.Validate(_graph);
            var viewModel = new BlueprintConnectionViewModel(this, model);
            Connections.Add(viewModel);
            viewModel.Refresh();
            _statusMessage = $"已连接 {source.Node.Title} → {target.Node.Title}";
            MarkChanged();
            NotifySummaryChanged();
        }
        catch (Exception exception)
        {
            _graph.Connections.Remove(model);
            _statusMessage = $"连线被拒绝：{exception.Message}";
            _console.WriteWarning("Blueprint", _statusMessage);
            OnPropertyChanged(nameof(StatusText));
        }
        finally
        {
            CancelPendingConnection();
        }
    }

    private bool CancelPendingConnection()
    {
        if (_pendingConnectionPort is null)
        {
            return false;
        }

        _pendingConnectionPort.IsPendingConnection = false;
        _pendingConnectionPort = null;
        ConnectionPreview = null;
        OnPropertyChanged(nameof(HasPendingConnection));
        return true;
    }

    private bool CancelTransientInteractionCore()
    {
        var cancelled = CancelPendingConnection();
        if (PlacementPreview is not null)
        {
            PlacementPreview = null;
            cancelled = true;
        }

        return cancelled;
    }

    private static BlueprintPlacementPreview CreatePlacementPreview(
        BlueprintNodeTypeOption nodeType,
        double x,
        double y) =>
        new(
            nodeType,
            Math.Round(Math.Clamp(x, 0d, CanvasWidth - PlacementPreviewWidth), 1),
            Math.Round(Math.Clamp(y, 0d, CanvasHeight - MaximumEstimatedNodeHeight), 1),
            PlacementPreviewWidth,
            PlacementPreviewHeight);

    private static string GetDirectionLabel(BlueprintPortDirection direction) =>
        direction == BlueprintPortDirection.Output ? "输出" : "输入";

    private void RefreshConnections(string? nodeId = null)
    {
        foreach (var connection in Connections)
        {
            if (nodeId is null ||
                string.Equals(connection.Model.SourceNodeId, nodeId, StringComparison.Ordinal) ||
                string.Equals(connection.Model.TargetNodeId, nodeId, StringComparison.Ordinal))
            {
                connection.Refresh();
            }
        }
    }

    private void MarkChanged(bool scheduleSave = true)
    {
        if (_disposed)
        {
            return;
        }

        _hasPendingSave = true;
        if (scheduleSave)
        {
            _saveTimer.Stop();
            _saveTimer.Start();
        }
        else
        {
            _saveTimer.Stop();
        }
        NotifySummaryChanged();
    }

    private void OnSaveTimerTick(object? sender, EventArgs e)
    {
        _saveTimer.Stop();
        _ = SaveNow();
    }

    private void NotifySummaryChanged()
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(PersistenceText));
    }

    private void NotifyActiveToolSelection()
    {
        OnPropertyChanged(nameof(IsPointerTool));
        OnPropertyChanged(nameof(IsAddTool));
        OnPropertyChanged(nameof(IsConnectTool));
        OnPropertyChanged(nameof(IsMoveNodeTool));
        OnPropertyChanged(nameof(ActiveToolLabel));
    }

    private void EnsureActivated()
    {
        if (_graph is null)
        {
            throw new InvalidOperationException("蓝图尚未激活项目或工作流。");
        }
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    public const double CanvasWidth = 6_000d;

    public const double CanvasHeight = 4_000d;

    public const double PlacementPreviewWidth = 286d;

    public const double PlacementPreviewHeight = 150d;

    public const double MaximumEstimatedNodeHeight = 520d;
}
