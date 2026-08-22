using GAssetForge.App.Infrastructure;
using GAssetForge.Core.Projects;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json.Nodes;

namespace GAssetForge.App.ViewModels;

public sealed class LayerViewModel : ObservableObject
{
    private readonly EditorWorkspaceViewModel _owner;
    private bool _isSelected;
    private bool _isExpanded = true;
    private bool _effectiveVisibility = true;
    private bool _effectiveLocked;
    private double _effectiveOpacity = 1;

    public LayerViewModel(EditorWorkspaceViewModel owner, UiLayerModel model)
    {
        _owner = owner;
        Model = model;
    }

    public UiLayerModel Model { get; }
    public ObservableCollection<LayerViewModel> Children { get; } = [];
    public string Id => Model.Id;
    public string Kind => Model.Kind;
    public bool IsGroup => string.Equals(Model.Kind, "group", StringComparison.Ordinal);
    public bool IsDrawable => Model.Transform is not null;
    public bool IsText => string.Equals(Model.Kind, "text", StringComparison.Ordinal);
    public bool IsPage => string.Equals(Model.Component?.Type, "page", StringComparison.Ordinal);
    public string ComponentType => Model.Component?.Type ?? Model.Kind;
    public string TypeLabel => IsGroup ? $"Folder · {Children.Count} 项" : ComponentType;
    public int ZIndex { get; internal set; }

    public string Name
    {
        get => Model.Name;
        set
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                _owner.EditLayer(Id, "重命名图层", layer => layer.Name = value, nameof(Name));
            }
        }
    }

    public double X
    {
        get => Model.Transform?.X ?? 0;
        set
        {
            if (IsEditable && double.IsFinite(value))
            {
                _owner.EditLayer(Id, "修改图层 X", layer =>
                {
                    if (layer.Transform is { } transform)
                    {
                        transform.X = value;
                    }
                }, nameof(X));
            }
        }
    }

    public double Y
    {
        get => Model.Transform?.Y ?? 0;
        set
        {
            if (IsEditable && double.IsFinite(value))
            {
                _owner.EditLayer(Id, "修改图层 Y", layer =>
                {
                    if (layer.Transform is { } transform)
                    {
                        transform.Y = value;
                    }
                }, nameof(Y));
            }
        }
    }

    public double Width
    {
        get => Model.Transform?.Width ?? 0;
        set
        {
            if (IsEditable && double.IsFinite(value))
            {
                _owner.EditLayer(Id, "修改图层宽度", layer =>
                {
                    if (layer.Transform is { } transform)
                    {
                        transform.Width = Math.Max(8, value);
                    }
                }, nameof(Width));
            }
        }
    }

    public double Height
    {
        get => Model.Transform?.Height ?? 0;
        set
        {
            if (IsEditable && double.IsFinite(value))
            {
                _owner.EditLayer(Id, "修改图层高度", layer =>
                {
                    if (layer.Transform is { } transform)
                    {
                        transform.Height = Math.Max(8, value);
                    }
                }, nameof(Height));
            }
        }
    }

    public double Radius
    {
        get => Model.Style?.Radius ?? 0;
        set
        {
            if (IsEditable && double.IsFinite(value))
            {
                _owner.EditLayer(Id, "修改图层圆角", layer =>
                {
                    if (layer.Style is { } style)
                    {
                        style.Radius = Math.Max(0, value);
                    }
                }, nameof(Radius));
            }
        }
    }

    public double OpacityPercent
    {
        get => Math.Round(Model.Opacity * 100);
        set
        {
            if (IsEditable && double.IsFinite(value))
            {
                _owner.EditLayer(Id, "修改图层透明度", layer => layer.Opacity = Math.Clamp(value / 100, 0, 1), nameof(OpacityPercent));
            }
        }
    }

    public double Opacity => Model.Opacity;

    public bool IsVisible
    {
        get => Model.Visible;
        set => _owner.EditLayer(Id, value ? "显示图层" : "隐藏图层", layer => layer.Visible = value, nameof(IsVisible));
    }

    public bool IsLocked
    {
        get => Model.Locked;
        set => _owner.EditLayer(Id, value ? "锁定图层" : "解锁图层", layer => layer.Locked = value, nameof(IsLocked));
    }

    public string TextValue
    {
        get => Model.Text?.Value ?? PrimaryText;
        set
        {
            if (!IsEditable)
            {
                return;
            }

            _owner.EditLayer(Id, "修改文字内容", layer =>
            {
                if (layer.Text is not null)
                {
                    layer.Text.Value = value;
                }
                else if (layer.Component is not null)
                {
                    layer.Component.Props["label"] = value;
                }
            }, nameof(TextValue));
        }
    }

    public string PrimaryText => Model.Text?.Value
        ?? ReadString("label")
        ?? ReadString("title")
        ?? Model.Name;

    public string SecondaryText => ReadString("subtitle")
        ?? ReadString("placeholder")
        ?? ReadString("eyebrow")
        ?? string.Empty;

    public string TertiaryText => ReadString("rows")
        ?? ReadString("items")
        ?? string.Empty;

    public string Variant => ReadString("variant") ?? "default";
    public double SliderValue => ReadDouble("value") ?? 60;
    public bool ToggleChecked => ReadBoolean("checked") ?? true;

    public bool IsSelected
    {
        get => _isSelected;
        internal set => SetProperty(ref _isSelected, value);
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    public bool EffectiveVisibility
    {
        get => _effectiveVisibility;
        internal set => SetProperty(ref _effectiveVisibility, value);
    }

    public bool EffectiveLocked
    {
        get => _effectiveLocked;
        internal set
        {
            if (SetProperty(ref _effectiveLocked, value))
            {
                OnPropertyChanged(nameof(IsEditable));
            }
        }
    }

    public bool IsEditable => !EffectiveLocked;

    public double EffectiveOpacity
    {
        get => _effectiveOpacity;
        internal set => SetProperty(ref _effectiveOpacity, value);
    }

    internal void RefreshGeometry()
    {
        OnPropertyChanged(nameof(X));
        OnPropertyChanged(nameof(Y));
        OnPropertyChanged(nameof(Width));
        OnPropertyChanged(nameof(Height));
    }

    internal void RefreshContent()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(TypeLabel));
        OnPropertyChanged(nameof(PrimaryText));
        OnPropertyChanged(nameof(SecondaryText));
        OnPropertyChanged(nameof(TertiaryText));
        OnPropertyChanged(nameof(TextValue));
        OnPropertyChanged(nameof(IsVisible));
        OnPropertyChanged(nameof(IsLocked));
        OnPropertyChanged(nameof(OpacityPercent));
        OnPropertyChanged(nameof(Opacity));
        OnPropertyChanged(nameof(Radius));
        OnPropertyChanged(nameof(Variant));
        OnPropertyChanged(nameof(SliderValue));
        OnPropertyChanged(nameof(ToggleChecked));
    }

    private string? ReadString(string key)
    {
        if (Model.Component?.Props[key] is not JsonNode node)
        {
            return null;
        }

        if (node is JsonValue value && value.TryGetValue<string>(out var text))
        {
            return text;
        }

        if (node is JsonArray array)
        {
            return string.Join(" · ", array.Select(item =>
                item is JsonValue itemValue && itemValue.TryGetValue<string>(out var itemText)
                    ? itemText
                    : item?.ToJsonString() ?? string.Empty));
        }

        return node.ToJsonString().Trim('"').Replace("\\u00b7", "·", StringComparison.OrdinalIgnoreCase);
    }

    private double? ReadDouble(string key)
    {
        var rawJson = Model.Component?.Props[key]?.ToJsonString();
        return double.TryParse(rawJson, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) &&
               double.IsFinite(number)
            ? number
            : null;
    }

    private bool? ReadBoolean(string key) =>
        Model.Component?.Props[key] is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : null;
}
