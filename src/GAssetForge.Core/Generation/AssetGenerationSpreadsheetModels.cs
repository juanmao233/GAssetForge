namespace GAssetForge.Core.Generation;

public static class AssetGenerationSpreadsheetImportLimits
{
    public const long MaximumFileBytes = 32L * 1024L * 1024L;
    public const int MaximumWorksheetCount = 16;
    public const int MaximumRowsPerWorksheet = 2_000;
    public const int MaximumColumnsPerWorksheet = 128;
    public const int MaximumCellCharacters = 16_384;
    public const long MaximumWorkbookCharacters = 8L * 1024L * 1024L;

}

public interface IAssetGenerationSpreadsheetReader
{
    Task<AssetGenerationSpreadsheetWorkbook> ReadAsync(
        string path,
        CancellationToken cancellationToken = default);
}

public sealed record AssetGenerationSpreadsheetWorkbook(
    string SourcePath,
    IReadOnlyList<AssetGenerationSpreadsheetSheet> Sheets);

public sealed record AssetGenerationSpreadsheetSheet(
    string Name,
    IReadOnlyList<AssetGenerationSpreadsheetColumn> Columns,
    IReadOnlyList<AssetGenerationSpreadsheetRow> Rows);

public sealed record AssetGenerationSpreadsheetColumn(
    int Index,
    string Header,
    string Key);

public sealed record AssetGenerationSpreadsheetRow(
    int SourceRowNumber,
    IReadOnlyList<string> Cells);
