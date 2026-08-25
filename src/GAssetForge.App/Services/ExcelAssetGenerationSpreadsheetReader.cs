using ExcelDataReader;
using GAssetForge.Core.Generation;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace GAssetForge.App.Services;

/// <summary>
/// Reads user-selected spreadsheet data as bounded, inert display values. Formula expressions are
/// never evaluated; ExcelDataReader exposes only the cached value already stored in the workbook.
/// </summary>
public sealed class ExcelAssetGenerationSpreadsheetReader : IAssetGenerationSpreadsheetReader
{
    private const int MaximumZipEntryCount = 2_048;
    private const long MaximumZipEntryBytes = 64L * 1024L * 1024L;
    private const long MaximumZipExpandedBytes = 128L * 1024L * 1024L;
    private const double MaximumZipCompressionRatio = 200d;
    private const int MaximumSharedStringCount =
        AssetGenerationSpreadsheetImportLimits.MaximumRowsPerWorksheet *
        AssetGenerationSpreadsheetImportLimits.MaximumColumnsPerWorksheet;
    private const long MaximumContentTypesXmlCharacters = 1024L * 1024L;
    private const long MaximumSharedStringsXmlCharacters =
        AssetGenerationSpreadsheetImportLimits.MaximumWorkbookCharacters * 4L;
    private static readonly string[] SupportedExtensions = [".xlsx", ".xls", ".csv", ".tsv"];
    private static readonly string[] ExplicitlyRejectedExtensions = [".xlsm", ".xlsb"];
    private static readonly Encoding LegacyCsvFallbackEncoding;

    static ExcelAssetGenerationSpreadsheetReader()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        LegacyCsvFallbackEncoding = Encoding.GetEncoding(936);
    }

    public Task<AssetGenerationSpreadsheetWorkbook> ReadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var sourcePath = NormalizeSourcePath(path);
        return Task.Run(() =>
        {
            try
            {
                return ReadCore(sourcePath, cancellationToken);
            }
            catch (Exception exception) when (IsThirdPartyParseException(exception))
            {
                throw new InvalidDataException(
                    "表格内容损坏、加密或不符合当前支持的 Excel/分隔文本格式。",
                    exception);
            }
        }, cancellationToken);
    }

    private static bool IsThirdPartyParseException(Exception exception)
    {
        if (exception is InvalidDataException or IOException or UnauthorizedAccessException or
            OperationCanceledException)
        {
            return false;
        }

        return exception is XmlException or FormatException or DecoderFallbackException or
                   InvalidOperationException or NotSupportedException or IndexOutOfRangeException or
                   ArgumentOutOfRangeException ||
               exception.GetType().Namespace?.StartsWith(
                   "ExcelDataReader",
                   StringComparison.Ordinal) == true;
    }

    private static AssetGenerationSpreadsheetWorkbook ReadCore(
        string sourcePath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        ValidateExtension(extension);

        using var stream = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);
        ValidateFileLength(stream.Length);

        if (extension == ".xlsx")
        {
            ValidateXlsxContainer(stream, cancellationToken);
            stream.Position = 0;
        }

        using var reader = CreateReader(stream, extension);
        var sheets = new List<AssetGenerationSpreadsheetSheet>();
        long workbookCharacterCount = 0;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (sheets.Count >= AssetGenerationSpreadsheetImportLimits.MaximumWorksheetCount)
            {
                throw new InvalidDataException(
                    $"工作簿最多允许 {AssetGenerationSpreadsheetImportLimits.MaximumWorksheetCount} 个工作表。");
            }

            sheets.Add(ReadSheet(
                reader,
                sourcePath,
                sheets.Count,
                ref workbookCharacterCount,
                cancellationToken));
        }
        while (reader.NextResult());

        if (sheets.Count == 0)
        {
            throw new InvalidDataException("表格中没有可读取的工作表。");
        }

        return new AssetGenerationSpreadsheetWorkbook(sourcePath, sheets);
    }

    private static AssetGenerationSpreadsheetSheet ReadSheet(
        IExcelDataReader reader,
        string sourcePath,
        int sheetIndex,
        ref long workbookCharacterCount,
        CancellationToken cancellationToken)
    {
        var fieldCount = reader.FieldCount;
        if (fieldCount > AssetGenerationSpreadsheetImportLimits.MaximumColumnsPerWorksheet)
        {
            throw new InvalidDataException(
                $"工作表“{ResolveSheetName(reader.Name, sourcePath, sheetIndex)}”包含 {fieldCount} 列，" +
                $"超过 {AssetGenerationSpreadsheetImportLimits.MaximumColumnsPerWorksheet} 列上限。");
        }

        string[]? headerCells = null;
        var rows = new List<AssetGenerationSpreadsheetRow>();
        var sourceRowNumber = 0;
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            sourceRowNumber++;
            if (sourceRowNumber > AssetGenerationSpreadsheetImportLimits.MaximumRowsPerWorksheet)
            {
                throw new InvalidDataException(
                    $"工作表“{ResolveSheetName(reader.Name, sourcePath, sheetIndex)}”超过 " +
                    $"{AssetGenerationSpreadsheetImportLimits.MaximumRowsPerWorksheet} 行上限。空白行也计入安全上限。");
            }

            fieldCount = Math.Max(fieldCount, reader.FieldCount);
            if (fieldCount > AssetGenerationSpreadsheetImportLimits.MaximumColumnsPerWorksheet)
            {
                throw new InvalidDataException(
                    $"工作表“{ResolveSheetName(reader.Name, sourcePath, sheetIndex)}”超过 " +
                    $"{AssetGenerationSpreadsheetImportLimits.MaximumColumnsPerWorksheet} 列上限。");
            }

            var cells = new string[fieldCount];
            var hasContent = false;
            for (var columnIndex = 0; columnIndex < fieldCount; columnIndex++)
            {
                var value = columnIndex < reader.FieldCount
                    ? FormatCachedCellValue(reader.GetValue(columnIndex))
                    : string.Empty;
                ValidateCell(value, sourceRowNumber, columnIndex + 1);
                AddWorkbookCharacters(ref workbookCharacterCount, value.Length);
                cells[columnIndex] = value;
                hasContent |= value.Length > 0;
            }

            if (!hasContent)
            {
                continue;
            }

            if (headerCells is null)
            {
                headerCells = cells;
                continue;
            }

            rows.Add(new AssetGenerationSpreadsheetRow(sourceRowNumber, cells));
        }

        var columns = CreateColumns(headerCells, fieldCount, ref workbookCharacterCount);
        return new AssetGenerationSpreadsheetSheet(
            ResolveSheetName(reader.Name, sourcePath, sheetIndex),
            columns,
            NormalizeRowWidths(rows, fieldCount));
    }

    private static IReadOnlyList<AssetGenerationSpreadsheetRow> NormalizeRowWidths(
        IReadOnlyList<AssetGenerationSpreadsheetRow> rows,
        int fieldCount)
    {
        if (rows.Count == 0 || rows.All(row => row.Cells.Count == fieldCount))
        {
            return rows;
        }

        return rows.Select(row =>
        {
            if (row.Cells.Count == fieldCount)
            {
                return row;
            }

            var cells = new string[fieldCount];
            for (var index = 0; index < row.Cells.Count && index < fieldCount; index++)
            {
                cells[index] = row.Cells[index];
            }

            for (var index = row.Cells.Count; index < fieldCount; index++)
            {
                cells[index] = string.Empty;
            }

            return new AssetGenerationSpreadsheetRow(row.SourceRowNumber, cells);
        }).ToArray();
    }

    private static IReadOnlyList<AssetGenerationSpreadsheetColumn> CreateColumns(
        string[]? headerCells,
        int fieldCount,
        ref long workbookCharacterCount)
    {
        if (fieldCount == 0)
        {
            return [];
        }

        var columns = new AssetGenerationSpreadsheetColumn[fieldCount];
        var duplicateCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < fieldCount; index++)
        {
            var rawHeader = index < (headerCells?.Length ?? 0)
                ? headerCells![index].Trim()
                : string.Empty;
            var baseHeader = rawHeader.Length == 0 ? $"列 {ToSpreadsheetColumnName(index)}" : rawHeader;
            duplicateCounts.TryGetValue(baseHeader, out var duplicateCount);
            duplicateCount++;
            duplicateCounts[baseHeader] = duplicateCount;
            var header = duplicateCount == 1 ? baseHeader : $"{baseHeader} ({duplicateCount})";
            if (!string.Equals(header, rawHeader, StringComparison.Ordinal))
            {
                AddWorkbookCharacters(ref workbookCharacterCount, header.Length);
            }

            columns[index] = new AssetGenerationSpreadsheetColumn(index, header, $"column_{index + 1}");
        }

        return columns;
    }

    private static IExcelDataReader CreateReader(Stream stream, string extension)
    {
        var configuration = new ExcelReaderConfiguration
        {
            FallbackEncoding = LegacyCsvFallbackEncoding,
            LeaveOpen = false
        };

        return extension switch
        {
            ".xlsx" => ExcelReaderFactory.CreateOpenXmlReader(stream, configuration),
            ".xls" => ExcelReaderFactory.CreateBinaryReader(stream, configuration),
            ".csv" => ExcelReaderFactory.CreateCsvReader(stream, new ExcelReaderConfiguration
            {
                FallbackEncoding = LegacyCsvFallbackEncoding,
                AutodetectSeparators = [',', ';'],
                QuoteChar = '"',
                AnalyzeInitialCsvRows = 100,
                LeaveOpen = false
            }),
            ".tsv" => ExcelReaderFactory.CreateCsvReader(stream, new ExcelReaderConfiguration
            {
                FallbackEncoding = LegacyCsvFallbackEncoding,
                AutodetectSeparators = ['\t'],
                QuoteChar = '"',
                AnalyzeInitialCsvRows = 100,
                LeaveOpen = false
            }),
            _ => throw new InvalidDataException($"不支持的表格格式：{extension}")
        };
    }

    private static void ValidateXlsxContainer(Stream stream, CancellationToken cancellationToken)
    {
        try
        {
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            if (archive.Entries.Count == 0 ||
                archive.Entries.Count > MaximumZipEntryCount)
            {
                throw new InvalidDataException(
                    $"XLSX ZIP 条目数量必须位于 1 到 " +
                    $"{MaximumZipEntryCount} 之间。");
            }

            long totalExpandedBytes = 0;
            var hasContentTypes = false;
            var hasWorkbook = false;
            ZipArchiveEntry? contentTypesEntry = null;
            ZipArchiveEntry? sharedStringsEntry = null;
            var entryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var normalizedName = entry.FullName.Replace('\\', '/');
                ValidateZipEntryName(entry.FullName, normalizedName);
                if (!entryNames.Add(normalizedName))
                {
                    throw new InvalidDataException(
                        $"XLSX ZIP 包含大小写重复的条目“{normalizedName}”。");
                }

                if (normalizedName.EndsWith("vbaProject.bin", StringComparison.OrdinalIgnoreCase) ||
                    normalizedName.Contains("/macrosheets/", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        "XLSX ZIP 包含宏工程或宏工作表；当前版本只读取无宏工作簿。");
                }

                if (entry.Length > MaximumZipEntryBytes)
                {
                    throw new InvalidDataException(
                        $"XLSX ZIP 条目“{normalizedName}”展开后超过 " +
                        $"{MaximumZipEntryBytes / 1024 / 1024} MiB 上限。");
                }

                totalExpandedBytes = checked(totalExpandedBytes + entry.Length);
                if (totalExpandedBytes > MaximumZipExpandedBytes)
                {
                    throw new InvalidDataException(
                        $"XLSX ZIP 总展开大小超过 " +
                        $"{MaximumZipExpandedBytes / 1024 / 1024} MiB 上限。");
                }

                if (entry.CompressedLength > 0 &&
                    entry.Length >= 1024L * 1024L &&
                    entry.Length / (double)entry.CompressedLength >
                    MaximumZipCompressionRatio)
                {
                    throw new InvalidDataException(
                        $"XLSX ZIP 条目“{normalizedName}”压缩比异常，已按潜在 ZIP Bomb 拒绝。");
                }

                if (string.Equals(
                    normalizedName,
                    "[Content_Types].xml",
                    StringComparison.OrdinalIgnoreCase))
                {
                    hasContentTypes = true;
                    contentTypesEntry = entry;
                }

                hasWorkbook |= string.Equals(
                    normalizedName,
                    "xl/workbook.xml",
                    StringComparison.OrdinalIgnoreCase);
                if (string.Equals(
                    normalizedName,
                    "xl/sharedStrings.xml",
                    StringComparison.OrdinalIgnoreCase))
                {
                    sharedStringsEntry = entry;
                }
            }

            if (!hasContentTypes || !hasWorkbook)
            {
                throw new InvalidDataException("XLSX ZIP 缺少必要的工作簿结构。");
            }

            ValidateContentTypes(contentTypesEntry!);
            if (sharedStringsEntry is not null)
            {
                ValidateSharedStrings(sharedStringsEntry, cancellationToken);
            }
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or NotSupportedException)
        {
            throw new InvalidDataException("XLSX ZIP 容器无效或无法安全读取。", exception);
        }
    }

    private static void ValidateZipEntryName(string rawName, string normalizedName)
    {
        if (string.IsNullOrWhiteSpace(rawName) ||
            rawName.IndexOf('\\') >= 0 ||
            rawName.Any(char.IsControl) ||
            normalizedName.StartsWith("/", StringComparison.Ordinal) ||
            normalizedName.IndexOf(':') >= 0)
        {
            throw new InvalidDataException("XLSX ZIP 包含不安全的条目路径。");
        }

        var segments = normalizedName.Split('/');
        for (var index = 0; index < segments.Length; index++)
        {
            var segment = segments[index];
            var isTrailingDirectoryMarker = index == segments.Length - 1 && segment.Length == 0;
            if ((!isTrailingDirectoryMarker && segment.Length == 0) || segment is "." or "..")
            {
                throw new InvalidDataException("XLSX ZIP 包含不安全的条目路径。");
            }
        }
    }

    private static void ValidateContentTypes(ZipArchiveEntry entry)
    {
        try
        {
            using var stream = entry.Open();
            using var reader = XmlReader.Create(stream, CreateSafeXmlSettings(MaximumContentTypesXmlCharacters));
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element)
                {
                    continue;
                }

                var contentType = reader.GetAttribute("ContentType");
                if (contentType is not null &&
                    (contentType.Contains("macroEnabled", StringComparison.OrdinalIgnoreCase) ||
                     contentType.Contains("vbaProject", StringComparison.OrdinalIgnoreCase) ||
                     contentType.Contains("macrosheet", StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidDataException(
                        "XLSX 内容类型声明了宏能力；请另存为无宏 .xlsx 后再导入。");
                }
            }
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (exception is XmlException or IOException or InvalidOperationException)
        {
            throw new InvalidDataException("XLSX 内容类型清单无效或无法安全读取。", exception);
        }
    }

    private static void ValidateSharedStrings(
        ZipArchiveEntry entry,
        CancellationToken cancellationToken)
    {
        try
        {
            using var stream = entry.Open();
            using var reader = XmlReader.Create(stream, CreateSafeXmlSettings(MaximumSharedStringsXmlCharacters));
            var stringCount = 0;
            long textCharacterCount = 0;
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (reader.NodeType == XmlNodeType.Element)
                {
                    if (reader.LocalName == "sst")
                    {
                        ValidateSharedStringCountAttribute(reader.GetAttribute("uniqueCount"));
                        ValidateSharedStringCountAttribute(reader.GetAttribute("count"));
                    }
                    else if (reader.LocalName == "si")
                    {
                        stringCount++;
                        if (stringCount > MaximumSharedStringCount)
                        {
                            throw new InvalidDataException(
                                $"XLSX 共享字符串超过 {MaximumSharedStringCount:N0} 项安全上限。");
                        }
                    }
                }
                else if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or
                         XmlNodeType.SignificantWhitespace)
                {
                    textCharacterCount = checked(textCharacterCount + reader.Value.Length);
                    if (textCharacterCount > AssetGenerationSpreadsheetImportLimits.MaximumWorkbookCharacters)
                    {
                        throw new InvalidDataException(
                            "XLSX 共享字符串文本总量超过工作簿安全上限。");
                    }
                }
            }
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (exception is XmlException or IOException or
                                           InvalidOperationException or OverflowException)
        {
            throw new InvalidDataException("XLSX 共享字符串表无效或超过安全上限。", exception);
        }
    }

    private static void ValidateSharedStringCountAttribute(string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return;
        }

        if (!long.TryParse(rawValue, NumberStyles.None, CultureInfo.InvariantCulture, out var count) ||
            count < 0 ||
            count > MaximumSharedStringCount)
        {
            throw new InvalidDataException(
                $"XLSX 共享字符串计数“{rawValue}”无效或超过 {MaximumSharedStringCount:N0} 项安全上限。");
        }
    }

    private static XmlReaderSettings CreateSafeXmlSettings(long maximumCharacters) => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
        MaxCharactersFromEntities = 0,
        MaxCharactersInDocument = maximumCharacters
    };

    private static string NormalizeSourcePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("表格路径不能为空。", nameof(path));
        }

        var sourcePath = Path.GetFullPath(path.Trim());
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("找不到要导入的表格文件。", sourcePath);
        }

        return sourcePath;
    }

    private static void ValidateExtension(string extension)
    {
        if (ExplicitlyRejectedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"当前版本明确不支持 {extension}；请另存为 .xlsx、.xls、.csv 或 .tsv 后导入。");
        }

        if (!SupportedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("仅支持 .xlsx、.xls、.csv 和 .tsv 表格。");
        }
    }

    private static void ValidateFileLength(long length)
    {
        if (length <= 0)
        {
            throw new InvalidDataException("表格文件为空。");
        }

        if (length > AssetGenerationSpreadsheetImportLimits.MaximumFileBytes)
        {
            throw new InvalidDataException(
                $"表格文件超过 {AssetGenerationSpreadsheetImportLimits.MaximumFileBytes / 1024 / 1024} MiB 上限。");
        }
    }

    private static void ValidateCell(string value, int rowNumber, int columnNumber)
    {
        if (value.Length > AssetGenerationSpreadsheetImportLimits.MaximumCellCharacters)
        {
            throw new InvalidDataException(
                $"单元格 R{rowNumber}C{columnNumber} 超过 " +
                $"{AssetGenerationSpreadsheetImportLimits.MaximumCellCharacters} 个字符上限。");
        }

        if (value.Contains('\0', StringComparison.Ordinal))
        {
            throw new InvalidDataException($"单元格 R{rowNumber}C{columnNumber} 包含 NUL 字符。");
        }
    }

    private static void AddWorkbookCharacters(ref long total, int count)
    {
        total = checked(total + count);
        if (total > AssetGenerationSpreadsheetImportLimits.MaximumWorkbookCharacters)
        {
            throw new InvalidDataException(
                $"工作簿文本总量超过 " +
                $"{AssetGenerationSpreadsheetImportLimits.MaximumWorkbookCharacters / 1024 / 1024} MiB 字符上限。");
        }
    }

    private static string FormatCachedCellValue(object? value) => value switch
    {
        null or DBNull => string.Empty,
        string text => text,
        DateTime dateTime => dateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        DateTimeOffset dateTimeOffset => dateTimeOffset.ToString("O", CultureInfo.InvariantCulture),
        TimeSpan timeSpan => timeSpan.ToString("c", CultureInfo.InvariantCulture),
        bool boolean => boolean ? "TRUE" : "FALSE",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty,
        _ => value.ToString() ?? string.Empty
    };

    private static string ResolveSheetName(string? readerName, string sourcePath, int sheetIndex)
    {
        var normalized = readerName?.Trim();
        return string.IsNullOrWhiteSpace(normalized)
            ? sheetIndex == 0
                ? Path.GetFileNameWithoutExtension(sourcePath)
                : $"工作表 {sheetIndex + 1}"
            : normalized;
    }

    private static string ToSpreadsheetColumnName(int zeroBasedIndex)
    {
        var value = zeroBasedIndex + 1;
        var builder = new StringBuilder();
        while (value > 0)
        {
            value--;
            builder.Insert(0, (char)('A' + value % 26));
            value /= 26;
        }

        return builder.ToString();
    }
}
