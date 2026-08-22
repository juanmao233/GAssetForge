using Microsoft.Win32;
using System.IO;

namespace GAssetForge.App.Services;

public sealed class DesktopProjectLocationPicker : IProjectLocationPicker
{
    public string? SelectParentDirectory(string initialPath)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择新项目的保存位置",
            InitialDirectory = Directory.Exists(initialPath) ? initialPath : string.Empty,
            Multiselect = false
        };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }
}
