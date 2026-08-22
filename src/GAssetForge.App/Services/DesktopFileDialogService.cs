using Microsoft.Win32;
using System.IO;

namespace GAssetForge.App.Services;

public sealed class DesktopFileDialogService : IAssetDirectoryPicker
{
    public string? OpenProject()
    {
        var dialog = new OpenFileDialog
        {
            Title = "导入 GAssetForge UI 项目",
            Filter = "GAssetForge UI 项目 (*.pfui.json)|*.pfui.json|JSON 文件 (*.json)|*.json",
            CheckFileExists = true,
            Multiselect = false
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? SaveProject(string suggestedName)
    {
        var dialog = new SaveFileDialog
        {
            Title = "导出 GAssetForge UI 项目",
            Filter = "GAssetForge UI 项目 (*.pfui.json)|*.pfui.json|JSON 文件 (*.json)|*.json",
            FileName = suggestedName,
            AddExtension = true,
            DefaultExt = ".pfui.json"
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? SelectFolder(string initialPath)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择 GAssetForge 默认资产目录",
            InitialDirectory = Directory.Exists(initialPath) ? initialPath : string.Empty,
            Multiselect = false
        };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }
}
