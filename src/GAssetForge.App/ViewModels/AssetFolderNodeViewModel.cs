using System.Collections.ObjectModel;

namespace GAssetForge.App.ViewModels;

public sealed class AssetFolderNodeViewModel
{
    private AssetFolderNodeViewModel()
    {
        FullPath = string.Empty;
        Name = string.Empty;
        IsPlaceholder = true;
        ChildrenLoaded = true;
    }

    public AssetFolderNodeViewModel(string fullPath, string name, bool hasChildren)
    {
        FullPath = fullPath;
        Name = name;
        if (hasChildren)
        {
            Children.Add(CreatePlaceholder());
        }
        else
        {
            ChildrenLoaded = true;
        }
    }

    public string FullPath { get; }

    public string Name { get; }

    public string IconGlyph => "\uE8B7";

    public bool IsPlaceholder { get; }

    public ObservableCollection<AssetFolderNodeViewModel> Children { get; } = [];

    internal bool ChildrenLoaded { get; set; }

    internal static AssetFolderNodeViewModel CreatePlaceholder() => new();
}
