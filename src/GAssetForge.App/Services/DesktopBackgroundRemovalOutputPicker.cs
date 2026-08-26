using Microsoft.Win32;
using System.IO;

namespace GAssetForge.App.Services;

public sealed class DesktopBackgroundRemovalOutputPicker : IBackgroundRemovalOutputPicker
{
    public string? SelectOutputFile(string? initialDirectory, string suggestedFileName)
    {
        var dialog = new SaveFileDialog
        {
            Title = "保存透明背景 PNG",
            Filter = "PNG 图片 (*.png)|*.png",
            InitialDirectory = Directory.Exists(initialDirectory) ? initialDirectory : string.Empty,
            FileName = suggestedFileName,
            AddExtension = true,
            DefaultExt = ".png",
            OverwritePrompt = true
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? SelectOutputDirectory(string? initialDirectory)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择批量背景剔除输出目录",
            InitialDirectory = Directory.Exists(initialDirectory) ? initialDirectory : string.Empty,
            Multiselect = false
        };

        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }
}
