using Microsoft.Win32;
using System.IO;

namespace GAssetForge.App.Services;

public sealed class DesktopAssetGenerationSpreadsheetPicker : IAssetGenerationSpreadsheetPicker
{
    public string? SelectSpreadsheet(string? initialDirectory)
    {
        var dialog = new OpenFileDialog
        {
            Title = "导入素材描述表格",
            Filter =
                "支持的表格 (*.xlsx;*.xls;*.csv;*.tsv)|*.xlsx;*.xls;*.csv;*.tsv|" +
                "Excel 工作簿 (*.xlsx;*.xls)|*.xlsx;*.xls|" +
                "分隔文本 (*.csv;*.tsv)|*.csv;*.tsv",
            CheckFileExists = true,
            CheckPathExists = true,
            Multiselect = false,
            InitialDirectory = Directory.Exists(initialDirectory) ? initialDirectory : string.Empty
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
