namespace GAssetForge.App.Services;

public interface IImageOutlineOutputPicker
{
    string? SelectOutputFile(string? initialDirectory, string suggestedFileName);

    string? SelectOutputDirectory(string? initialDirectory) => null;
}
