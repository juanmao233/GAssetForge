using Microsoft.Win32;
using System.IO;

namespace GAssetForge.App.Services;

public sealed class DesktopPixelAlignmentOutputPicker : IPixelAlignmentOutputPicker
{
    public string? SelectOutputFile(string? initialDirectory, string suggestedFileName, string title)
    {
        var dialog = new SaveFileDialog
        {
            Title = title,
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
            Title = "选择像素对齐批量输出目录",
            InitialDirectory = Directory.Exists(initialDirectory) ? initialDirectory : string.Empty,
            Multiselect = false
        };

        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }
}
