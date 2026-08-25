using GAssetForge.App.Infrastructure;

namespace GAssetForge.App.ViewModels;

public sealed class AssetGenerationSpreadsheetRowViewModel : ObservableObject
{
    private bool _isSelected;
    private string _resolvedDescription = string.Empty;
    private string _validationMessage = "尚未选择物品描述列";

    public AssetGenerationSpreadsheetRowViewModel(
        int sourceRowNumber,
        IReadOnlyList<string> cells)
    {
        SourceRowNumber = sourceRowNumber;
        Cells = cells ?? throw new ArgumentNullException(nameof(cells));
    }

    public event EventHandler? SelectionChanged;

    public int SourceRowNumber { get; }

    public IReadOnlyList<string> Cells { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (value && !CanImport)
            {
                value = false;
            }

            if (SetProperty(ref _isSelected, value))
            {
                SelectionChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public string ResolvedDescription
    {
        get => _resolvedDescription;
        private set => SetProperty(ref _resolvedDescription, value);
    }

    public string ValidationMessage
    {
        get => _validationMessage;
        private set
        {
            if (SetProperty(ref _validationMessage, value))
            {
                OnPropertyChanged(nameof(CanImport));
            }
        }
    }

    public bool CanImport => ValidationMessage.Length == 0;

    public void ApplyDescriptionColumn(int? columnIndex)
    {
        if (columnIndex is null || columnIndex < 0 || columnIndex >= Cells.Count)
        {
            ResolvedDescription = string.Empty;
            ValidationMessage = "尚未选择物品描述列";
            IsSelected = false;
            return;
        }

        var description = (Cells[columnIndex.Value] ?? string.Empty).Trim();
        ResolvedDescription = description;
        ValidationMessage = description.Length switch
        {
            0 => "该行物品描述为空",
            > 16_384 => "该行物品描述超过 16384 字符",
            _ when description.IndexOf('\0') >= 0 => "该行物品描述包含无效字符",
            _ => string.Empty
        };
        if (!CanImport)
        {
            IsSelected = false;
        }
    }
}
