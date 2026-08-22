namespace GAssetForge.App.Services;

public interface IProjectLocationPicker
{
    string? SelectParentDirectory(string initialPath);
}
