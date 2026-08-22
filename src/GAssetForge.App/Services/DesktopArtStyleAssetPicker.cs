using Microsoft.Win32;

namespace GAssetForge.App.Services;

public sealed class DesktopArtStyleAssetPicker : IArtStyleAssetPicker
{
    public IReadOnlyList<string> SelectImageFiles()
    {
        var dialog = new OpenFileDialog
        {
            Title = "导入美术风格参考素材",
            Filter = "支持的图片|*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.gif;*.tif;*.tiff|PNG 图片 (*.png)|*.png|JPEG 图片 (*.jpg;*.jpeg)|*.jpg;*.jpeg|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = true
        };

        return dialog.ShowDialog() == true ? dialog.FileNames : [];
    }
}
