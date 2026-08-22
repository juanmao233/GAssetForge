using GAssetForge.Core.Projects;

namespace GAssetForge.App.ViewModels;

public sealed class PrefabViewModel(UiPrefabAssetModel model)
{
    public UiPrefabAssetModel Model { get; } = model;
    public string Id => Model.Id;
    public string Name => Model.Name;
    public string Tone => Model.Tone;
    public string ComponentType => Model.Payload.RootIds.Count > 0 &&
        Model.Payload.LayersById.TryGetValue(Model.Payload.RootIds[0], out var layer)
            ? layer.Component?.Type ?? layer.Kind
            : "prefab";
    public string SizeLabel => $"{Math.Round(Model.Payload.Bounds.Width)} × {Math.Round(Model.Payload.Bounds.Height)}";
}
