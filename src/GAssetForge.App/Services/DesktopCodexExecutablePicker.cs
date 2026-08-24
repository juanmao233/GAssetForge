using Microsoft.Win32;
using System.IO;

namespace GAssetForge.App.Services;

public sealed class DesktopCodexExecutablePicker : ICodexExecutablePicker
{
    public string? SelectExecutable(string? currentPath)
    {
        var initialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrWhiteSpace(currentPath))
        {
            try
            {
                initialDirectory = Path.GetDirectoryName(currentPath) ?? initialDirectory;
            }
            catch (Exception exception) when (exception is
                ArgumentException or
                NotSupportedException or
                PathTooLongException)
            {
            }
        }
        var dialog = new OpenFileDialog
        {
            Title = "选择 Codex CLI 启动程序",
            Filter = "Codex 启动程序|*.cmd;*.bat;*.exe|命令脚本 (*.cmd;*.bat)|*.cmd;*.bat|可执行文件 (*.exe)|*.exe",
            InitialDirectory = Directory.Exists(initialDirectory) ? initialDirectory : string.Empty,
            CheckFileExists = true,
            Multiselect = false
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
