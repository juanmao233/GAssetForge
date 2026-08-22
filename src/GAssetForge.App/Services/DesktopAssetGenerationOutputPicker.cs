using Microsoft.Win32;
using System.IO;

namespace GAssetForge.App.Services;

public sealed class DesktopAssetGenerationOutputPicker : IAssetGenerationOutputPicker
{
    public string? SelectOutputDirectory(string initialPath)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择素材生成输出目录（必须位于当前 Assets 根目录内）",
            InitialDirectory = Directory.Exists(initialPath) ? initialPath : string.Empty,
            Multiselect = false
        };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }
}
