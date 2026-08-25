using Microsoft.Win32;
using System.IO;

namespace GAssetForge.App.Services;

public sealed class DesktopImageOutlineInputPicker : IImageOutlineInputPicker
{
    public string? SelectInputFile(string? initialDirectory)
    {
        var dialog = CreateDialog(initialDirectory, multiselect: false);
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public IReadOnlyList<string> SelectInputFiles(string? initialDirectory)
    {
        var dialog = CreateDialog(initialDirectory, multiselect: true);
        return dialog.ShowDialog() == true ? dialog.FileNames : [];
    }

    private static OpenFileDialog CreateDialog(string? initialDirectory, bool multiselect) =>
        new()
        {
            Title = multiselect ? "选择需要批量描边的图片" : "选择需要描边的图片",
            Filter = "支持的图片|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.webp|PNG 图片 (*.png)|*.png|JPEG 图片 (*.jpg;*.jpeg)|*.jpg;*.jpeg|所有文件 (*.*)|*.*",
            InitialDirectory = Directory.Exists(initialDirectory) ? initialDirectory : string.Empty,
            CheckFileExists = true,
            Multiselect = multiselect
        };
}
