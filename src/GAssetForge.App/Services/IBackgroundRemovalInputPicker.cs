namespace GAssetForge.App.Services;

public interface IBackgroundRemovalInputPicker
{
    string? SelectInputFile(string? initialDirectory);
}
