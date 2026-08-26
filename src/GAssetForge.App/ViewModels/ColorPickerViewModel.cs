using System.Windows.Input;
using System.Windows.Media;
using GAssetForge.App.Infrastructure;
using GAssetForge.App.Services;
using GAssetForge.Core.Imaging;

namespace GAssetForge.App.ViewModels;

/// <summary>
/// Keeps the RGB, HSV and hexadecimal editing surfaces in sync for a reusable color picker.
/// </summary>
public sealed class ColorPickerViewModel : ObservableObject
{
    private readonly RgbColor _originalRgb;
    private RgbColor _selectedRgb;
    private HsvColor _selectedHsv;
    private string _hexInput;
    private string _hexValidationMessage = string.Empty;
    private double _fallbackHue;
    private readonly IScreenEyedropperService _screenEyedropperService;

    public ColorPickerViewModel(
        string? initialColorHex = null,
        IScreenEyedropperService? screenEyedropperService = null)
        : this(ColorConversion.TryParseHex(initialColorHex, out var parsed)
            ? parsed
            : new RgbColor(0, 0, 0),
            screenEyedropperService)
    {
    }

    public ColorPickerViewModel(
        RgbColor initialColor,
        IScreenEyedropperService? screenEyedropperService = null)
    {
        _screenEyedropperService = screenEyedropperService ?? new DesktopScreenEyedropperService();
        _originalRgb = initialColor;
        _selectedRgb = initialColor;
        _selectedHsv = ColorConversion.ToHsv(initialColor);
        _fallbackHue = _selectedHsv.Hue;
        _hexInput = initialColor.Hex;
        ResetToOriginalCommand = new RelayCommand(ResetToOriginal);
        PickScreenColorCommand = new RelayCommand(PickScreenColor);
    }

    public int Red
    {
        get => _selectedRgb.Red;
        set => ApplyRgb(RgbColor.FromInts(value, Green, Blue));
    }

    public int Green
    {
        get => _selectedRgb.Green;
        set => ApplyRgb(RgbColor.FromInts(Red, value, Blue));
    }

    public int Blue
    {
        get => _selectedRgb.Blue;
        set => ApplyRgb(RgbColor.FromInts(Red, Green, value));
    }

    /// <summary>Hue in degrees, normalized to [0, 360).</summary>
    public double Hue
    {
        get => _selectedHsv.Hue;
        set
        {
            var normalized = ColorConversion.NormalizeHue(value);
            _fallbackHue = normalized;
            ApplyHsv(new HsvColor(normalized, Saturation, Value));
        }
    }

    /// <summary>Saturation as a normalized value in [0, 1].</summary>
    public double Saturation
    {
        get => _selectedHsv.Saturation;
        set => ApplyHsv(new HsvColor(Hue, value, Value));
    }

    /// <summary>Brightness/value as a normalized value in [0, 1].</summary>
    public double Value
    {
        get => _selectedHsv.Value;
        set => ApplyHsv(new HsvColor(Hue, Saturation, value));
    }

    public double SaturationPercent
    {
        get => Saturation * 100d;
        set => Saturation = value / 100d;
    }

    public double ValuePercent
    {
        get => Value * 100d;
        set => Value = value / 100d;
    }

    /// <summary>
    /// Editable hexadecimal text. Invalid intermediate input is retained without mutating
    /// the selected color, which makes typing and pasting predictable.
    /// </summary>
    public string HexInput
    {
        get => _hexInput;
        set
        {
            value ??= string.Empty;
            if (string.Equals(_hexInput, value, StringComparison.Ordinal))
            {
                return;
            }

            _hexInput = value;
            OnPropertyChanged();

            if (ColorConversion.TryParseHex(value, out var parsed))
            {
                ApplyRgb(parsed);
            }
            else
            {
                SetHexValidation("请输入 6 位十六进制颜色，例如 #4A90E2。");
            }
        }
    }

    public string SelectedHex => _selectedRgb.Hex;

    public RgbColor SelectedRgb => _selectedRgb;

    public HsvColor SelectedHsv => _selectedHsv;

    public string OriginalHex => _originalRgb.Hex;

    public Brush SelectedColorBrush => CreateBrush(_selectedRgb);

    public Brush OriginalColorBrush => CreateBrush(_originalRgb);

    public string HexValidationMessage
    {
        get => _hexValidationMessage;
        private set => SetProperty(ref _hexValidationMessage, value);
    }

    public bool HasHexError => !string.IsNullOrEmpty(HexValidationMessage);

    public ICommand ResetToOriginalCommand { get; }

    public ICommand PickScreenColorCommand { get; }

    public void SetColor(RgbColor color) => ApplyRgb(color);

    public void ResetToOriginal() => ApplyRgb(_originalRgb);

    private void PickScreenColor()
    {
        var sampledColor = _screenEyedropperService.PickColor();
        if (sampledColor is { } color)
        {
            SetColor(color);
        }
    }

    private void ApplyRgb(RgbColor rgb)
    {
        var previousRgb = _selectedRgb;
        var previousHsv = _selectedHsv;
        _selectedRgb = rgb;
        _selectedHsv = ColorConversion.ToHsv(rgb, _fallbackHue);
        if (_selectedHsv.Saturation > 0d)
        {
            _fallbackHue = _selectedHsv.Hue;
        }

        SynchronizeHexInput();
        RaiseColorProperties(previousRgb, previousHsv);
    }

    private void ApplyHsv(HsvColor hsv)
    {
        var previousRgb = _selectedRgb;
        var previousHsv = _selectedHsv;
        _selectedHsv = hsv;
        _fallbackHue = hsv.Hue;
        _selectedRgb = ColorConversion.ToRgb(hsv);

        SynchronizeHexInput();
        RaiseColorProperties(previousRgb, previousHsv);
    }

    private void SynchronizeHexInput()
    {
        var normalized = _selectedRgb.Hex;
        if (!string.Equals(_hexInput, normalized, StringComparison.Ordinal))
        {
            _hexInput = normalized;
            OnPropertyChanged(nameof(HexInput));
        }

        SetHexValidation(string.Empty);
    }

    private void RaiseColorProperties(RgbColor previousRgb, HsvColor previousHsv)
    {
        if (previousRgb.Red != _selectedRgb.Red)
        {
            OnPropertyChanged(nameof(Red));
        }

        if (previousRgb.Green != _selectedRgb.Green)
        {
            OnPropertyChanged(nameof(Green));
        }

        if (previousRgb.Blue != _selectedRgb.Blue)
        {
            OnPropertyChanged(nameof(Blue));
        }

        if (!NearlyEqual(previousHsv.Hue, _selectedHsv.Hue))
        {
            OnPropertyChanged(nameof(Hue));
        }

        if (!NearlyEqual(previousHsv.Saturation, _selectedHsv.Saturation))
        {
            OnPropertyChanged(nameof(Saturation));
            OnPropertyChanged(nameof(SaturationPercent));
        }

        if (!NearlyEqual(previousHsv.Value, _selectedHsv.Value))
        {
            OnPropertyChanged(nameof(Value));
            OnPropertyChanged(nameof(ValuePercent));
        }

        OnPropertyChanged(nameof(SelectedRgb));
        OnPropertyChanged(nameof(SelectedHsv));
        OnPropertyChanged(nameof(SelectedHex));
        OnPropertyChanged(nameof(SelectedColorBrush));
    }

    private void SetHexValidation(string message)
    {
        var hadError = HasHexError;
        HexValidationMessage = message;
        if (hadError != HasHexError)
        {
            OnPropertyChanged(nameof(HasHexError));
        }
    }

    private static bool NearlyEqual(double left, double right) => Math.Abs(left - right) < 0.000001d;

    private static SolidColorBrush CreateBrush(RgbColor rgb)
    {
        var brush = new SolidColorBrush(Color.FromRgb(rgb.Red, rgb.Green, rgb.Blue));
        brush.Freeze();
        return brush;
    }
}
