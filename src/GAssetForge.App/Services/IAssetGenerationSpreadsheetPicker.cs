namespace GAssetForge.App.Services;

public interface IAssetGenerationSpreadsheetPicker
{
    string? SelectSpreadsheet(string? initialDirectory);
}
