using GAssetForge.App.Infrastructure;
using GAssetForge.App.Services;
using GAssetForge.Core.ArtStyles;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;

namespace GAssetForge.App.ViewModels;

public sealed class ArtStyleLibraryViewModel : ObservableObject, IDisposable
{
    private readonly JsonArtStyleLibraryRepository _repository;
    private readonly IArtStyleAssetPicker _assetPicker;
    private readonly ArtStyleLibraryModel _library;
    private readonly RelayCommand _deleteGroupCommand;
    private readonly RelayCommand<string> _renameGroupCommand;
    private readonly RelayCommand _importAssetsCommand;
    private readonly RelayCommand<string[]> _importDroppedAssetsCommand;
    private readonly RelayCommand _removeAssetCommand;
    private ArtStyleGroupViewModel? _selectedGroup;
    private ArtStyleAssetViewModel? _selectedAsset;
    private string _statusText = "美术风格素材库正在初始化";
    private bool _isApplyingPersistedSelection;
    private bool _isShutDown;

    public ArtStyleLibraryViewModel(WorkspacePaths paths, IArtStyleAssetPicker? picker = null)
        : this(new JsonArtStyleLibraryRepository(paths), picker ?? new DesktopArtStyleAssetPicker())
    {
    }

    public ArtStyleLibraryViewModel(
        JsonArtStyleLibraryRepository repository,
        IArtStyleAssetPicker assetPicker)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _assetPicker = assetPicker ?? throw new ArgumentNullException(nameof(assetPicker));
        _library = _repository.LoadOrCreate();

        foreach (var group in _library.Groups)
        {
            Groups.Add(CreateGroupViewModel(group));
        }

        CreateGroupCommand = new RelayCommand(CreateGroup);
        _deleteGroupCommand = new RelayCommand(DeleteSelectedGroup, () => SelectedGroup is not null);
        DeleteGroupCommand = _deleteGroupCommand;
        _renameGroupCommand = new RelayCommand<string>(
            name => RenameSelectedGroup(name),
            name => SelectedGroup is not null && !string.IsNullOrWhiteSpace(name));
        RenameGroupCommand = _renameGroupCommand;
        _importAssetsCommand = new RelayCommand(ImportPickedAssets, () => SelectedGroup is not null);
        ImportAssetsCommand = _importAssetsCommand;
        _importDroppedAssetsCommand = new RelayCommand<string[]>(
            paths => ImportAssets(paths ?? []),
            paths => SelectedGroup is not null && paths is { Length: > 0 });
        ImportDroppedAssetsCommand = _importDroppedAssetsCommand;
        _removeAssetCommand = new RelayCommand(RemoveSelectedAsset, () => SelectedAsset is not null);
        RemoveAssetCommand = _removeAssetCommand;

        ApplyPersistedSelection();
        StatusText = _repository.LastRecoverySource switch
        {
            "backup" => "主清单不可用，已从备份恢复美术风格素材库",
            "default" => "已创建默认美术风格组，可开始导入参考素材",
            _ => $"已加载 {Groups.Count} 个风格组"
        };
    }

    public ObservableCollection<ArtStyleGroupViewModel> Groups { get; } = [];

    public ArtStyleGroupViewModel? SelectedGroup
    {
        get => _selectedGroup;
        set
        {
            if (!SetProperty(ref _selectedGroup, value))
            {
                return;
            }

            OnPropertyChanged(nameof(HasSelectedGroup));
            RaiseCommandStates();
            if (_isApplyingPersistedSelection)
            {
                return;
            }

            var nextAsset = value?.Assets.FirstOrDefault(asset => asset.Id == _library.SelectedAssetId)
                ?? value?.Assets.FirstOrDefault();
            ApplySelectedAsset(nextAsset, persist: false);
            _library.SelectedGroupId = value?.Id;
            _library.SelectedAssetId = nextAsset?.Id;
            TrySave("已选择风格组");
        }
    }

    public ArtStyleAssetViewModel? SelectedAsset
    {
        get => _selectedAsset;
        set => ApplySelectedAsset(value, persist: true);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public bool HasGroups => Groups.Count > 0;

    public bool HasSelectedGroup => SelectedGroup is not null;

    public bool HasSelectedAsset => SelectedAsset is not null;

    public string LibraryFilePath => _repository.LibraryFilePath;

    public string ManagedAssetRoot => _repository.AssetRoot;

    public ICommand CreateGroupCommand { get; }

    public ICommand DeleteGroupCommand { get; }

    public ICommand RenameGroupCommand { get; }

    public ICommand ImportAssetsCommand { get; }

    public ICommand ImportDroppedAssetsCommand { get; }

    public ICommand RemoveAssetCommand { get; }

    public void RenameSelectedGroup(string? requestedName)
    {
        if (SelectedGroup is null)
        {
            StatusText = "请先选择要重命名的风格组";
            return;
        }

        var name = requestedName?.Trim() ?? string.Empty;
        try
        {
            ArtStyleLibraryValidator.ValidateGroupName(name);
            if (Groups.Any(group =>
                    !ReferenceEquals(group, SelectedGroup) &&
                    string.Equals(group.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                StatusText = $"已存在名为“{name}”的风格组";
                return;
            }

            SelectedGroup.Name = name;
        }
        catch (ArgumentException exception)
        {
            StatusText = exception.Message;
        }
    }

    public void ImportAssets(IEnumerable<string> sourcePaths)
    {
        ArgumentNullException.ThrowIfNull(sourcePaths);
        var targetGroup = SelectedGroup;
        if (targetGroup is null)
        {
            StatusText = "请先创建或选择一个风格组";
            return;
        }

        try
        {
            var result = _repository.ImportAssets(_library, targetGroup.Id, sourcePaths);
            var importedViewModels = result.ImportedAssets
                .Select(CreateAssetViewModel)
                .ToArray();
            targetGroup.AddAssets(importedViewModels);
            ApplySelectedAsset(importedViewModels.LastOrDefault() ?? SelectedAsset, persist: false);
            StatusText = result.ImportedAssets.Count switch
            {
                0 when result.DuplicateCount > 0 => $"未重复导入：{result.DuplicateCount} 张图片已存在于当前风格组",
                0 => "没有选择可导入的图片",
                _ when result.DuplicateCount > 0 =>
                    $"已导入 {result.ImportedAssets.Count} 张图片，跳过 {result.DuplicateCount} 张重复图片",
                _ => $"已导入 {result.ImportedAssets.Count} 张风格参考图片"
            };
        }
        catch (Exception exception) when (IsExpectedOperationException(exception))
        {
            StatusText = $"导入失败：{exception.Message}";
        }
    }

    public void Shutdown()
    {
        if (_isShutDown)
        {
            return;
        }

        _isShutDown = true;
        try
        {
            _repository.Save(_library);
        }
        catch (Exception exception) when (IsExpectedOperationException(exception))
        {
            StatusText = $"关闭前保存风格库失败：{exception.Message}";
        }
    }

    public void Dispose() => Shutdown();

    private void CreateGroup()
    {
        var previousSelectedGroupId = _library.SelectedGroupId;
        var previousSelectedAssetId = _library.SelectedAssetId;
        var now = DateTimeOffset.UtcNow;
        var model = new ArtStyleGroupModel
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = CreateUniqueGroupName(),
            Description = string.Empty,
            Prompt = string.Empty,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        var viewModel = CreateGroupViewModel(model);
        _library.Groups.Add(model);
        Groups.Add(viewModel);
        _library.SelectedGroupId = model.Id;
        _library.SelectedAssetId = null;

        if (!TrySave($"已创建风格组：{model.Name}"))
        {
            Groups.Remove(viewModel);
            _library.Groups.Remove(model);
            _library.SelectedGroupId = previousSelectedGroupId;
            _library.SelectedAssetId = previousSelectedAssetId;
            ApplyPersistedSelection();
            return;
        }

        ApplySelection(viewModel, null);
        OnPropertyChanged(nameof(HasGroups));
    }

    private void DeleteSelectedGroup()
    {
        var target = SelectedGroup;
        if (target is null)
        {
            return;
        }

        try
        {
            _repository.DeleteGroup(_library, target.Id);
            Groups.Remove(target);
            var nextGroup = Groups.FirstOrDefault(group => group.Id == _library.SelectedGroupId);
            var nextAsset = nextGroup?.Assets.FirstOrDefault(asset => asset.Id == _library.SelectedAssetId);
            ApplySelection(nextGroup, nextAsset);
            OnPropertyChanged(nameof(HasGroups));
            StatusText = $"已删除风格组：{target.Name}";
        }
        catch (Exception exception) when (IsExpectedOperationException(exception))
        {
            StatusText = $"删除风格组失败：{exception.Message}";
        }
    }

    private void ImportPickedAssets()
    {
        try
        {
            var selectedPaths = _assetPicker.SelectImageFiles();
            if (selectedPaths.Count > 0)
            {
                ImportAssets(selectedPaths);
            }
        }
        catch (Exception exception) when (IsExpectedOperationException(exception))
        {
            StatusText = $"无法选择图片：{exception.Message}";
        }
    }

    private void RemoveSelectedAsset()
    {
        var group = SelectedGroup;
        var asset = SelectedAsset;
        if (group is null || asset is null)
        {
            return;
        }

        try
        {
            _repository.RemoveAsset(_library, group.Id, asset.Id);
            group.RemoveAsset(asset);
            var nextAsset = group.Assets.FirstOrDefault(candidate => candidate.Id == _library.SelectedAssetId);
            ApplySelectedAsset(nextAsset, persist: false);
            StatusText = $"已从风格组移除素材：{asset.Name}";
        }
        catch (Exception exception) when (IsExpectedOperationException(exception))
        {
            StatusText = $"移除素材失败：{exception.Message}";
        }
    }

    private ArtStyleGroupViewModel CreateGroupViewModel(ArtStyleGroupModel model) =>
        new(
            model,
            model.Assets.Select(CreateAssetViewModel),
            PersistGroupEdit,
            message => StatusText = message);

    private ArtStyleAssetViewModel CreateAssetViewModel(ArtStyleAssetModel model) =>
        new(model, _repository.ResolveAssetFullPath(model));

    private bool PersistGroupEdit(ArtStyleGroupViewModel group)
    {
        if (Groups.Any(candidate =>
                !ReferenceEquals(candidate, group) &&
                string.Equals(candidate.Name, group.Name, StringComparison.OrdinalIgnoreCase)))
        {
            StatusText = $"已存在名为“{group.Name}”的风格组";
            return false;
        }

        return TrySave($"已保存风格定义：{group.Name}");
    }

    private bool TrySave(string successMessage)
    {
        try
        {
            _repository.Save(_library);
            StatusText = successMessage;
            return true;
        }
        catch (Exception exception) when (IsExpectedOperationException(exception))
        {
            StatusText = $"保存美术风格素材库失败：{exception.Message}";
            return false;
        }
    }

    private void ApplyPersistedSelection()
    {
        var group = Groups.FirstOrDefault(candidate => candidate.Id == _library.SelectedGroupId)
            ?? Groups.FirstOrDefault();
        var asset = group?.Assets.FirstOrDefault(candidate => candidate.Id == _library.SelectedAssetId)
            ?? group?.Assets.FirstOrDefault();
        ApplySelection(group, asset);
    }

    private void ApplySelection(
        ArtStyleGroupViewModel? group,
        ArtStyleAssetViewModel? asset)
    {
        _isApplyingPersistedSelection = true;
        try
        {
            SelectedGroup = group;
            ApplySelectedAsset(asset, persist: false);
        }
        finally
        {
            _isApplyingPersistedSelection = false;
        }

        RaiseCommandStates();
    }

    private void ApplySelectedAsset(ArtStyleAssetViewModel? asset, bool persist)
    {
        if (asset is not null && (SelectedGroup is null || !SelectedGroup.Assets.Contains(asset)))
        {
            return;
        }

        if (!SetProperty(ref _selectedAsset, asset, nameof(SelectedAsset)))
        {
            return;
        }

        OnPropertyChanged(nameof(HasSelectedAsset));
        _removeAssetCommand?.RaiseCanExecuteChanged();
        if (persist && !_isApplyingPersistedSelection)
        {
            _library.SelectedGroupId = SelectedGroup?.Id;
            _library.SelectedAssetId = asset?.Id;
            TrySave(asset is null ? "已清除素材选择" : $"已选择参考素材：{asset.Name}");
        }
    }

    private string CreateUniqueGroupName()
    {
        var occupied = Groups.Select(group => group.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var index = 1; ; index++)
        {
            var candidate = index == 1 ? "新建风格组" : $"新建风格组 {index}";
            if (!occupied.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    private void RaiseCommandStates()
    {
        _deleteGroupCommand?.RaiseCanExecuteChanged();
        _renameGroupCommand?.RaiseCanExecuteChanged();
        _importAssetsCommand?.RaiseCanExecuteChanged();
        _importDroppedAssetsCommand?.RaiseCanExecuteChanged();
        _removeAssetCommand?.RaiseCanExecuteChanged();
    }

    private static bool IsExpectedOperationException(Exception exception) => exception is
        ArgumentException or
        InvalidOperationException or
        InvalidDataException or
        IOException or
        UnauthorizedAccessException or
        NotSupportedException or
        System.Security.SecurityException;
}
