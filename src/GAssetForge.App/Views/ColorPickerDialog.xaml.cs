using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GAssetForge.App.ViewModels;
using GAssetForge.Core.Imaging;

namespace GAssetForge.App.Views;

public partial class ColorPickerDialog : Window
{
    public ColorPickerDialog(string? initialColorHex = null)
        : this(new ColorPickerViewModel(initialColorHex))
    {
    }

    public ColorPickerDialog(RgbColor initialColor)
        : this(new ColorPickerViewModel(initialColor))
    {
    }

    public ColorPickerDialog(ColorPickerViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = ViewModel;
    }

    public ColorPickerViewModel ViewModel { get; }

    public RgbColor SelectedRgb => ViewModel.SelectedRgb;

    public HsvColor SelectedHsv => ViewModel.SelectedHsv;

    public string SelectedColorHex => ViewModel.SelectedHex;

    private void OnAcceptClick(object sender, RoutedEventArgs e)
    {
        var editors = new[]
        {
            HueTextBox,
            SaturationTextBox,
            ValueTextBox,
            RedTextBox,
            GreenTextBox,
            BlueTextBox,
            HexTextBox
        };

        // The default button can be invoked with Enter without moving keyboard focus. Commit
        // that editor first so its pending value drives the synchronized HSV/RGB fields before
        // the remaining bindings are updated.
        var focusedEditor = Keyboard.FocusedElement as TextBox;
        if (focusedEditor is not null && editors.Contains(focusedEditor))
        {
            focusedEditor.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            if (Validation.GetHasError(focusedEditor) || ViewModel.HasHexError)
            {
                FocusInvalidEditor(Validation.GetHasError(focusedEditor) ? focusedEditor : HexTextBox);
                return;
            }
        }

        foreach (var editor in editors)
        {
            if (!ReferenceEquals(editor, focusedEditor))
            {
                editor.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            }

            if (Validation.GetHasError(editor))
            {
                FocusInvalidEditor(editor);
                return;
            }
        }

        if (ViewModel.HasHexError)
        {
            FocusInvalidEditor(HexTextBox);
            return;
        }

        DialogResult = true;
    }

    private static void FocusInvalidEditor(TextBox editor)
    {
        editor.Focus();
        editor.SelectAll();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
