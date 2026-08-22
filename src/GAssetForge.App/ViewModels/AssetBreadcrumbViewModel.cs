using GAssetForge.App.Infrastructure;
using System.Windows.Input;

namespace GAssetForge.App.ViewModels;

public sealed class AssetBreadcrumbViewModel
{
    public AssetBreadcrumbViewModel(string label, string fullPath, Action<string> navigate)
    {
        Label = label;
        FullPath = fullPath;
        NavigateCommand = new RelayCommand(() => navigate(FullPath));
    }

    public string Label { get; }

    public string FullPath { get; }

    public ICommand NavigateCommand { get; }
}
