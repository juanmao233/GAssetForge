using GAssetForge.App.Infrastructure;
using GAssetForge.Core.Generation;
using GAssetForge.Core.IconDesign;
using System.Windows.Input;

namespace GAssetForge.App.ViewModels;

public sealed record IconDesignModeOption(
    IconDesignMode Mode,
    string DisplayName,
    string Description);

public sealed record IconDesignSettingsSnapshot(
    string Mode,
    int ImageWidth,
    int ImageHeight,
    double CameraAzimuthDegrees,
    double CameraElevationDegrees,
    double CameraRollDegrees,
    double CameraDistance,
    double FocalLengthMillimeters);

public sealed class IconDesignSettingsChangedEventArgs(
    IconDesignSettingsSnapshot settings) : EventArgs
{
    public IconDesignSettingsSnapshot Settings { get; } = settings;
}

public sealed class IconDesignViewModel : ObservableObject
{
    public const int DefaultImageWidth = 512;
    public const int DefaultImageHeight = 512;
    public const double DefaultCameraAzimuthDegrees = 45d;
    public const double DefaultCameraElevationDegrees = 30d;
    public const double DefaultCameraRollDegrees = 0d;
    public const double DefaultCameraDistance = 4.5d;
    public const double DefaultFocalLengthMillimeters = 50d;
    public const double MaximumEditorCameraDistance = 20d;

    private static readonly IReadOnlyList<IconDesignModeOption> SupportedModes =
    [
        new(
            IconDesignMode.SpecifiedAngle,
            "指定角度生成",
            "通过相机方位、仰角、镜头滚转、焦距和物距明确约束图标主体的透视。")
    ];

    private readonly IIconPerspectiveProjectionService _projectionService;
    private IconDesignModeOption _selectedMode;
    private int _imageWidth = DefaultImageWidth;
    private int _imageHeight = DefaultImageHeight;
    private double _cameraAzimuthDegrees = DefaultCameraAzimuthDegrees;
    private double _cameraElevationDegrees = DefaultCameraElevationDegrees;
    private double _cameraRollDegrees = DefaultCameraRollDegrees;
    private double _cameraDistance = DefaultCameraDistance;
    private double _focalLengthMillimeters = DefaultFocalLengthMillimeters;
    private IconProjectionSnapshot _projection;
    private bool _suppressRefresh;
    private bool _suppressSettingsChanged;

    public IconDesignViewModel(
        IIconPerspectiveProjectionService? projectionService = null,
        IconDesignSettingsSnapshot? initialSettings = null)
    {
        _projectionService = projectionService ?? new IconPerspectiveProjectionService();
        _selectedMode = SupportedModes[0];
        _projection = _projectionService.Calculate(CreateProjectionParameters());

        ResetCommand = new RelayCommand(Reset);
        ApplyFrontPresetCommand = new RelayCommand(ApplyFrontPreset);
        ApplyThreeQuarterPresetCommand = new RelayCommand(ApplyThreeQuarterPreset);
        ApplyTopThreeQuarterPresetCommand = new RelayCommand(ApplyTopThreeQuarterPreset);

        if (initialSettings is not null)
        {
            ApplySettings(initialSettings, notifySettingsChanged: false);
        }
    }

    public event EventHandler<IconDesignSettingsChangedEventArgs>? SettingsChanged;

    public string DocumentTitle => "Icon 图标制作";

    public IReadOnlyList<IconDesignModeOption> ModeOptions => SupportedModes;

    public IconDesignModeOption SelectedMode
    {
        get => _selectedMode;
        set
        {
            var normalized = value is null
                ? SupportedModes[0]
                : SupportedModes.FirstOrDefault(option => option.Mode == value.Mode) ?? SupportedModes[0];
            if (!SetProperty(ref _selectedMode, normalized))
            {
                return;
            }

            OnPropertyChanged(nameof(ModeDescription));
            RefreshProjectionAndText();
            NotifySettingsChanged();
        }
    }

    public string ModeDescription => SelectedMode.Description;

    public int ImageWidth
    {
        get => _imageWidth;
        set
        {
            var normalized = Math.Clamp(
                value,
                IconPerspectiveProjectionService.MinimumImageDimension,
                IconPerspectiveProjectionService.MaximumImageDimension);
            if (SetProperty(ref _imageWidth, normalized))
            {
                RefreshProjectionAndText();
                NotifySettingsChanged();
            }
        }
    }

    public int ImageHeight
    {
        get => _imageHeight;
        set
        {
            var normalized = Math.Clamp(
                value,
                IconPerspectiveProjectionService.MinimumImageDimension,
                IconPerspectiveProjectionService.MaximumImageDimension);
            if (SetProperty(ref _imageHeight, normalized))
            {
                RefreshProjectionAndText();
                NotifySettingsChanged();
            }
        }
    }

    public double CameraAzimuthDegrees
    {
        get => _cameraAzimuthDegrees;
        set
        {
            var normalized = CoerceFinite(value, DefaultCameraAzimuthDegrees, -180d, 180d);
            if (SetProperty(ref _cameraAzimuthDegrees, normalized))
            {
                RefreshProjectionAndText();
                NotifySettingsChanged();
            }
        }
    }

    public double CameraElevationDegrees
    {
        get => _cameraElevationDegrees;
        set
        {
            var normalized = CoerceFinite(
                value,
                DefaultCameraElevationDegrees,
                IconPerspectiveProjectionService.MinimumElevationDegrees,
                IconPerspectiveProjectionService.MaximumElevationDegrees);
            if (SetProperty(ref _cameraElevationDegrees, normalized))
            {
                RefreshProjectionAndText();
                NotifySettingsChanged();
            }
        }
    }

    public double CameraRollDegrees
    {
        get => _cameraRollDegrees;
        set
        {
            var normalized = CoerceFinite(value, DefaultCameraRollDegrees, -180d, 180d);
            if (SetProperty(ref _cameraRollDegrees, normalized))
            {
                RefreshProjectionAndText();
                NotifySettingsChanged();
            }
        }
    }

    public double CameraDistance
    {
        get => _cameraDistance;
        set
        {
            var normalized = CoerceFinite(
                value,
                DefaultCameraDistance,
                IconPerspectiveProjectionService.MinimumCameraDistance,
                MaximumEditorCameraDistance);
            if (SetProperty(ref _cameraDistance, normalized))
            {
                RefreshProjectionAndText();
                NotifySettingsChanged();
            }
        }
    }

    public double FocalLengthMillimeters
    {
        get => _focalLengthMillimeters;
        set
        {
            var normalized = CoerceFinite(
                value,
                DefaultFocalLengthMillimeters,
                IconPerspectiveProjectionService.MinimumFocalLengthMillimeters,
                IconPerspectiveProjectionService.MaximumFocalLengthMillimeters);
            if (SetProperty(ref _focalLengthMillimeters, normalized))
            {
                RefreshProjectionAndText();
                NotifySettingsChanged();
            }
        }
    }

    public IconProjectionSnapshot Projection
    {
        get => _projection;
        private set => SetProperty(ref _projection, value);
    }

    public string ImageSizeText => $"{ImageWidth} × {ImageHeight} px";

    public string CameraPositionText =>
        $"X {Projection.CameraPosition.X:0.00} · Y {Projection.CameraPosition.Y:0.00} · Z {Projection.CameraPosition.Z:0.00}";

    public string FieldOfViewText =>
        $"垂直 {Projection.VerticalFieldOfViewDegrees:0.0}° · 水平 {Projection.HorizontalFieldOfViewDegrees:0.0}°";

    public string FramingText => Projection.NormalizedBounds.IsFinite
        ? $"主体画面占比约 {Projection.NormalizedBounds.WidthPercentOfFrame:0.#}% × {Projection.NormalizedBounds.HeightPercentOfFrame:0.#}%"
        : "当前相机参数无法形成有效投影";

    public string FramingStateText => Projection.IsFullyInFrame ? "主体完整位于镜头内" : "主体超出镜头边界，请减小焦距或增大物距";

    public string AngleDescription =>
        $"方位角 {CameraAzimuthDegrees:0.#}° · 仰角 {CameraElevationDegrees:0.#}° · 镜头滚转 {CameraRollDegrees:0.#}°";

    public string GenerationDescription =>
        IconDesignConstraintPackageBuilder.BuildGenerationAngleDescription(Projection);

    public string StatusText =>
        $"指定角度生成 · {ImageSizeText} · {AngleDescription} · {FocalLengthMillimeters:0.#} mm · {FramingStateText}";

    public ICommand ResetCommand { get; }

    public ICommand ApplyFrontPresetCommand { get; }

    public ICommand ApplyThreeQuarterPresetCommand { get; }

    public ICommand ApplyTopThreeQuarterPresetCommand { get; }

    public IconDesignSettingsSnapshot CaptureSettings() =>
        new(
            SelectedMode.Mode.ToString(),
            ImageWidth,
            ImageHeight,
            CameraAzimuthDegrees,
            CameraElevationDegrees,
            CameraRollDegrees,
            CameraDistance,
            FocalLengthMillimeters);

    public GenerationConstraintPackage BuildGenerationConstraints(
        string contentPrompt = "单个游戏美术 Icon",
        string? stylePrompt = null,
        string? negativePrompt = null,
        int outputCount = 1,
        GenerationBackgroundRemovalSpec? backgroundRemoval = null) =>
        IconDesignConstraintPackageBuilder.Build(new IconDesignGenerationConstraintInput
        {
            Mode = SelectedMode.Mode,
            ImageWidth = ImageWidth,
            ImageHeight = ImageHeight,
            CameraAzimuthDegrees = CameraAzimuthDegrees,
            CameraElevationDegrees = CameraElevationDegrees,
            CameraRollDegrees = CameraRollDegrees,
            CameraDistance = CameraDistance,
            FocalLengthMillimeters = FocalLengthMillimeters,
            ContentPrompt = contentPrompt,
            StylePrompt = stylePrompt,
            NegativePrompt = negativePrompt,
            OutputCount = outputCount,
            BackgroundRemoval = backgroundRemoval
        });

    public void ApplySettings(
        IconDesignSettingsSnapshot settings,
        bool notifySettingsChanged = true)
    {
        ArgumentNullException.ThrowIfNull(settings);
        RunBatchUpdate(
            () =>
            {
                SelectedMode = SupportedModes.FirstOrDefault(option =>
                                   string.Equals(option.Mode.ToString(), settings.Mode, StringComparison.OrdinalIgnoreCase))
                               ?? SupportedModes[0];
                ImageWidth = settings.ImageWidth;
                ImageHeight = settings.ImageHeight;
                CameraAzimuthDegrees = settings.CameraAzimuthDegrees;
                CameraElevationDegrees = settings.CameraElevationDegrees;
                CameraRollDegrees = settings.CameraRollDegrees;
                CameraDistance = settings.CameraDistance;
                FocalLengthMillimeters = settings.FocalLengthMillimeters;
            },
            notifySettingsChanged);
    }

    private void Reset() =>
        ApplyPreset(
            DefaultImageWidth,
            DefaultImageHeight,
            DefaultCameraAzimuthDegrees,
            DefaultCameraElevationDegrees,
            DefaultCameraRollDegrees,
            DefaultCameraDistance,
            DefaultFocalLengthMillimeters);

    private void ApplyFrontPreset() =>
        ApplyPreset(ImageWidth, ImageHeight, 0d, 0d, 0d, 4.5d, 50d);

    private void ApplyThreeQuarterPreset() =>
        ApplyPreset(ImageWidth, ImageHeight, 45d, 30d, 0d, 4.5d, 50d);

    private void ApplyTopThreeQuarterPreset() =>
        ApplyPreset(ImageWidth, ImageHeight, 45d, 55d, 0d, 5d, 50d);

    private void ApplyPreset(
        int width,
        int height,
        double azimuth,
        double elevation,
        double roll,
        double distance,
        double focalLength)
    {
        RunBatchUpdate(
            () =>
            {
                ImageWidth = width;
                ImageHeight = height;
                CameraAzimuthDegrees = azimuth;
                CameraElevationDegrees = elevation;
                CameraRollDegrees = roll;
                CameraDistance = distance;
                FocalLengthMillimeters = focalLength;
            },
            notifySettingsChanged: true);
    }

    private void RunBatchUpdate(Action update, bool notifySettingsChanged)
    {
        _suppressRefresh = true;
        _suppressSettingsChanged = true;
        try
        {
            update();
        }
        finally
        {
            _suppressRefresh = false;
            _suppressSettingsChanged = false;
        }

        RefreshProjectionAndText();
        if (notifySettingsChanged)
        {
            NotifySettingsChanged();
        }
    }

    private void RefreshProjectionAndText()
    {
        if (_suppressRefresh)
        {
            return;
        }

        Projection = _projectionService.Calculate(CreateProjectionParameters());
        OnPropertyChanged(nameof(ImageSizeText));
        OnPropertyChanged(nameof(CameraPositionText));
        OnPropertyChanged(nameof(FieldOfViewText));
        OnPropertyChanged(nameof(FramingText));
        OnPropertyChanged(nameof(FramingStateText));
        OnPropertyChanged(nameof(AngleDescription));
        OnPropertyChanged(nameof(GenerationDescription));
        OnPropertyChanged(nameof(StatusText));
    }

    private IconProjectionParameters CreateProjectionParameters() =>
        new()
        {
            Mode = SelectedMode.Mode,
            ImageWidth = ImageWidth,
            ImageHeight = ImageHeight,
            CameraAzimuthDegrees = CameraAzimuthDegrees,
            CameraElevationDegrees = CameraElevationDegrees,
            CameraRollDegrees = CameraRollDegrees,
            CameraDistance = CameraDistance,
            FocalLengthMillimeters = FocalLengthMillimeters
        };

    private void NotifySettingsChanged()
    {
        if (!_suppressSettingsChanged)
        {
            SettingsChanged?.Invoke(this, new IconDesignSettingsChangedEventArgs(CaptureSettings()));
        }
    }

    private static double CoerceFinite(double value, double fallback, double minimum, double maximum) =>
        double.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : fallback;
}
