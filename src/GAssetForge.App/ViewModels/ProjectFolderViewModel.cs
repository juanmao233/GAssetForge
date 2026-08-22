using GAssetForge.App.Infrastructure;
using GAssetForge.Core.Projects;
using System.Windows.Input;

namespace GAssetForge.App.ViewModels;

public sealed class ProjectFolderViewModel : ObservableObject
{
    private readonly EditorWorkspaceViewModel _owner;
    private bool _isActive;

    public ProjectFolderViewModel(EditorWorkspaceViewModel owner, UiAssetFolderModel model)
    {
        _owner = owner;
        Model = model;
        SelectCommand = new RelayCommand(() => _owner.SelectProjectFolder(Id));
    }

    public UiAssetFolderModel Model { get; }
    public string Id => Model.Id;
    public string Name => Model.Name;
    public int AssetCount => Model.AssetIds.Count;
    public ICommand SelectCommand { get; }

    public bool IsActive
    {
        get => _isActive;
        internal set => SetProperty(ref _isActive, value);
    }
}
