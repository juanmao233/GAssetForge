using GAssetForge.App.Infrastructure;
using GAssetForge.Core.ArtStyles;
using System.Collections.ObjectModel;
using System.IO;

namespace GAssetForge.App.ViewModels;

public sealed class ArtStyleGroupViewModel : ObservableObject
{
    private readonly Func<ArtStyleGroupViewModel, bool> _persistEdit;
    private readonly Action<string> _reportValidationError;
    private string _name;
    private string _description;
    private string _stylePrompt;
    private string _unifiedPromptSourceFingerprint;

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
        _unifiedPromptSourceFingerprint = string.Empty;
        foreach (var asset in assets)
        {
            Assets.Add(asset);
        }

        _unifiedPromptSourceFingerprint = ArtStylePromptFingerprint.Compute(Model);
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
            RefreshUnifiedPromptSourceFingerprint();
            if (!_persistEdit(this))
            {
                _description = previousValue;
                Model.Description = previousValue;
                Model.UpdatedAtUtc = previousUpdatedAt;
                OnPropertyChanged();
                RefreshUnifiedPromptSourceFingerprint();
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
            RefreshUnifiedPromptSourceFingerprint();
            if (!_persistEdit(this))
            {
                _stylePrompt = previousValue;
                Model.Prompt = previousValue;
                Model.UpdatedAtUtc = previousUpdatedAt;
                OnPropertyChanged();
                OnPropertyChanged(nameof(Prompt));
                RefreshUnifiedPromptSourceFingerprint();
            }
        }
    }

    public string Prompt
    {
        get => StylePrompt;
        set => StylePrompt = value;
    }

    public ArtStylePromptSampleModel? UnifiedPromptSample => Model.UnifiedPromptSample;

    public string UnifiedPromptSourceFingerprint => _unifiedPromptSourceFingerprint;

    public string UnifiedPromptText => UnifiedPromptSample?.Prompt ?? string.Empty;

    public string UnifiedPromptSummary => UnifiedPromptSample?.Summary ?? string.Empty;

    public DateTimeOffset? UnifiedPromptGeneratedAtUtc => UnifiedPromptSample?.GeneratedAtUtc;

    public string UnifiedPromptGeneratedAtText => UnifiedPromptGeneratedAtUtc is { } generatedAt
        ? generatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
        : "尚未生成";

    public bool HasUnifiedPrompt => !string.IsNullOrWhiteSpace(UnifiedPromptText);

    public bool IsUnifiedPromptFresh =>
        UnifiedPromptSample is { } sample &&
        HasUnifiedPrompt &&
        string.Equals(
            sample.SourceFingerprint,
            UnifiedPromptSourceFingerprint,
            StringComparison.OrdinalIgnoreCase);

    public string UnifiedPromptStateText => !HasUnifiedPrompt
        ? "尚未生成统一提示词样本"
        : IsUnifiedPromptFresh
            ? "统一提示词样本可用"
            : "素材或人工风格定义已变化，请重新分析";

    public bool ApplyUnifiedPromptSample(ArtStylePromptSampleModel sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        var safeSample = ClonePromptSample(sample);
        try
        {
            ArtStyleLibraryValidator.ValidatePromptSample(Model, safeSample);
        }
        catch (InvalidDataException exception)
        {
            _reportValidationError(exception.Message);
            return false;
        }

        var currentFingerprint = UnifiedPromptSourceFingerprint;
        if (!string.Equals(
                currentFingerprint,
                safeSample.SourceFingerprint,
                StringComparison.OrdinalIgnoreCase))
        {
            _reportValidationError("风格组内容已在分析期间发生变化，请重新生成统一提示词样本。");
            return false;
        }

        var previousSample = Model.UnifiedPromptSample;
        var previousUpdatedAt = Model.UpdatedAtUtc;
        Model.UnifiedPromptSample = safeSample;
        Model.UpdatedAtUtc = DateTimeOffset.UtcNow;
        NotifyUnifiedPromptStateChanged();
        if (_persistEdit(this))
        {
            return true;
        }

        Model.UnifiedPromptSample = previousSample;
        Model.UpdatedAtUtc = previousUpdatedAt;
        NotifyUnifiedPromptStateChanged();
        return false;
    }

    public string AssetCountText => $"{Assets.Count} 张参考素材";

    internal void AddAssets(IEnumerable<ArtStyleAssetViewModel> assets)
    {
        foreach (var asset in assets)
        {
            Assets.Add(asset);
        }

        OnPropertyChanged(nameof(AssetCountText));
        RefreshUnifiedPromptSourceFingerprint();
    }

    internal void RemoveAsset(ArtStyleAssetViewModel asset)
    {
        Assets.Remove(asset);
        OnPropertyChanged(nameof(AssetCountText));
        RefreshUnifiedPromptSourceFingerprint();
    }

    private void RefreshUnifiedPromptSourceFingerprint()
    {
        _unifiedPromptSourceFingerprint = ArtStylePromptFingerprint.Compute(Model);
        NotifyUnifiedPromptStateChanged();
    }

    private void NotifyUnifiedPromptStateChanged()
    {
        OnPropertyChanged(nameof(UnifiedPromptSample));
        OnPropertyChanged(nameof(UnifiedPromptSourceFingerprint));
        OnPropertyChanged(nameof(UnifiedPromptText));
        OnPropertyChanged(nameof(UnifiedPromptSummary));
        OnPropertyChanged(nameof(UnifiedPromptGeneratedAtUtc));
        OnPropertyChanged(nameof(UnifiedPromptGeneratedAtText));
        OnPropertyChanged(nameof(HasUnifiedPrompt));
        OnPropertyChanged(nameof(IsUnifiedPromptFresh));
        OnPropertyChanged(nameof(UnifiedPromptStateText));
    }

    private static ArtStylePromptSampleModel ClonePromptSample(ArtStylePromptSampleModel sample) =>
        new()
        {
            SchemaVersion = sample.SchemaVersion,
            Prompt = sample.Prompt,
            Summary = sample.Summary,
            SourceFingerprint = sample.SourceFingerprint,
            GeneratedAtUtc = sample.GeneratedAtUtc,
            AnalyzerId = sample.AnalyzerId,
            AnalyzerVersion = sample.AnalyzerVersion,
            RequestId = sample.RequestId,
            AnalyzedAssetIds = sample.AnalyzedAssetIds?.ToList() ?? [],
            AnalyzedAssetCount = sample.AnalyzedAssetCount
        };
}
