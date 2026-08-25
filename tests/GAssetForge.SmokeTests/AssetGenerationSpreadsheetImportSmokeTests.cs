using GAssetForge.App.Services;
using GAssetForge.Core.Generation;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace GAssetForge.SmokeTests;

internal static class AssetGenerationSpreadsheetImportSmokeTests
{
    public static void Run()
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            "GAssetForge.SmokeTests",
            "asset-generation-spreadsheet-import",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testRoot);

        try
        {
            ReadsBoundedXlsxSheetsAndCachedFormulaValues(testRoot);
            ReadsCsvAndTsvWithoutOfficeInterop(testRoot);
            RejectsUnsupportedFormatsAndUnsafeInputs(testRoot);
            ExposesStableDesktopPickerContract();
        }
        finally
        {
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    private static void ReadsBoundedXlsxSheetsAndCachedFormulaValues(string testRoot)
    {
        var path = Path.Combine(testRoot, "描述清单.xlsx");
        CreateXlsx(
            path,
            [
                new TestSheet(
                    "Tilemap",
                    [
                        ["物品描述", "数量", "公式缓存"],
                        ["苔藓砂岩地表", 2d, new CachedFormula("1+1", 2d)],
                        [null, null, null],
                        ["破损石质路标", 1d, new CachedFormula("40+2", 42d)]
                    ]),
                new TestSheet(
                    "Icons",
                    [
                        ["Description", "Description", null],
                        ["青铜钥匙", "备用说明", "透明底"]
                    ])
            ]);

        var workbook = new ExcelAssetGenerationSpreadsheetReader()
            .ReadAsync(path)
            .GetAwaiter()
            .GetResult();

        Equal(Path.GetFullPath(path), workbook.SourcePath, "XLSX 来源路径应规范化");
        Equal(2, workbook.Sheets.Count, "XLSX 工作表数量");
        var tilemap = workbook.Sheets[0];
        Equal("Tilemap", tilemap.Name, "首个工作表名称");
        Equal(3, tilemap.Columns.Count, "首个工作表列数");
        Equal("物品描述", tilemap.Columns[0].Header, "中文表头");
        Equal("column_1", tilemap.Columns[0].Key, "列稳定 Key");
        Equal(2, tilemap.Rows.Count, "空白行不应成为预览数据行");
        True(tilemap.Rows.All(row => row.Cells.Count == tilemap.Columns.Count),
            "所有预览行都应补齐到最终列宽，避免动态 DataGrid 索引越界");
        Equal(2, tilemap.Rows[0].SourceRowNumber, "来源 Excel 行号");
        Equal("苔藓砂岩地表", tilemap.Rows[0].Cells[0], "中文单元格内容");
        Equal("2", tilemap.Rows[0].Cells[1], "数字使用不依赖本地化的显示值");
        Equal("2", tilemap.Rows[0].Cells[2], "公式只读取文件中的缓存值");
        Equal("42", tilemap.Rows[1].Cells[2], "不得执行或重新计算公式表达式");

        var icons = workbook.Sheets[1];
        Equal("Description", icons.Columns[0].Header, "首个同名表头");
        Equal("Description (2)", icons.Columns[1].Header, "重复表头应稳定消歧");
        Equal("列 C", icons.Columns[2].Header, "空表头应使用 Excel 列名");
    }

    private static void ReadsCsvAndTsvWithoutOfficeInterop(string testRoot)
    {
        var csvPath = Path.Combine(testRoot, "descriptions.csv");
        File.WriteAllText(
            csvPath,
            "物品描述,备注\r\n\"石质宝箱,带锁\",中文备注\r\n法杖,\"两行\r\n说明\"\r\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        var csv = new ExcelAssetGenerationSpreadsheetReader()
            .ReadAsync(csvPath)
            .GetAwaiter()
            .GetResult();
        Equal(1, csv.Sheets.Count, "CSV 应呈现为单个工作表");
        Equal("descriptions", csv.Sheets[0].Name, "CSV 工作表使用文件名");
        Equal(2, csv.Sheets[0].Rows.Count, "CSV 数据行数量");
        Equal("石质宝箱,带锁", csv.Sheets[0].Rows[0].Cells[0], "CSV 引号内逗号");
        Equal("两行\r\n说明", csv.Sheets[0].Rows[1].Cells[1], "CSV 引号内换行");

        var tsvPath = Path.Combine(testRoot, "descriptions.tsv");
        File.WriteAllText(
            tsvPath,
            "Description\tTag\r\nCompass\tUI Icon\r\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        var tsv = new ExcelAssetGenerationSpreadsheetReader()
            .ReadAsync(tsvPath)
            .GetAwaiter()
            .GetResult();
        Equal(2, tsv.Sheets[0].Columns.Count, "TSV 仅按 Tab 切列");
        Equal("Compass", tsv.Sheets[0].Rows.Single().Cells[0], "TSV 内容");
    }

    private static void RejectsUnsupportedFormatsAndUnsafeInputs(string testRoot)
    {
        foreach (var extension in new[] { ".xlsm", ".xlsb" })
        {
            var rejectedPath = Path.Combine(testRoot, $"rejected{extension}");
            File.WriteAllBytes(rejectedPath, [1]);
            var exception = Throws<InvalidDataException>(() =>
                new ExcelAssetGenerationSpreadsheetReader()
                    .ReadAsync(rejectedPath)
                    .GetAwaiter()
                    .GetResult());
            True(exception.Message.Contains("明确不支持", StringComparison.Ordinal),
                $"{extension} 应给出明确拒绝信息");
        }

        var oversizedPath = Path.Combine(testRoot, "oversized.csv");
        using (var stream = new FileStream(oversizedPath, FileMode.CreateNew, FileAccess.Write))
        {
            stream.SetLength(AssetGenerationSpreadsheetImportLimits.MaximumFileBytes + 1);
        }
        Throws<InvalidDataException>(() => Read(oversizedPath));

        var tooManyRowsPath = Path.Combine(testRoot, "too-many-rows.csv");
        using (var writer = new StreamWriter(tooManyRowsPath, false, new UTF8Encoding(false)))
        {
            writer.WriteLine("Description");
            for (var index = 0; index < AssetGenerationSpreadsheetImportLimits.MaximumRowsPerWorksheet; index++)
            {
                writer.WriteLine($"row-{index}");
            }
        }
        Throws<InvalidDataException>(() => Read(tooManyRowsPath));

        var tooManyColumnsPath = Path.Combine(testRoot, "too-many-columns.csv");
        File.WriteAllText(
            tooManyColumnsPath,
            string.Join(',', Enumerable.Range(
                1,
                AssetGenerationSpreadsheetImportLimits.MaximumColumnsPerWorksheet + 1)),
            new UTF8Encoding(false));
        Throws<InvalidDataException>(() => Read(tooManyColumnsPath));

        var oversizedCellPath = Path.Combine(testRoot, "oversized-cell.csv");
        File.WriteAllText(
            oversizedCellPath,
            $"Description\r\n{new string('x', AssetGenerationSpreadsheetImportLimits.MaximumCellCharacters + 1)}",
            new UTF8Encoding(false));
        Throws<InvalidDataException>(() => Read(oversizedCellPath));

        var excessiveTextPath = Path.Combine(testRoot, "excessive-text.csv");
        var cellText = new string('x', 129);
        using (var writer = new StreamWriter(excessiveTextPath, false, new UTF8Encoding(false)))
        {
            writer.WriteLine(string.Join(',', Enumerable.Repeat("Header", 128)));
            for (var row = 0; row < 512; row++)
            {
                writer.WriteLine(string.Join(',', Enumerable.Repeat(cellText, 128)));
            }
        }
        Throws<InvalidDataException>(() => Read(excessiveTextPath));

        var invalidZipPath = Path.Combine(testRoot, "invalid-container.xlsx");
        using (var stream = new FileStream(invalidZipPath, FileMode.CreateNew, FileAccess.Write))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            WriteTextEntry(archive, "[Content_Types].xml", "<Types />");
        }
        var invalidZip = Throws<InvalidDataException>(() => Read(invalidZipPath));
        True(invalidZip.Message.Contains("工作簿结构", StringComparison.Ordinal),
            "XLSX ZIP 预检应先于第三方解析器拒绝缺少 workbook.xml 的容器");

        var corruptWorksheetPath = Path.Combine(testRoot, "corrupt-workbook-xml.xlsx");
        CreateXlsx(
            corruptWorksheetPath,
            [new TestSheet("Sheet1", [["Description"], ["Rock"]])]);
        using (var stream = new FileStream(corruptWorksheetPath, FileMode.Open, FileAccess.ReadWrite))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Update))
        {
            archive.GetEntry("xl/workbook.xml")!.Delete();
            WriteTextEntry(archive, "xl/workbook.xml", "<");
        }
        var corruptWorksheet = Throws<InvalidDataException>(() => Read(corruptWorksheetPath));
        True(corruptWorksheet.Message.Contains("表格内容损坏", StringComparison.Ordinal),
            "通过 ZIP 结构预检但 XML 损坏的 XLSX 应统一包装为可展示的导入错误");

        var corruptBinaryWorkbookPath = Path.Combine(testRoot, "corrupt-workbook.xls");
        File.WriteAllBytes(corruptBinaryWorkbookPath, [0x01, 0x02, 0x03, 0x04]);
        var corruptBinaryWorkbook = Throws<InvalidDataException>(() => Read(corruptBinaryWorkbookPath));
        True(corruptBinaryWorkbook.Message.Contains("表格内容损坏", StringComparison.Ordinal),
            "无效 BIFF 工作簿不得让 ExcelDataReader 异常越过 ViewModel");

        var tooManySheetsPath = Path.Combine(testRoot, "too-many-sheets.xlsx");
        CreateXlsx(
            tooManySheetsPath,
            Enumerable.Range(1, AssetGenerationSpreadsheetImportLimits.MaximumWorksheetCount + 1)
                .Select(index => new TestSheet($"Sheet{index}", [["Description"], [$"Row{index}"]]))
                .ToArray());
        Throws<InvalidDataException>(() => Read(tooManySheetsPath));

        var duplicateEntryPath = Path.Combine(testRoot, "duplicate-entry.xlsx");
        CreateXlsx(
            duplicateEntryPath,
            [new TestSheet("Sheet1", [["Description"], ["Rock"]])]);
        using (var stream = new FileStream(duplicateEntryPath, FileMode.Open, FileAccess.ReadWrite))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Update))
        {
            WriteTextEntry(archive, "XL/WORKBOOK.XML", "<duplicate />");
        }
        var duplicateEntry = Throws<InvalidDataException>(() => Read(duplicateEntryPath));
        True(duplicateEntry.Message.Contains("重复", StringComparison.Ordinal),
            "XLSX ZIP 大小写重复条目应在交给第三方解析器前被拒绝");

        var unsafeEntryPath = Path.Combine(testRoot, "unsafe-entry.xlsx");
        CreateXlsx(
            unsafeEntryPath,
            [new TestSheet("Sheet1", [["Description"], ["Rock"]])]);
        using (var stream = new FileStream(unsafeEntryPath, FileMode.Open, FileAccess.ReadWrite))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Update))
        {
            WriteTextEntry(archive, "xl\\unsafe.xml", "<unsafe />");
        }
        var unsafeEntry = Throws<InvalidDataException>(() => Read(unsafeEntryPath));
        True(unsafeEntry.Message.Contains("不安全", StringComparison.Ordinal),
            "XLSX ZIP 反斜杠路径应被明确拒绝");

        var macroEntryPath = Path.Combine(testRoot, "macro-entry.xlsx");
        CreateXlsx(
            macroEntryPath,
            [new TestSheet("Sheet1", [["Description"], ["Rock"]])]);
        using (var stream = new FileStream(macroEntryPath, FileMode.Open, FileAccess.ReadWrite))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Update))
        {
            WriteTextEntry(archive, "xl/vbaProject.bin", "not-a-real-macro");
        }
        var macroEntry = Throws<InvalidDataException>(() => Read(macroEntryPath));
        True(macroEntry.Message.Contains("宏", StringComparison.Ordinal),
            "伪装成 .xlsx 的宏工程应被明确拒绝");

        var excessiveSharedStringsPath = Path.Combine(testRoot, "shared-strings-limit.xlsx");
        CreateXlsx(
            excessiveSharedStringsPath,
            [new TestSheet("Sheet1", [["Description"], ["Rock"]])]);
        using (var stream = new FileStream(excessiveSharedStringsPath, FileMode.Open, FileAccess.ReadWrite))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Update))
        {
            var excessiveSharedStringCount =
                AssetGenerationSpreadsheetImportLimits.MaximumRowsPerWorksheet *
                AssetGenerationSpreadsheetImportLimits.MaximumColumnsPerWorksheet + 1;
            WriteTextEntry(
                archive,
                "xl/sharedStrings.xml",
                $"<sst xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" " +
                $"count=\"{excessiveSharedStringCount}\" " +
                $"uniqueCount=\"{excessiveSharedStringCount}\" />");
        }
        var excessiveSharedStrings = Throws<InvalidDataException>(() => Read(excessiveSharedStringsPath));
        True(excessiveSharedStrings.Message.Contains("共享字符串", StringComparison.Ordinal),
            "XLSX sharedStrings 计数应在可能触发大容量分配前被有界预检拒绝");
    }

    private static void ExposesStableDesktopPickerContract()
    {
        var method = typeof(IAssetGenerationSpreadsheetPicker).GetMethod(
            nameof(IAssetGenerationSpreadsheetPicker.SelectSpreadsheet));
        True(method is not null, "表格文件选择接口应公开 SelectSpreadsheet");
        Equal(typeof(string), method!.ReturnType, "表格文件选择接口返回路径或 null");
        True(typeof(IAssetGenerationSpreadsheetPicker).IsAssignableFrom(
                typeof(DesktopAssetGenerationSpreadsheetPicker)),
            "桌面文件选择器应实现稳定接口");
    }

    private static AssetGenerationSpreadsheetWorkbook Read(string path) =>
        new ExcelAssetGenerationSpreadsheetReader()
            .ReadAsync(path)
            .GetAwaiter()
            .GetResult();

    private static void CreateXlsx(string path, IReadOnlyList<TestSheet> sheets)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);

        WriteXmlEntry(archive, "[Content_Types].xml", writer =>
        {
            writer.WriteStartElement("Types", "http://schemas.openxmlformats.org/package/2006/content-types");
            writer.WriteStartElement("Default");
            writer.WriteAttributeString("Extension", "rels");
            writer.WriteAttributeString("ContentType", "application/vnd.openxmlformats-package.relationships+xml");
            writer.WriteEndElement();
            writer.WriteStartElement("Default");
            writer.WriteAttributeString("Extension", "xml");
            writer.WriteAttributeString("ContentType", "application/xml");
            writer.WriteEndElement();
            writer.WriteStartElement("Override");
            writer.WriteAttributeString("PartName", "/xl/workbook.xml");
            writer.WriteAttributeString("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml");
            writer.WriteEndElement();
            for (var index = 0; index < sheets.Count; index++)
            {
                writer.WriteStartElement("Override");
                writer.WriteAttributeString("PartName", $"/xl/worksheets/sheet{index + 1}.xml");
                writer.WriteAttributeString("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml");
                writer.WriteEndElement();
            }
            writer.WriteEndElement();
        });
        WriteXmlEntry(archive, "_rels/.rels", writer =>
        {
            writer.WriteStartElement("Relationships", "http://schemas.openxmlformats.org/package/2006/relationships");
            writer.WriteStartElement("Relationship");
            writer.WriteAttributeString("Id", "rId1");
            writer.WriteAttributeString("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument");
            writer.WriteAttributeString("Target", "xl/workbook.xml");
            writer.WriteEndElement();
            writer.WriteEndElement();
        });
        WriteXmlEntry(archive, "xl/workbook.xml", writer =>
        {
            writer.WriteStartElement("workbook", "http://schemas.openxmlformats.org/spreadsheetml/2006/main");
            writer.WriteAttributeString("xmlns", "r", null, "http://schemas.openxmlformats.org/officeDocument/2006/relationships");
            writer.WriteStartElement("sheets");
            for (var index = 0; index < sheets.Count; index++)
            {
                writer.WriteStartElement("sheet");
                writer.WriteAttributeString("name", sheets[index].Name);
                writer.WriteAttributeString("sheetId", (index + 1).ToString(CultureInfo.InvariantCulture));
                writer.WriteAttributeString("r", "id", null, $"rId{index + 1}");
                writer.WriteEndElement();
            }
            writer.WriteEndElement();
            writer.WriteEndElement();
        });
        WriteXmlEntry(archive, "xl/_rels/workbook.xml.rels", writer =>
        {
            writer.WriteStartElement("Relationships", "http://schemas.openxmlformats.org/package/2006/relationships");
            for (var index = 0; index < sheets.Count; index++)
            {
                writer.WriteStartElement("Relationship");
                writer.WriteAttributeString("Id", $"rId{index + 1}");
                writer.WriteAttributeString("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet");
                writer.WriteAttributeString("Target", $"worksheets/sheet{index + 1}.xml");
                writer.WriteEndElement();
            }
            writer.WriteEndElement();
        });

        for (var sheetIndex = 0; sheetIndex < sheets.Count; sheetIndex++)
        {
            var sheet = sheets[sheetIndex];
            WriteXmlEntry(archive, $"xl/worksheets/sheet{sheetIndex + 1}.xml", writer =>
            {
                writer.WriteStartElement("worksheet", "http://schemas.openxmlformats.org/spreadsheetml/2006/main");
                writer.WriteStartElement("sheetData");
                for (var rowIndex = 0; rowIndex < sheet.Rows.Count; rowIndex++)
                {
                    writer.WriteStartElement("row");
                    writer.WriteAttributeString("r", (rowIndex + 1).ToString(CultureInfo.InvariantCulture));
                    for (var columnIndex = 0; columnIndex < sheet.Rows[rowIndex].Length; columnIndex++)
                    {
                        var value = sheet.Rows[rowIndex][columnIndex];
                        if (value is null)
                        {
                            continue;
                        }

                        writer.WriteStartElement("c");
                        writer.WriteAttributeString("r", $"{ToColumnName(columnIndex)}{rowIndex + 1}");
                        switch (value)
                        {
                            case CachedFormula formula:
                                writer.WriteStartElement("f");
                                writer.WriteString(formula.Expression);
                                writer.WriteEndElement();
                                writer.WriteStartElement("v");
                                writer.WriteString(Convert.ToString(formula.CachedValue, CultureInfo.InvariantCulture));
                                writer.WriteEndElement();
                                break;
                            case string text:
                                writer.WriteAttributeString("t", "inlineStr");
                                writer.WriteStartElement("is");
                                writer.WriteStartElement("t");
                                writer.WriteAttributeString("xml", "space", null, "preserve");
                                writer.WriteString(text);
                                writer.WriteEndElement();
                                writer.WriteEndElement();
                                break;
                            default:
                                writer.WriteStartElement("v");
                                writer.WriteString(Convert.ToString(value, CultureInfo.InvariantCulture));
                                writer.WriteEndElement();
                                break;
                        }
                        writer.WriteEndElement();
                    }
                    writer.WriteEndElement();
                }
                writer.WriteEndElement();
                writer.WriteEndElement();
            });
        }
    }

    private static void WriteXmlEntry(ZipArchive archive, string name, Action<XmlWriter> write)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = XmlWriter.Create(stream, new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false),
            CloseOutput = false,
            Indent = false
        });
        writer.WriteStartDocument();
        write(writer);
        writer.WriteEndDocument();
    }

    private static void WriteTextEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(content);
    }

    private static string ToColumnName(int zeroBasedIndex)
    {
        var value = zeroBasedIndex + 1;
        var result = string.Empty;
        while (value > 0)
        {
            value--;
            result = (char)('A' + value % 26) + result;
            value /= 26;
        }
        return result;
    }

    private static TException Throws<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException exception)
        {
            return exception;
        }

        throw new InvalidOperationException($"预期抛出 {typeof(TException).Name}。");
    }

    private static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message}：预期 {expected}，实际 {actual}。");
        }
    }

    private sealed record CachedFormula(string Expression, object CachedValue);

    private sealed record TestSheet(string Name, IReadOnlyList<object?[]> Rows);
}
