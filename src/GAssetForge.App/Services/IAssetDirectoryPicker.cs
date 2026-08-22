namespace GAssetForge.App.Services;

public interface IAssetDirectoryPicker
{
    string? SelectFolder(string initialPath);
}
