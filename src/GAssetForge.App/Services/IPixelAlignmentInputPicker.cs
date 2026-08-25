namespace GAssetForge.App.Services;

public interface IPixelAlignmentInputPicker
{
    string? SelectInputFile(string? initialDirectory);

    IReadOnlyList<string> SelectInputFiles(string? initialDirectory)
    {
        var selected = SelectInputFile(initialDirectory);
        return string.IsNullOrWhiteSpace(selected) ? [] : [selected];
    }
}
