using Microsoft.Win32;
using System.IO;

namespace GAssetForge.App.Services;

public sealed class DesktopArtWorkspaceImagePicker : IArtWorkspaceImagePicker
{
    public IReadOnlyList<string> SelectImages(string? initialDirectory)
    {
        var dialog = new OpenFileDialog
        {
            Title = "向美术工作区导入参考图片",
            Filter = "支持的图片|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.webp|PNG 图片 (*.png)|*.png|JPEG 图片 (*.jpg;*.jpeg)|*.jpg;*.jpeg|所有文件 (*.*)|*.*",
            InitialDirectory = Directory.Exists(initialDirectory) ? initialDirectory : string.Empty,
            CheckFileExists = true,
            Multiselect = true
        };
        return dialog.ShowDialog() == true ? dialog.FileNames : [];
    }
}
