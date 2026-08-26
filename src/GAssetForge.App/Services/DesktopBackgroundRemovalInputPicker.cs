using Microsoft.Win32;
using System.IO;

namespace GAssetForge.App.Services;

public sealed class DesktopBackgroundRemovalInputPicker : IBackgroundRemovalInputPicker
{
    public string? SelectInputFile(string? initialDirectory)
    {
        var selected = SelectInputFilesCore(initialDirectory, multiselect: false);
        return selected.Count == 0 ? null : selected[0];
    }

    public IReadOnlyList<string> SelectInputFiles(string? initialDirectory) =>
        SelectInputFilesCore(initialDirectory, multiselect: true);

    private static IReadOnlyList<string> SelectInputFilesCore(
        string? initialDirectory,
        bool multiselect)
    {
        var dialog = new OpenFileDialog
        {
            Title = multiselect ? "选择需要批量剔除背景的图片" : "选择需要剔除背景的图片",
            Filter = "支持的图片|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.webp|PNG 图片 (*.png)|*.png|JPEG 图片 (*.jpg;*.jpeg)|*.jpg;*.jpeg|所有文件 (*.*)|*.*",
            InitialDirectory = Directory.Exists(initialDirectory) ? initialDirectory : string.Empty,
            CheckFileExists = true,
            Multiselect = multiselect
        };

        return dialog.ShowDialog() == true ? dialog.FileNames : [];
    }
}
