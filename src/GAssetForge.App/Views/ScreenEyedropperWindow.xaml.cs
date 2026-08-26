using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using GAssetForge.App.Services;
using GAssetForge.Core.Imaging;

namespace GAssetForge.App.Views;

public partial class ScreenEyedropperWindow : Window
{
    private readonly IScreenColorSampler _screenColorSampler;
    private readonly PhysicalScreenBounds _virtualScreenBounds;
    private HwndSource? _windowSource;
    private bool _nativeBoundsReady;
    private bool _leftButtonPressed;
    private bool _rightButtonPressed;
    private bool _isCompleting;

    internal ScreenEyedropperWindow(IScreenColorSampler screenColorSampler)
    {
        ArgumentNullException.ThrowIfNull(screenColorSampler);
        _screenColorSampler = screenColorSampler;
        _virtualScreenBounds = screenColorSampler.GetVirtualScreenBounds();
        InitializeComponent();

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        PreviewMouseMove += OnPreviewMouseMove;
        PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
        PreviewMouseLeftButtonUp += OnPreviewMouseLeftButtonUp;
        PreviewMouseRightButtonDown += OnPreviewMouseRightButtonDown;
        PreviewMouseRightButtonUp += OnPreviewMouseRightButtonUp;
        PreviewKeyDown += OnPreviewKeyDown;
        Deactivated += OnDeactivated;
        LostMouseCapture += OnLostMouseCapture;
    }

    public RgbColor SelectedColor { get; private set; }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        _windowSource = HwndSource.FromHwnd(handle);
        _windowSource?.AddHook(OnWindowMessage);
        _nativeBoundsReady = ApplyNativeBounds();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _nativeBoundsReady = ApplyNativeBounds() || _nativeBoundsReady;
        if (!_nativeBoundsReady)
        {
            CancelSampling();
            return;
        }

        Activate();
        Focus();
        Keyboard.Focus(this);
        if (!Mouse.Capture(this, CaptureMode.SubTree))
        {
            CancelSampling();
            return;
        }

        UpdateSamplePreview();
    }

    private bool ApplyNativeBounds()
    {
        if (!_virtualScreenBounds.IsValid)
        {
            return false;
        }

        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        return DesktopWindowInterop.SetTopmostBounds(handle, _virtualScreenBounds);
    }

    private void OnPreviewMouseMove(object sender, MouseEventArgs e) => UpdateSamplePreview();

    private void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _leftButtonPressed = true;
        e.Handled = true;
        UpdateSamplePreview();
    }

    private void OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (!_leftButtonPressed)
        {
            return;
        }

        _leftButtonPressed = false;
        ConfirmCurrentPixel();
    }

    private void OnPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        _rightButtonPressed = true;
        e.Handled = true;
    }

    private void OnPreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (!_rightButtonPressed)
        {
            return;
        }

        _rightButtonPressed = false;
        CancelSampling();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        e.Handled = true;
        CancelSampling();
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (IsLoaded && !_isCompleting)
        {
            CancelSampling();
        }
    }

    private void OnLostMouseCapture(object sender, MouseEventArgs e)
    {
        if (IsLoaded && !_isCompleting)
        {
            CancelSampling();
        }
    }

    private IntPtr OnWindowMessage(
        IntPtr windowHandle,
        int message,
        IntPtr wordParameter,
        IntPtr longParameter,
        ref bool handled)
    {
        const int displayChangedMessage = 0x007E;
        if (message == displayChangedMessage && !_isCompleting)
        {
            Dispatcher.BeginInvoke(CancelSampling, DispatcherPriority.Send);
        }

        return IntPtr.Zero;
    }

    private void UpdateSamplePreview()
    {
        try
        {
            if (!_screenColorSampler.TryGetCursorPoint(out var point))
            {
                ShowSamplingError("无法读取当前鼠标位置，请移动鼠标后重试。");
                return;
            }

            PlaceStatusPanelNear(point);
            CoordinateText.Text = $"X {point.X}  Y {point.Y}";
            if (!_screenColorSampler.TrySample(point, out var color))
            {
                ShowSamplingError("当前位置暂时无法读取颜色。");
                return;
            }

            ColorSwatch.Background = CreateBrush(color);
            HexText.Text = color.Hex;
            RgbText.Text = $"RGB {color.Red}, {color.Green}, {color.Blue}";
            StatusText.Foreground = (Brush)FindResource("App.TextSecondaryBrush");
            StatusText.Text = "左键确认 · 右键或 Esc 取消";
        }
        catch (Exception exception)
        {
            ShowSamplingError(CreateSamplingExceptionMessage(exception));
        }
    }

    private void ConfirmCurrentPixel()
    {
        try
        {
            if (!_screenColorSampler.TryGetCursorPoint(out var point))
            {
                ShowSamplingError("无法读取当前鼠标位置，请重试。");
                return;
            }

            // Remove this layered overlay from desktop composition before the authoritative
            // sample. The live preview may be approximate, but the committed RGB is exact.
            Opacity = 0d;
            Dispatcher.Invoke(static () => { }, DispatcherPriority.Render);
            if (!DesktopWindowInterop.FlushDesktopComposition())
            {
                RestoreVisibleOverlay();
                ShowSamplingError("桌面合成尚未完成，未提交可能受覆盖层影响的颜色，请重试。");
                return;
            }

            if (!_screenColorSampler.TrySample(point, out var color))
            {
                RestoreVisibleOverlay();
                ShowSamplingError("取色失败，请移动到可见桌面区域后重试。");
                return;
            }

            SelectedColor = color;
            Complete(true);
        }
        catch (Exception exception)
        {
            RestoreVisibleOverlay();
            ShowSamplingError(CreateSamplingExceptionMessage(exception));
        }
    }

    private void ShowSamplingError(string message)
    {
        StatusText.Foreground = (Brush)FindResource("App.DangerBrush");
        StatusText.Text = message;
    }

    private void PlaceStatusPanelNear(PhysicalScreenPoint point)
    {
        if (ActualWidth <= 0d || ActualHeight <= 0d)
        {
            return;
        }

        const double cursorGap = 24d;
        const double edgeMargin = 12d;
        var panelWidth = StatusPanel.ActualWidth > 0d ? StatusPanel.ActualWidth : StatusPanel.Width;
        var panelHeight = StatusPanel.ActualHeight > 0d ? StatusPanel.ActualHeight : 148d;
        var cursorX = (point.X - _virtualScreenBounds.Left) *
                      (ActualWidth / _virtualScreenBounds.Width);
        var cursorY = (point.Y - _virtualScreenBounds.Top) *
                      (ActualHeight / _virtualScreenBounds.Height);

        var left = cursorX + cursorGap;
        if (left + panelWidth > ActualWidth - edgeMargin)
        {
            left = cursorX - cursorGap - panelWidth;
        }

        var top = cursorY + cursorGap;
        if (top + panelHeight > ActualHeight - edgeMargin)
        {
            top = cursorY - cursorGap - panelHeight;
        }

        left = Math.Clamp(left, edgeMargin, Math.Max(edgeMargin, ActualWidth - panelWidth - edgeMargin));
        top = Math.Clamp(top, edgeMargin, Math.Max(edgeMargin, ActualHeight - panelHeight - edgeMargin));
        StatusPanel.Margin = new Thickness(left, top, 0, 0);
    }

    private void RestoreVisibleOverlay()
    {
        Opacity = 1d;
        Dispatcher.Invoke(static () => { }, DispatcherPriority.Render);
        _ = DesktopWindowInterop.FlushDesktopComposition();
    }

    private static string CreateSamplingExceptionMessage(Exception exception)
    {
        var detail = exception.Message.Trim();
        if (detail.Length > 120)
        {
            detail = detail[..120] + "…";
        }

        return string.IsNullOrEmpty(detail)
            ? "屏幕取色服务发生异常，请取消后重试。"
            : $"屏幕取色服务异常：{detail}";
    }

    private void CancelSampling() => Complete(false);

    private void Complete(bool accepted)
    {
        if (_isCompleting)
        {
            return;
        }

        _isCompleting = true;
        if (ReferenceEquals(Mouse.Captured, this) || IsMouseCaptureWithin)
        {
            Mouse.Capture(null);
        }

        DialogResult = accepted;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _isCompleting = true;
        if (ReferenceEquals(Mouse.Captured, this) || IsMouseCaptureWithin)
        {
            Mouse.Capture(null);
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _windowSource?.RemoveHook(OnWindowMessage);
        _windowSource = null;
        base.OnClosed(e);
    }

    private static SolidColorBrush CreateBrush(RgbColor color)
    {
        var brush = new SolidColorBrush(Color.FromRgb(color.Red, color.Green, color.Blue));
        brush.Freeze();
        return brush;
    }
}

internal static class DesktopWindowInterop
{
    private static readonly IntPtr TopmostWindow = new(-1);
    private const uint SwpShowWindow = 0x0040;

    public static bool SetTopmostBounds(IntPtr windowHandle, PhysicalScreenBounds bounds)
    {
        return SetWindowPos(
            windowHandle,
            TopmostWindow,
            bounds.Left,
            bounds.Top,
            bounds.Width,
            bounds.Height,
            SwpShowWindow);
    }

    public static bool FlushDesktopComposition()
    {
        try
        {
            return DwmFlush() == 0;
        }
        catch (DllNotFoundException)
        {
            // Desktop composition is always present on supported Windows versions, but a
            // missing DWM must not strand the modal eyedropper overlay.
        }
        catch (EntryPointNotFoundException)
        {
        }

        return false;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr windowHandle,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();
}
