namespace GAssetForge.App.Services;

public interface IPixelAlignmentOutputPicker
{
    string? SelectOutputFile(string? initialDirectory, string suggestedFileName, string title);

    string? SelectOutputDirectory(string? initialDirectory) => null;
}
