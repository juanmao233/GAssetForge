using GAssetForge.App.Infrastructure;
using GAssetForge.App.Services;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;

namespace GAssetForge.App.ViewModels;

public sealed class ProjectBrowserViewModel : ObservableObject
{
    private readonly AssetRootService _rootService;
    private readonly LocalAssetCatalogService _catalogService;
    private readonly IAssetDirectoryPicker _directoryPicker;
    private readonly RelayCommand _chooseRootCommand;
    private readonly RelayCommand _useDefaultRootCommand;
    private readonly RelayCommand _navigateUpCommand;
    private readonly string _defaultGeneratedPath;
    private string? _configuredRootPath;
    private string _rootPath = string.Empty;
    private string _currentPath = string.Empty;
    private string _entryCountText = "0 个项目";
    private string _statusText = "资产浏览器正在初始化";
    private bool _isUsingFallback;
    private bool _isPrefabLibraryAvailable;
    private bool _isRootSelectionLocked;

    public ProjectBrowserViewModel(
        WorkspacePaths paths,
        IAssetDirectoryPicker directoryPicker,
        EditorWorkspaceViewModel editorWorkspace,
        string? configuredRootPath,
        string? configuredCurrentPath)
    {
        _rootService = new AssetRootService(paths);
        _catalogService = new LocalAssetCatalogService();
        _directoryPicker = directoryPicker;
        _defaultGeneratedPath = paths.GeneratedAssetDirectory;
        EditorWorkspace = editorWorkspace;

        _chooseRootCommand = new RelayCommand(ChooseRoot, () => !IsRootSelectionLocked);
        ChooseRootCommand = _chooseRootCommand;
        _useDefaultRootCommand = new RelayCommand(UseDefaultRoot, () => !IsRootSelectionLocked);
        UseDefaultRootCommand = _useDefaultRootCommand;
        _navigateUpCommand = new RelayCommand(NavigateUp, () => CanNavigateUp);
        NavigateUpCommand = _navigateUpCommand;
        RefreshCommand = new RelayCommand(Refresh);
        CreateFolderCommand = new RelayCommand(CreateFolder);
        OpenCurrentFolderCommand = new RelayCommand(OpenCurrentFolder);
        CopyCurrentPathCommand = new RelayCommand(CopyCurrentPath);
        TestCreateCommand = new RelayCommand(() =>
            StatusText = "创建（测试）：占位功能，未修改任何文件");

        var resolution = _rootService.Resolve(configuredRootPath);
        var initialCurrentPath = configuredCurrentPath ??
                                 (string.IsNullOrWhiteSpace(configuredRootPath) ? _defaultGeneratedPath : null);
        ApplyRootResolution(resolution, initialCurrentPath, notifyConfiguration: false);
    }

    public event Action<string?>? ConfiguredRootPathChanged;

    public event Action<string>? CurrentPathChanged;

    public EditorWorkspaceViewModel EditorWorkspace { get; }

    public ObservableCollection<AssetFolderNodeViewModel> FolderRoots { get; } = [];

    public ObservableCollection<AssetBreadcrumbViewModel> Breadcrumbs { get; } = [];

    public ObservableCollection<ProjectAssetEntryViewModel> Entries { get; } = [];

    public string? ConfiguredRootPath => _configuredRootPath;

    public string RootPath
    {
        get => _rootPath;
        private set
        {
            if (SetProperty(ref _rootPath, value))
            {
                OnPropertyChanged(nameof(RootDisplayName));
                OnPropertyChanged(nameof(IsUsingDefaultRoot));
            }
        }
    }

    public string CurrentPath
    {
        get => _currentPath;
        private set
        {
            if (SetProperty(ref _currentPath, value))
            {
                OnPropertyChanged(nameof(CurrentFolderName));
                OnPropertyChanged(nameof(CanNavigateUp));
                OnPropertyChanged(nameof(GenerationOutputPath));
                _navigateUpCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string RootDisplayName => GetDirectoryName(RootPath, "Assets");

    public string CurrentFolderName => GetDirectoryName(CurrentPath, RootDisplayName);

    public string GenerationOutputPath => CurrentPath;

    public bool CanNavigateUp => !string.IsNullOrEmpty(CurrentPath) &&
                                 !AssetRootService.PathEquals(CurrentPath, RootPath);

    public bool IsUsingDefaultRoot => AssetRootService.PathEquals(RootPath, _rootService.DefaultRootPath);

    public bool IsUsingFallback
    {
        get => _isUsingFallback;
        private set => SetProperty(ref _isUsingFallback, value);
    }

    public bool IsPrefabLibraryAvailable
    {
        get => _isPrefabLibraryAvailable;
        private set => SetProperty(ref _isPrefabLibraryAvailable, value);
    }

    public bool IsRootSelectionLocked
    {
        get => _isRootSelectionLocked;
        private set
        {
            if (SetProperty(ref _isRootSelectionLocked, value))
            {
                _chooseRootCommand.RaiseCanExecuteChanged();
                _useDefaultRootCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string EntryCountText
    {
        get => _entryCountText;
        private set => SetProperty(ref _entryCountText, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public ICommand ChooseRootCommand { get; }

    public ICommand UseDefaultRootCommand { get; }

    public ICommand NavigateUpCommand { get; }

    public ICommand RefreshCommand { get; }

    public ICommand CreateFolderCommand { get; }

    public ICommand OpenCurrentFolderCommand { get; }

    public ICommand CopyCurrentPathCommand { get; }

    public ICommand TestCreateCommand { get; }

    public bool TrySetRootPath(string? rootPath)
    {
        if (IsRootSelectionLocked)
        {
            StatusText = "当前项目的 Assets 根目录由项目位置管理；请通过“文件 → 新建项目”切换项目。";
            return false;
        }

        var resolution = _rootService.Resolve(rootPath);
        if (!string.IsNullOrWhiteSpace(rootPath) && resolution.IsUsingFallback)
        {
            StatusText = resolution.Message;
            return false;
        }

        var preferredCurrentPath = string.IsNullOrWhiteSpace(rootPath)
            ? _defaultGeneratedPath
            : resolution.EffectivePath;
        ApplyRootResolution(resolution, preferredCurrentPath, notifyConfiguration: true);
        return true;
    }

    public void SetPrefabLibraryAvailability(bool isAvailable) =>
        IsPrefabLibraryAvailable = isAvailable;

    public void SetRootSelectionLock(bool isLocked) =>
        IsRootSelectionLocked = isLocked;

    public bool SelectDirectory(string directoryPath)
    {
        if (!_rootService.TryNormalizeDirectory(directoryPath, out var normalizedPath, out var reason))
        {
            StatusText = $"无法打开文件夹：{reason}";
            return false;
        }

        if (!AssetRootService.IsWithinRoot(RootPath, normalizedPath))
        {
            StatusText = "已拒绝跳出当前 Assets 根目录的导航请求";
            return false;
        }

        CurrentPath = normalizedPath;
        RefreshCurrentDirectory();
        CurrentPathChanged?.Invoke(CurrentPath);
        return true;
    }

    public void LoadChildren(AssetFolderNodeViewModel node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (node.IsPlaceholder || node.ChildrenLoaded)
        {
            return;
        }

        node.Children.Clear();
        try
        {
            foreach (var entry in _catalogService.Enumerate(node.FullPath).Entries.Where(entry => entry.IsDirectory))
            {
                node.Children.Add(CreateFolderNode(entry.FullPath, entry.Name));
            }

            node.ChildrenLoaded = true;
        }
        catch (Exception exception) when (IsFileSystemException(exception))
        {
            node.ChildrenLoaded = true;
            StatusText = $"无法读取文件夹树：{exception.Message}";
        }
    }

    public void OpenEntry(ProjectAssetEntryViewModel entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!AssetRootService.IsWithinRoot(RootPath, entry.FullPath))
        {
            StatusText = "已拒绝打开 Assets 根目录之外的项目";
            return;
        }

        if (entry.IsDirectory)
        {
            SelectDirectory(entry.FullPath);
            return;
        }

        try
        {
            if (!File.Exists(entry.FullPath))
            {
                Refresh();
                StatusText = "文件已被移动或删除，列表已刷新";
                return;
            }

            Process.Start(new ProcessStartInfo(entry.FullPath) { UseShellExecute = true });
            StatusText = $"已打开：{entry.Name}";
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            StatusText = $"无法打开文件：{exception.Message}";
        }
    }

    public void Refresh()
    {
        if (!Directory.Exists(RootPath))
        {
            if (IsRootSelectionLocked)
            {
                FolderRoots.Clear();
                Breadcrumbs.Clear();
                Entries.Clear();
                EntryCountText = "0 个项目";
                IsUsingFallback = false;
                CurrentPath = RootPath;
                StatusText = "当前项目的 Assets 目录不存在或暂时不可访问；已保持项目边界，未回退到全局目录。";
                return;
            }

            var recovery = _rootService.Resolve(_configuredRootPath);
            ApplyRootResolution(recovery, recovery.EffectivePath, notifyConfiguration: false);
            return;
        }

        if (!Directory.Exists(CurrentPath) || !AssetRootService.IsWithinRoot(RootPath, CurrentPath))
        {
            CurrentPath = RootPath;
        }

        RebuildFolderTree();
        RefreshCurrentDirectory();
    }

    public void Shutdown()
    {
        // Reserved for a future debounced FileSystemWatcher/indexer lifecycle.
    }

    private void ChooseRoot()
    {
        var selectedPath = _directoryPicker.SelectFolder(RootPath);
        if (!string.IsNullOrWhiteSpace(selectedPath))
        {
            TrySetRootPath(selectedPath);
        }
    }

    private void UseDefaultRoot() => TrySetRootPath(null);

    private void NavigateUp()
    {
        if (!CanNavigateUp)
        {
            return;
        }

        var parent = Directory.GetParent(CurrentPath)?.FullName;
        if (parent is not null)
        {
            SelectDirectory(parent);
        }
    }

    private void CreateFolder()
    {
        try
        {
            if (!Directory.Exists(CurrentPath) ||
                !AssetRootService.IsWithinRoot(RootPath, CurrentPath))
            {
                StatusText = "当前项目资产目录不可用，未创建文件夹。";
                return;
            }

            var index = 1;
            string folderPath;
            do
            {
                var folderName = index == 1 ? "New Folder" : $"New Folder {index}";
                folderPath = Path.Combine(CurrentPath, folderName);
                index++;
            }
            while (Directory.Exists(folderPath) || File.Exists(folderPath));

            Directory.CreateDirectory(folderPath);
            Refresh();
            StatusText = $"已创建：{Path.GetFileName(folderPath)}";
        }
        catch (Exception exception) when (IsFileSystemException(exception))
        {
            StatusText = $"创建文件夹失败：{exception.Message}";
        }
    }

    private void OpenCurrentFolder()
    {
        try
        {
            if (!Directory.Exists(CurrentPath) ||
                !AssetRootService.IsWithinRoot(RootPath, CurrentPath))
            {
                StatusText = "当前项目资产目录不可用，无法在资源管理器中打开。";
                return;
            }

            Process.Start(new ProcessStartInfo(CurrentPath) { UseShellExecute = true });
            StatusText = "已在资源管理器中打开当前资产目录";
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            StatusText = $"无法打开资源管理器：{exception.Message}";
        }
    }

    private void CopyCurrentPath()
    {
        try
        {
            Clipboard.SetText(CurrentPath);
            StatusText = "当前生成输出路径已复制";
        }
        catch (Exception exception)
        {
            StatusText = $"复制路径失败：{exception.Message}";
        }
    }

    private void ApplyRootResolution(
        AssetRootResolution resolution,
        string? preferredCurrentPath,
        bool notifyConfiguration)
    {
        _configuredRootPath = resolution.ConfiguredPath;
        RootPath = resolution.EffectivePath;
        IsUsingFallback = resolution.IsUsingFallback;
        CurrentPath = ResolveCurrentPath(preferredCurrentPath, RootPath);
        RebuildFolderTree();
        RefreshCurrentDirectory();
        StatusText = resolution.IsUsingFallback
            ? resolution.Message
            : $"{resolution.Message} · 当前生成输出：{CurrentFolderName}";

        if (notifyConfiguration)
        {
            ConfiguredRootPathChanged?.Invoke(_configuredRootPath);
            CurrentPathChanged?.Invoke(CurrentPath);
        }
    }

    private void RefreshCurrentDirectory()
    {
        Entries.Clear();
        Breadcrumbs.Clear();
        try
        {
            var snapshot = _catalogService.Enumerate(CurrentPath);
            foreach (var entry in snapshot.Entries)
            {
                Entries.Add(new ProjectAssetEntryViewModel(entry));
            }

            BuildBreadcrumbs();
            EntryCountText = $"{snapshot.DirectoryCount} 个文件夹 · {snapshot.FileCount} 个文件";
            StatusText = $"当前生成输出：{CurrentPath}";
        }
        catch (Exception exception) when (IsFileSystemException(exception))
        {
            EntryCountText = "目录不可用";
            StatusText = $"读取资产目录失败：{exception.Message}";
        }
    }

    private void RebuildFolderTree()
    {
        FolderRoots.Clear();
        var root = CreateFolderNode(RootPath, RootDisplayName);
        FolderRoots.Add(root);
        LoadChildren(root);
    }

    private AssetFolderNodeViewModel CreateFolderNode(string fullPath, string name) =>
        new(fullPath, name, _catalogService.HasSubdirectories(fullPath));

    private void BuildBreadcrumbs()
    {
        Breadcrumbs.Add(new AssetBreadcrumbViewModel(RootDisplayName, RootPath, path => SelectDirectory(path)));
        var relativePath = Path.GetRelativePath(RootPath, CurrentPath);
        if (string.Equals(relativePath, ".", StringComparison.Ordinal))
        {
            return;
        }

        var current = RootPath;
        foreach (var segment in relativePath.Split(
                     new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            Breadcrumbs.Add(new AssetBreadcrumbViewModel(segment, current, path => SelectDirectory(path)));
        }
    }

    private string ResolveCurrentPath(string? configuredCurrentPath, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(configuredCurrentPath) &&
            _rootService.TryNormalizeDirectory(configuredCurrentPath, out var normalizedPath, out _) &&
            AssetRootService.IsWithinRoot(fallback, normalizedPath))
        {
            return normalizedPath;
        }

        return fallback;
    }

    private static string GetDirectoryName(string path, string fallback)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return fallback;
        }

        return new DirectoryInfo(path).Name is { Length: > 0 } name ? name : fallback;
    }

    private static bool IsFileSystemException(Exception exception) => exception is
        UnauthorizedAccessException or
        IOException or
        DirectoryNotFoundException or
        PathTooLongException or
        ArgumentException or
        NotSupportedException;
}
