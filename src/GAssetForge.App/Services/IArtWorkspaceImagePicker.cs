namespace GAssetForge.App.Services;

public interface IArtWorkspaceImagePicker
{
    IReadOnlyList<string> SelectImages(string? initialDirectory);
}
