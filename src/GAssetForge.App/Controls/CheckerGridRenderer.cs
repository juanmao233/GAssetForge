using System.Windows;
using System.Windows.Media;

namespace GAssetForge.App.Controls;

internal static class CheckerGridRenderer
{
    private const int MaximumRenderedLinesPerAxis = 2048;

    internal static void Draw(
        DrawingContext drawingContext,
        Rect imageBounds,
        int sourcePixelWidth,
        int sourcePixelHeight,
        double cellSize,
        double offsetX,
        double offsetY)
    {
        if (imageBounds.IsEmpty || sourcePixelWidth <= 0 || sourcePixelHeight <= 0 ||
            !double.IsFinite(cellSize) || cellSize <= 0d ||
            !double.IsFinite(offsetX) || !double.IsFinite(offsetY))
        {
            return;
        }

        var safeCellSize = Math.Clamp(cellSize, 0.25d, 4096d);
        var scaleX = imageBounds.Width / sourcePixelWidth;
        var scaleY = imageBounds.Height / sourcePixelHeight;
        if (!double.IsFinite(scaleX) || !double.IsFinite(scaleY) || scaleX <= 0d || scaleY <= 0d)
        {
            return;
        }

        drawingContext.PushClip(new RectangleGeometry(imageBounds));
        var shadowPen = new Pen(new SolidColorBrush(Color.FromArgb(155, 0, 0, 0)), 2d)
        {
            DashStyle = DashStyles.Dash
        };
        var guidePen = new Pen(new SolidColorBrush(Color.FromArgb(230, 255, 255, 255)), 1d)
        {
            DashStyle = DashStyles.Dash
        };
        shadowPen.Freeze();
        guidePen.Freeze();

        var normalizedOffsetX = PositiveModulo(offsetX, safeCellSize);
        var normalizedOffsetY = PositiveModulo(offsetY, safeCellSize);
        DrawAxisGuides(
            drawingContext,
            shadowPen,
            guidePen,
            imageBounds,
            sourcePixelWidth,
            safeCellSize,
            normalizedOffsetX,
            scaleX,
            vertical: true);
        DrawAxisGuides(
            drawingContext,
            shadowPen,
            guidePen,
            imageBounds,
            sourcePixelHeight,
            safeCellSize,
            normalizedOffsetY,
            scaleY,
            vertical: false);

        var originX = imageBounds.Left + normalizedOffsetX * scaleX;
        var originY = imageBounds.Top + normalizedOffsetY * scaleY;
        var originPen = new Pen(new SolidColorBrush(Color.FromArgb(245, 0, 210, 255)), 2d);
        originPen.Freeze();
        drawingContext.DrawEllipse(null, originPen, new Point(originX, originY), 4d, 4d);
        drawingContext.Pop();
    }

    private static void DrawAxisGuides(
        DrawingContext context,
        Pen shadowPen,
        Pen guidePen,
        Rect imageBounds,
        int sourceExtent,
        double cellSize,
        double normalizedOffset,
        double viewportScale,
        bool vertical)
    {
        var firstIndex = (long)Math.Ceiling(-normalizedOffset / cellSize);
        var lastIndex = (long)Math.Floor((sourceExtent - normalizedOffset) / cellSize);
        if (lastIndex < firstIndex)
        {
            return;
        }

        var lineCount = lastIndex - firstIndex + 1L;
        var indexStride = Math.Max(
            1L,
            (long)Math.Ceiling(lineCount / (double)MaximumRenderedLinesPerAxis));
        for (var index = firstIndex; index <= lastIndex; index += indexStride)
        {
            var sourceCoordinate = normalizedOffset + index * cellSize;
            var viewportCoordinate = vertical
                ? imageBounds.Left + sourceCoordinate * viewportScale
                : imageBounds.Top + sourceCoordinate * viewportScale;
            DrawGuideLine(
                context,
                shadowPen,
                guidePen,
                vertical
                    ? new Point(viewportCoordinate, imageBounds.Top)
                    : new Point(imageBounds.Left, viewportCoordinate),
                vertical
                    ? new Point(viewportCoordinate, imageBounds.Bottom)
                    : new Point(imageBounds.Right, viewportCoordinate));

            if (lastIndex - index < indexStride)
            {
                break;
            }
        }
    }

    private static void DrawGuideLine(
        DrawingContext context,
        Pen shadowPen,
        Pen guidePen,
        Point start,
        Point end)
    {
        context.DrawLine(shadowPen, start, end);
        context.DrawLine(guidePen, start, end);
    }

    private static double PositiveModulo(double value, double divisor)
    {
        var remainder = value % divisor;
        return remainder < 0 ? remainder + divisor : remainder;
    }
}
