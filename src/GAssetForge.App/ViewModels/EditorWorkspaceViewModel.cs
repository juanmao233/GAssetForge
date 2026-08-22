using GAssetForge.App.Infrastructure;
using GAssetForge.App.Services;
using GAssetForge.Core.Projects;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows.Input;
using System.Windows.Threading;

namespace GAssetForge.App.ViewModels;

public sealed class EditorWorkspaceViewModel : ObservableObject
{
    private readonly JsonProjectRepository _repository;
    private readonly DesktopFileDialogService _dialogs;
    private readonly ProjectHistoryService _history;
    private readonly DispatcherTimer _autosaveTimer;
    private readonly RelayCommand _undoCommand;
    private readonly RelayCommand _redoCommand;
    private UiProjectModel _project;
    private LayerViewModel? _selectedLayer;
    private EditorTool _activeTool;
    private bool _isDirty;
    private string _saveStateText = "已加载";
    private string _statusText = "UI 编辑器已就绪";
    private string? _dragBeforeJson;
    private string? _dragLayerId;
    private double _dragOriginX;
    private double _dragOriginY;
    private bool _dragMoved;
    private bool _hasPendingSessionSave;

    public EditorWorkspaceViewModel(
        JsonProjectRepository repository,
        DesktopFileDialogService dialogs,
        ProjectHistoryService history)
    {
        _repository = repository;
        _dialogs = dialogs;
        _history = history;
        _project = repository.LoadOrCreate();
        _activeTool = string.Equals(_project.EditorSession.ActiveTool, "text", StringComparison.OrdinalIgnoreCase)
            ? EditorTool.Text
            : EditorTool.Move;

        SelectToolCommand = new RelayCommand<string>(SelectTool);
        NewLayerCommand = new RelayCommand(NewLayer);
        NewFolderCommand = new RelayCommand(NewLayerFolder);
        DeleteSelectedCommand = new RelayCommand(
            DeleteSelected,
            () => SelectedLayer is { IsPage: false, EffectiveLocked: false });
        InstantiatePrefabCommand = new RelayCommand<PrefabViewModel>(prefab =>
        {
            if (prefab is not null)
            {
                InstantiatePrefabAt(prefab.Id, null, null);
            }
        });
        SaveCommand = new RelayCommand(SaveImmediately);
        ImportCommand = new RelayCommand(ImportProject);
        ExportCommand = new RelayCommand(ExportProject);
        _undoCommand = new RelayCommand(Undo, () => _history.CanUndo);
        _redoCommand = new RelayCommand(Redo, () => _history.CanRedo);
        UndoCommand = _undoCommand;
        RedoCommand = _redoCommand;
        ZoomInCommand = new RelayCommand(() => Zoom = Math.Min(1.5, Zoom + 0.1));
        ZoomOutCommand = new RelayCommand(() => Zoom = Math.Max(0.25, Zoom - 0.1));
        ResetZoomCommand = new RelayCommand(() => Zoom = 0.5);

        _autosaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(650) };
        _autosaveTimer.Tick += (_, _) =>
        {
            _autosaveTimer.Stop();
            SaveImmediately();
        };

        RefreshAll();
        SaveStateText = _repository.LastRecoverySource switch
        {
            "backup" => "已从备份恢复",
            "default" => "已创建原生默认项目",
            _ => "已加载本地项目"
        };
    }

    public ObservableCollection<LayerViewModel> LayerRoots { get; } = [];
    public ObservableCollection<LayerViewModel> CanvasLayers { get; } = [];
    public ObservableCollection<ProjectFolderViewModel> ProjectFolders { get; } = [];
    public ObservableCollection<PrefabViewModel> Prefabs { get; } = [];

    public ICommand SelectToolCommand { get; }
    public ICommand NewLayerCommand { get; }
    public ICommand NewFolderCommand { get; }
    public ICommand DeleteSelectedCommand { get; }
    public ICommand InstantiatePrefabCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand ImportCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand UndoCommand { get; }
    public ICommand RedoCommand { get; }
    public ICommand ZoomInCommand { get; }
    public ICommand ZoomOutCommand { get; }
    public ICommand ResetZoomCommand { get; }

    public UiProjectModel Project => _project;
    public string? ProjectRoot => _repository.ProjectRoot;
    public string ProjectFilePath => _repository.ProjectFilePath;
    public UiDocumentModel ActiveDocument => _project.DocumentsById[_project.ActiveDocumentId];
    public string DocumentTitle => ActiveDocument.Name;
    public double CanvasWidth => ActiveDocument.Canvas.Width;
    public double CanvasHeight => ActiveDocument.Canvas.Height;
    public string CanvasBackground => ActiveDocument.Canvas.Background;
    public string DocumentMeta => $"{CanvasWidth:0} × {CanvasHeight:0} / {Zoom * 100:0}%";
    public string LayerCountText => $"{ActiveDocument.LayersById.Count} 个图层 · {ActiveDocument.LayersById.Values.Count(layer => layer.Kind == "group")} 个文件夹";
    public string ActiveFolderName => _project.Library.FoldersById.TryGetValue(_project.Library.ActiveFolderId, out var folder) ? folder.Name : "Assets";
    public string PrefabCountText => $"{Prefabs.Count} 个预制体";
    public string ZoomText => $"{Zoom * 100:0}%";

    public LayerViewModel? SelectedLayer
    {
        get => _selectedLayer;
        private set
        {
            if (ReferenceEquals(_selectedLayer, value))
            {
                if (value is not null)
                {
                    value.IsSelected = true;
                }

                return;
            }

            if (_selectedLayer is not null)
            {
                _selectedLayer.IsSelected = false;
            }

            if (SetProperty(ref _selectedLayer, value))
            {
                if (_selectedLayer is not null)
                {
                    _selectedLayer.IsSelected = true;
                }

                OnPropertyChanged(nameof(HasSelectedLayer));
                OnPropertyChanged(nameof(SelectedLayerType));
                (DeleteSelectedCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public bool HasSelectedLayer => SelectedLayer is not null;
    public string SelectedLayerType => SelectedLayer?.TypeLabel ?? "未选择图层";

    public EditorTool ActiveTool
    {
        get => _activeTool;
        private set
        {
            if (SetProperty(ref _activeTool, value))
            {
                _project.EditorSession.ActiveTool = value == EditorTool.Text ? "text" : "move";
                OnPropertyChanged(nameof(ActiveToolLabel));
                ScheduleSessionSave();
            }
        }
    }

    public string ActiveToolLabel => ActiveTool == EditorTool.Text ? "文字" : "选择 / 移动";

    public double Zoom
    {
        get => _project.EditorSession.Zoom;
        set
        {
            var next = Math.Clamp(Math.Round(value, 2), 0.25, 1.5);
            if (Math.Abs(next - _project.EditorSession.Zoom) < 0.001)
            {
                return;
            }

            _project.EditorSession.Zoom = next;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ZoomText));
            OnPropertyChanged(nameof(DocumentMeta));
            ScheduleSessionSave();
        }
    }

    public bool IsDirty
    {
        get => _isDirty;
        private set => SetProperty(ref _isDirty, value);
    }

    public string SaveStateText
    {
        get => _saveStateText;
        private set => SetProperty(ref _saveStateText, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public void SelectLayer(string layerId)
    {
        var layer = FindLayerViewModel(layerId);
        if (layer is null)
        {
            return;
        }

        _project.EditorSession.SelectedLayerId = layerId;
        SelectedLayer = layer;
        StatusText = $"已选择：{layer.Name}";
        ScheduleSessionSave();
    }

    public void SelectProjectFolder(string folderId)
    {
        if (!_project.Library.FoldersById.ContainsKey(folderId))
        {
            return;
        }

        _project.Library.ActiveFolderId = folderId;
        RefreshProjectLibrary();
        ScheduleSessionSave();
    }

    public bool CanMoveLayer(string sourceId, string targetId, bool intoTarget)
    {
        var document = ActiveDocument;
        if (string.Equals(sourceId, targetId, StringComparison.Ordinal) ||
            !document.LayersById.TryGetValue(sourceId, out var source) ||
            !document.LayersById.TryGetValue(targetId, out var target) ||
            string.Equals(source.Component?.Type, "page", StringComparison.Ordinal) ||
            IsLayerEffectivelyLocked(document, sourceId) ||
            (intoTarget && (!string.Equals(target.Kind, "group", StringComparison.Ordinal) || IsLayerEffectivelyLocked(document, targetId))))
        {
            return false;
        }

        if (!intoTarget && target.ParentId is { } targetParentId && IsLayerEffectivelyLocked(document, targetParentId))
        {
            return false;
        }

        return !ContainsLayer(document, sourceId, targetId);
    }

    public void MoveLayer(string sourceId, string targetId, bool intoTarget)
    {
        if (!CanMoveLayer(sourceId, targetId, intoTarget))
        {
            return;
        }

        var sourceName = ActiveDocument.LayersById[sourceId].Name;
        Mutate($"移动图层：{sourceName}", project =>
        {
            var document = project.DocumentsById[project.ActiveDocumentId];
            var source = document.LayersById[sourceId];
            var target = document.LayersById[targetId];

            RemoveFromParent(document, source);
            if (intoTarget)
            {
                target.ChildIds.Insert(0, sourceId);
                source.ParentId = target.Id;
            }
            else
            {
                var siblings = target.ParentId is { } parentId
                    ? document.LayersById[parentId].ChildIds
                    : document.RootIds;
                var targetIndex = siblings.IndexOf(targetId);
                siblings.Insert(targetIndex < 0 ? 0 : targetIndex, sourceId);
                source.ParentId = target.ParentId;
            }

            project.EditorSession.SelectedLayerId = sourceId;
        });
    }

    public void CreateTextAt(double x, double y)
    {
        Mutate("创建文字图层", project =>
        {
            var document = project.DocumentsById[project.ActiveDocumentId];
            var id = $"layer-text-{Guid.NewGuid():N}";
            document.LayersById[id] = new UiLayerModel
            {
                Id = id,
                Kind = "text",
                Name = $"文字 {document.LayersById.Values.Count(layer => layer.Kind == "text") + 1}",
                Transform = new UiTransformModel
                {
                    X = Math.Clamp(x, 0, Math.Max(0, document.Canvas.Width - 240)),
                    Y = Math.Clamp(y, 0, Math.Max(0, document.Canvas.Height - 54)),
                    Width = 240,
                    Height = 54
                },
                Style = new UiStyleModel(),
                Text = new UiTextModel()
            };
            document.RootIds.Insert(0, id);
            project.EditorSession.SelectedLayerId = id;
        });
    }

    public bool BeginMove(string layerId)
    {
        if (!ActiveDocument.LayersById.TryGetValue(layerId, out var layer) ||
            layer.Transform is null || IsLayerEffectivelyLocked(ActiveDocument, layerId) ||
            string.Equals(layer.Component?.Type, "page", StringComparison.Ordinal))
        {
            return false;
        }

        _dragBeforeJson = ProjectJson.Serialize(_project);
        _dragLayerId = layerId;
        _dragOriginX = layer.Transform.X;
        _dragOriginY = layer.Transform.Y;
        _dragMoved = false;
        return true;
    }

    public void PreviewMove(double deltaX, double deltaY)
    {
        if (_dragLayerId is null || !ActiveDocument.LayersById.TryGetValue(_dragLayerId, out var layer) || layer.Transform is null)
        {
            return;
        }

        var nextX = Math.Clamp(
            Math.Round(_dragOriginX + deltaX),
            0,
            Math.Max(0, ActiveDocument.Canvas.Width - layer.Transform.Width));
        var nextY = Math.Clamp(
            Math.Round(_dragOriginY + deltaY),
            0,
            Math.Max(0, ActiveDocument.Canvas.Height - layer.Transform.Height));
        _dragMoved |= Math.Abs(nextX - _dragOriginX) >= 1 || Math.Abs(nextY - _dragOriginY) >= 1;
        layer.Transform.X = nextX;
        layer.Transform.Y = nextY;
        FindLayerViewModel(layer.Id)?.RefreshGeometry();
    }

    public void EndMove()
    {
        if (_dragMoved && _dragBeforeJson is not null &&
            !string.Equals(_dragBeforeJson, ProjectJson.Serialize(_project), StringComparison.Ordinal))
        {
            _history.RecordSnapshot(_dragBeforeJson);
            TouchDocument();
            MarkDirty("移动图层");
            RaiseHistoryState();
        }

        _dragBeforeJson = null;
        _dragLayerId = null;
        _dragMoved = false;
    }

    public void InstantiatePrefabAt(string prefabId, double? x, double? y)
    {
        if (!_project.Library.AssetsById.TryGetValue(prefabId, out var prefab))
        {
            return;
        }

        Mutate($"实例化预制体：{prefab.Name}", project =>
        {
            var document = project.DocumentsById[project.ActiveDocumentId];
            var idMap = prefab.Payload.LayersById.Keys.ToDictionary(id => id, _ => $"layer-{Guid.NewGuid():N}");
            var defaultX = Math.Min(520 + document.RootIds.Count * 8, document.Canvas.Width - prefab.Payload.Bounds.Width - 40);
            var defaultY = Math.Min(360 + document.RootIds.Count * 6, document.Canvas.Height - prefab.Payload.Bounds.Height - 40);
            var targetX = Math.Clamp(x ?? defaultX, 0, Math.Max(0, document.Canvas.Width - prefab.Payload.Bounds.Width));
            var targetY = Math.Clamp(y ?? defaultY, 0, Math.Max(0, document.Canvas.Height - prefab.Payload.Bounds.Height));
            var offsetX = targetX - prefab.Payload.Bounds.X;
            var offsetY = targetY - prefab.Payload.Bounds.Y;

            foreach (var (sourceId, sourceLayer) in prefab.Payload.LayersById)
            {
                var cloned = JsonSerializer.Deserialize<UiLayerModel>(JsonSerializer.Serialize(sourceLayer, ProjectJson.Options), ProjectJson.Options)!;
                cloned.Id = idMap[sourceId];
                cloned.ParentId = sourceLayer.ParentId is not null && idMap.TryGetValue(sourceLayer.ParentId, out var parentId) ? parentId : null;
                cloned.ChildIds = sourceLayer.ChildIds.Select(childId => idMap[childId]).ToList();
                cloned.PrefabSourceId = prefab.Id;
                if (cloned.Transform is not null)
                {
                    cloned.Transform.X += offsetX;
                    cloned.Transform.Y += offsetY;
                }

                document.LayersById[cloned.Id] = cloned;
            }

            var newRootIds = prefab.Payload.RootIds.Select(rootId => idMap[rootId]).ToList();
            document.RootIds.InsertRange(0, newRootIds);
            project.EditorSession.SelectedLayerId = newRootIds.FirstOrDefault();
        });
    }

    public void EditLayer(string layerId, string label, Action<UiLayerModel> edit, string propertyName)
    {
        if (!ActiveDocument.LayersById.TryGetValue(layerId, out var layer))
        {
            return;
        }

        var before = ProjectJson.Serialize(_project);
        try
        {
            edit(layer);
            ProjectValidator.Validate(_project);
        }
        catch (Exception exception)
        {
            _project = ProjectJson.Deserialize(before);
            RefreshAll();
            StatusText = $"{label}失败：{exception.Message}";
            return;
        }

        var after = ProjectJson.Serialize(_project);
        if (string.Equals(before, after, StringComparison.Ordinal))
        {
            return;
        }

        _history.RecordSnapshot(before);
        TouchDocument();
        var viewModel = FindLayerViewModel(layerId);
        viewModel?.RefreshContent();
        viewModel?.RefreshGeometry();
        RefreshEffectiveVisibility();
        (DeleteSelectedCommand as RelayCommand)?.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(LayerCountText));
        MarkDirty(label);
        RaiseHistoryState();
    }

    public void SaveImmediately()
    {
        _autosaveTimer.Stop();
        try
        {
            SaveStateText = "保存中…";
            _repository.Save(_project);
            IsDirty = false;
            _hasPendingSessionSave = false;
            SaveStateText = $"已保存 {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception exception)
        {
            SaveStateText = "保存失败";
            StatusText = exception.Message;
        }
    }

    public void PrepareForProjectSwitch() => SaveCurrentProjectForSwitch();

    public void SwitchProject(string projectRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        SaveCurrentProjectForSwitch();
        SwitchProjectCore(projectRoot);
    }

    internal void SwitchToPreparedProject(string projectRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        SwitchProjectCore(projectRoot);
    }

    private void SaveCurrentProjectForSwitch()
    {
        _autosaveTimer.Stop();
        var hadPendingSave = IsDirty || _hasPendingSessionSave;
        try
        {
            // Save unconditionally: a freshly loaded/default workspace may
            // contain meaningful session state even before it is marked dirty.
            _repository.Save(_project);
            IsDirty = false;
            _hasPendingSessionSave = false;
        }
        catch (Exception exception)
        {
            SaveStateText = "保存失败";
            StatusText = $"当前项目尚未成功保存，已取消项目切换：{exception.Message}";
            if (hadPendingSave)
            {
                _autosaveTimer.Start();
            }

            throw new InvalidOperationException(StatusText, exception);
        }
    }

    private void SwitchProjectCore(string projectRoot)
    {
        var previousProjectRoot = _repository.ProjectRoot;
        UiProjectModel nextProject;
        try
        {
            _repository.SwitchProject(projectRoot);
            nextProject = _repository.LoadOrCreate();
            ProjectValidator.Validate(nextProject);
        }
        catch
        {
            _repository.SwitchProject(previousProjectRoot);
            throw;
        }

        _project = nextProject;
        _history.Clear();
        _dragBeforeJson = null;
        _dragLayerId = null;
        _dragMoved = false;
        _hasPendingSessionSave = false;
        IsDirty = false;
        RefreshAll();
        RaiseHistoryState();
        OnPropertyChanged(nameof(ProjectRoot));
        OnPropertyChanged(nameof(ProjectFilePath));
        SaveStateText = _repository.LastRecoverySource switch
        {
            "backup" => "已从项目备份恢复",
            "default" => "已创建项目默认 UI 数据",
            _ => "已加载项目 UI 数据"
        };
        StatusText = $"当前项目：{_project.Name}";
    }

    public void Shutdown()
    {
        if (IsDirty || _hasPendingSessionSave)
        {
            SaveImmediately();
        }
    }

    private void SelectTool(string? tool)
    {
        ActiveTool = string.Equals(tool, "text", StringComparison.OrdinalIgnoreCase) ? EditorTool.Text : EditorTool.Move;
        StatusText = ActiveTool == EditorTool.Text ? "文字工具：点击画布创建文字" : "移动工具：拖拽画布图层";
    }

    private void NewLayer()
    {
        var selectedFolderId = SelectedLayer is { IsGroup: true, EffectiveLocked: false } ? SelectedLayer.Id : null;
        Mutate("新建普通图层", project =>
        {
            var document = project.DocumentsById[project.ActiveDocumentId];
            var index = document.LayersById.Values.Count(layer => string.Equals(layer.Component?.Type, "frame", StringComparison.Ordinal)) + 1;
            var id = $"layer-frame-{Guid.NewGuid():N}";
            document.LayersById[id] = new UiLayerModel
            {
                Id = id,
                Kind = "component",
                Name = $"普通图层 {index:00}",
                ParentId = selectedFolderId,
                Transform = new UiTransformModel { X = 180 + index * 16, Y = 160 + index * 16, Width = 320, Height = 180 },
                Style = new UiStyleModel { Radius = 12 },
                Component = new UiComponentModel
                {
                    Type = "frame",
                    Props = new System.Text.Json.Nodes.JsonObject { ["label"] = "Frame" }
                }
            };
            if (selectedFolderId is not null && document.LayersById.TryGetValue(selectedFolderId, out var selectedFolder))
            {
                selectedFolder.ChildIds.Insert(0, id);
            }
            else
            {
                document.RootIds.Insert(0, id);
            }

            project.EditorSession.SelectedLayerId = id;
        });
    }

    private void NewLayerFolder()
    {
        var selectedFolderId = SelectedLayer is { IsGroup: true, EffectiveLocked: false } ? SelectedLayer.Id : null;
        Mutate("新建图层文件夹", project =>
        {
            var document = project.DocumentsById[project.ActiveDocumentId];
            var index = document.LayersById.Values.Count(layer => layer.Kind == "group") + 1;
            var id = $"group-{Guid.NewGuid():N}";
            document.LayersById[id] = new UiLayerModel
            {
                Id = id,
                Kind = "group",
                Name = $"图层文件夹 {index:00}",
                ParentId = selectedFolderId
            };
            if (selectedFolderId is not null && document.LayersById.TryGetValue(selectedFolderId, out var selectedFolder))
            {
                selectedFolder.ChildIds.Insert(0, id);
            }
            else
            {
                document.RootIds.Insert(0, id);
            }

            project.EditorSession.SelectedLayerId = id;
        });
    }

    private void DeleteSelected()
    {
        var selected = SelectedLayer;
        if (selected is null || selected.IsPage || selected.EffectiveLocked)
        {
            return;
        }

        Mutate($"删除图层：{selected.Name}", project =>
        {
            var document = project.DocumentsById[project.ActiveDocumentId];
            var ids = new List<string>();
            void Collect(string id)
            {
                if (!document.LayersById.TryGetValue(id, out var layer))
                {
                    return;
                }

                ids.Add(id);
                foreach (var childId in layer.ChildIds)
                {
                    Collect(childId);
                }
            }

            Collect(selected.Id);
            if (selected.Model.ParentId is { } parentId && document.LayersById.TryGetValue(parentId, out var parent))
            {
                parent.ChildIds.Remove(selected.Id);
            }
            else
            {
                document.RootIds.Remove(selected.Id);
            }

            foreach (var id in ids)
            {
                document.LayersById.Remove(id);
            }

            project.EditorSession.SelectedLayerId = document.RootIds.FirstOrDefault();
        });
    }

    private void Undo()
    {
        if (!_history.CanUndo)
        {
            return;
        }

        _project = _history.Undo(_project);
        RefreshAll();
        MarkDirty("撤销");
        RaiseHistoryState();
    }

    private void Redo()
    {
        if (!_history.CanRedo)
        {
            return;
        }

        _project = _history.Redo(_project);
        RefreshAll();
        MarkDirty("重做");
        RaiseHistoryState();
    }

    private void ImportProject()
    {
        var path = _dialogs.OpenProject();
        if (path is null)
        {
            return;
        }

        try
        {
            var importedProject = _repository.Import(path);
            _history.Record(_project);
            _project = importedProject;
            RefreshAll();
            MarkDirty("导入项目");
            SaveImmediately();
            RaiseHistoryState();
            StatusText = $"已导入：{Path.GetFileName(path)}";
        }
        catch (Exception exception)
        {
            StatusText = $"导入失败：{exception.Message}";
        }
    }

    private void ExportProject()
    {
        var safeName = string.Concat(_project.Name.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
        var path = _dialogs.SaveProject($"{safeName}.pfui.json");
        if (path is null)
        {
            return;
        }

        try
        {
            _repository.Export(_project, path);
            StatusText = $"已导出：{Path.GetFileName(path)}";
        }
        catch (Exception exception)
        {
            StatusText = $"导出失败：{exception.Message}";
        }
    }

    private void Mutate(string label, Action<UiProjectModel> mutation)
    {
        var before = ProjectJson.Serialize(_project);
        UiProjectModel candidate;
        try
        {
            candidate = ProjectJson.Deserialize(before);
            mutation(candidate);
            ProjectValidator.Validate(candidate);
        }
        catch (Exception exception)
        {
            StatusText = $"{label}失败：{exception.Message}";
            return;
        }

        if (string.Equals(before, ProjectJson.Serialize(candidate), StringComparison.Ordinal))
        {
            return;
        }

        candidate.DocumentsById[candidate.ActiveDocumentId].UpdatedAt = DateTimeOffset.Now;
        _history.RecordSnapshot(before);
        _project = candidate;
        RefreshAll();
        MarkDirty(label);
        RaiseHistoryState();
    }

    private void TouchDocument() => ActiveDocument.UpdatedAt = DateTimeOffset.Now;

    private void MarkDirty(string label)
    {
        IsDirty = true;
        SaveStateText = $"未保存 · {label}";
        StatusText = label;
        _autosaveTimer.Stop();
        _autosaveTimer.Start();
    }

    private void ScheduleSessionSave()
    {
        _hasPendingSessionSave = true;
        _autosaveTimer.Stop();
        _autosaveTimer.Start();
    }

    private void RaiseHistoryState()
    {
        _undoCommand.RaiseCanExecuteChanged();
        _redoCommand.RaiseCanExecuteChanged();
    }

    private void RefreshAll()
    {
        var selectedId = _project.EditorSession.SelectedLayerId;
        var viewModels = ActiveDocument.LayersById.Values.ToDictionary(
            layer => layer.Id,
            layer => new LayerViewModel(this, layer),
            StringComparer.Ordinal);

        foreach (var viewModel in viewModels.Values)
        {
            foreach (var childId in viewModel.Model.ChildIds)
            {
                if (viewModels.TryGetValue(childId, out var child))
                {
                    viewModel.Children.Add(child);
                }
            }
        }

        LayerRoots.Clear();
        foreach (var rootId in ActiveDocument.RootIds)
        {
            if (viewModels.TryGetValue(rootId, out var root))
            {
                LayerRoots.Add(root);
            }
        }

        var topmostFirst = new List<LayerViewModel>();
        void CollectDrawable(LayerViewModel layer)
        {
            if (layer.IsDrawable)
            {
                topmostFirst.Add(layer);
            }

            foreach (var child in layer.Children)
            {
                CollectDrawable(child);
            }
        }

        foreach (var root in LayerRoots)
        {
            CollectDrawable(root);
        }

        CanvasLayers.Clear();
        var bottomFirst = topmostFirst.AsEnumerable().Reverse().ToList();
        for (var index = 0; index < bottomFirst.Count; index++)
        {
            bottomFirst[index].ZIndex = index;
            CanvasLayers.Add(bottomFirst[index]);
        }

        RefreshEffectiveVisibility();
        SelectedLayer = selectedId is not null && viewModels.TryGetValue(selectedId, out var selected)
            ? selected
            : LayerRoots.FirstOrDefault();
        _project.EditorSession.SelectedLayerId = SelectedLayer?.Id;
        RefreshProjectLibrary();
        _activeTool = string.Equals(_project.EditorSession.ActiveTool, "text", StringComparison.OrdinalIgnoreCase)
            ? EditorTool.Text
            : EditorTool.Move;

        OnPropertyChanged(nameof(Project));
        OnPropertyChanged(nameof(ActiveDocument));
        OnPropertyChanged(nameof(DocumentTitle));
        OnPropertyChanged(nameof(CanvasWidth));
        OnPropertyChanged(nameof(CanvasHeight));
        OnPropertyChanged(nameof(CanvasBackground));
        OnPropertyChanged(nameof(DocumentMeta));
        OnPropertyChanged(nameof(LayerCountText));
        OnPropertyChanged(nameof(ActiveTool));
        OnPropertyChanged(nameof(ActiveToolLabel));
    }

    private void RefreshProjectLibrary()
    {
        ProjectFolders.Clear();
        foreach (var folderId in _project.Library.RootFolderIds)
        {
            if (_project.Library.FoldersById.TryGetValue(folderId, out var folder))
            {
                ProjectFolders.Add(new ProjectFolderViewModel(this, folder)
                {
                    IsActive = string.Equals(folderId, _project.Library.ActiveFolderId, StringComparison.Ordinal)
                });
            }
        }

        Prefabs.Clear();
        if (_project.Library.FoldersById.TryGetValue(_project.Library.ActiveFolderId, out var activeFolder))
        {
            foreach (var assetId in activeFolder.AssetIds)
            {
                if (_project.Library.AssetsById.TryGetValue(assetId, out var asset))
                {
                    Prefabs.Add(new PrefabViewModel(asset));
                }
            }
        }

        OnPropertyChanged(nameof(ActiveFolderName));
        OnPropertyChanged(nameof(PrefabCountText));
    }

    private void RefreshEffectiveVisibility()
    {
        void Visit(LayerViewModel layer, bool parentVisible, bool parentLocked, double parentOpacity)
        {
            layer.EffectiveVisibility = parentVisible && layer.Model.Visible;
            layer.EffectiveLocked = parentLocked || layer.Model.Locked;
            layer.EffectiveOpacity = Math.Clamp(parentOpacity * layer.Model.Opacity, 0, 1);
            foreach (var child in layer.Children)
            {
                Visit(child, layer.EffectiveVisibility, layer.EffectiveLocked, layer.EffectiveOpacity);
            }
        }

        foreach (var root in LayerRoots)
        {
            Visit(root, true, false, 1);
        }
    }

    private static void RemoveFromParent(UiDocumentModel document, UiLayerModel layer)
    {
        if (layer.ParentId is { } parentId && document.LayersById.TryGetValue(parentId, out var parent))
        {
            parent.ChildIds.Remove(layer.Id);
        }
        else
        {
            document.RootIds.Remove(layer.Id);
        }
    }

    private static bool ContainsLayer(UiDocumentModel document, string ancestorId, string candidateId)
    {
        if (!document.LayersById.TryGetValue(ancestorId, out var ancestor))
        {
            return false;
        }

        foreach (var childId in ancestor.ChildIds)
        {
            if (string.Equals(childId, candidateId, StringComparison.Ordinal) || ContainsLayer(document, childId, candidateId))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsLayerEffectivelyLocked(UiDocumentModel document, string layerId)
    {
        var currentId = layerId;
        while (document.LayersById.TryGetValue(currentId, out var current))
        {
            if (current.Locked)
            {
                return true;
            }

            if (current.ParentId is not { } parentId)
            {
                return false;
            }

            currentId = parentId;
        }

        return false;
    }

    private LayerViewModel? FindLayerViewModel(string layerId)
    {
        LayerViewModel? Find(IEnumerable<LayerViewModel> layers)
        {
            foreach (var layer in layers)
            {
                if (string.Equals(layer.Id, layerId, StringComparison.Ordinal))
                {
                    return layer;
                }

                var child = Find(layer.Children);
                if (child is not null)
                {
                    return child;
                }
            }

            return null;
        }

        return Find(LayerRoots);
    }
}
