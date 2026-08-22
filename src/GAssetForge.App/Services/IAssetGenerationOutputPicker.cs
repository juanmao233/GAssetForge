namespace GAssetForge.App.Services;

public interface IAssetGenerationOutputPicker
{
    string? SelectOutputDirectory(string initialPath);
}
