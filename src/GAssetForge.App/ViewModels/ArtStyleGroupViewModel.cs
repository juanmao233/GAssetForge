using GAssetForge.App.Infrastructure;
using GAssetForge.Core.ArtStyles;
using System.Collections.ObjectModel;

namespace GAssetForge.App.ViewModels;

public sealed class ArtStyleGroupViewModel : ObservableObject
{
    private readonly Func<ArtStyleGroupViewModel, bool> _persistEdit;
    private readonly Action<string> _reportValidationError;
    private string _name;
    private string _description;
    private string _stylePrompt;

    internal ArtStyleGroupViewModel(
        ArtStyleGroupModel model,
        IEnumerable<ArtStyleAssetViewModel> assets,
        Func<ArtStyleGroupViewModel, bool> persistEdit,
        Action<string> reportValidationError)
    {
        Model = model ?? throw new ArgumentNullException(nameof(model));
        _persistEdit = persistEdit ?? throw new ArgumentNullException(nameof(persistEdit));
        _reportValidationError = reportValidationError ?? throw new ArgumentNullException(nameof(reportValidationError));
        _name = model.Name;
        _description = model.Description;
        _stylePrompt = model.Prompt;
        foreach (var asset in assets)
        {
            Assets.Add(asset);
        }
    }

    internal ArtStyleGroupModel Model { get; }

    public string Id => Model.Id;

    public ObservableCollection<ArtStyleAssetViewModel> Assets { get; } = [];

    public string Name
    {
        get => _name;
        set
        {
            value ??= string.Empty;
            if (string.Equals(_name, value, StringComparison.Ordinal))
            {
                return;
            }

            try
            {
                ArtStyleLibraryValidator.ValidateGroupName(value);
            }
            catch (ArgumentException exception)
            {
                _reportValidationError(exception.Message);
                OnPropertyChanged();
                return;
            }

            var previousValue = _name;
            var previousUpdatedAt = Model.UpdatedAtUtc;
            _name = value;
            Model.Name = value;
            Model.UpdatedAtUtc = DateTimeOffset.UtcNow;
            OnPropertyChanged();
            if (!_persistEdit(this))
            {
                _name = previousValue;
                Model.Name = previousValue;
                Model.UpdatedAtUtc = previousUpdatedAt;
                OnPropertyChanged();
            }
        }
    }

    public string Description
    {
        get => _description;
        set
        {
            value ??= string.Empty;
            if (string.Equals(_description, value, StringComparison.Ordinal))
            {
                return;
            }

            if (value.Length > ArtStyleLibraryValidator.MaximumDescriptionLength)
            {
                _reportValidationError(
                    $"风格描述不能超过 {ArtStyleLibraryValidator.MaximumDescriptionLength} 个字符。");
                OnPropertyChanged();
                return;
            }

            var previousValue = _description;
            var previousUpdatedAt = Model.UpdatedAtUtc;
            _description = value;
            Model.Description = value;
            Model.UpdatedAtUtc = DateTimeOffset.UtcNow;
            OnPropertyChanged();
            if (!_persistEdit(this))
            {
                _description = previousValue;
                Model.Description = previousValue;
                Model.UpdatedAtUtc = previousUpdatedAt;
                OnPropertyChanged();
            }
        }
    }

    public string StylePrompt
    {
        get => _stylePrompt;
        set
        {
            value ??= string.Empty;
            if (string.Equals(_stylePrompt, value, StringComparison.Ordinal))
            {
                return;
            }

            if (value.Length > ArtStyleLibraryValidator.MaximumPromptLength)
            {
                _reportValidationError(
                    $"AI 风格提示词不能超过 {ArtStyleLibraryValidator.MaximumPromptLength} 个字符。");
                OnPropertyChanged();
                return;
            }

            var previousValue = _stylePrompt;
            var previousUpdatedAt = Model.UpdatedAtUtc;
            _stylePrompt = value;
            Model.Prompt = value;
            Model.UpdatedAtUtc = DateTimeOffset.UtcNow;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Prompt));
            if (!_persistEdit(this))
            {
                _stylePrompt = previousValue;
                Model.Prompt = previousValue;
                Model.UpdatedAtUtc = previousUpdatedAt;
                OnPropertyChanged();
                OnPropertyChanged(nameof(Prompt));
            }
        }
    }

    public string Prompt
    {
        get => StylePrompt;
        set => StylePrompt = value;
    }

    public string AssetCountText => $"{Assets.Count} 张参考素材";

    internal void AddAssets(IEnumerable<ArtStyleAssetViewModel> assets)
    {
        foreach (var asset in assets)
        {
            Assets.Add(asset);
        }

        OnPropertyChanged(nameof(AssetCountText));
    }

    internal void RemoveAsset(ArtStyleAssetViewModel asset)
    {
        Assets.Remove(asset);
        OnPropertyChanged(nameof(AssetCountText));
    }
}
