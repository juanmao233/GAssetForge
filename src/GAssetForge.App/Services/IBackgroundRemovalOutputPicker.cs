namespace GAssetForge.App.Services;

public interface IBackgroundRemovalOutputPicker
{
    string? SelectOutputFile(string? initialDirectory, string suggestedFileName);
}
