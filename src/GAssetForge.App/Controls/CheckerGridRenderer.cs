using System.Windows;
using System.Windows.Media;

namespace GAssetForge.App.Controls;

internal static class CheckerGridRenderer
{
    internal static void Draw(
        DrawingContext drawingContext,
        Rect imageBounds,
        int sourcePixelWidth,
        int sourcePixelHeight,
        int cellSize,
        int offsetX,
        int offsetY)
    {
        if (imageBounds.IsEmpty || sourcePixelWidth <= 0 || sourcePixelHeight <= 0)
        {
            return;
        }

        var safeCellSize = Math.Clamp(cellSize, 2, 512);
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
        var firstX = normalizedOffsetX;
        while (firstX > 0)
        {
            firstX -= safeCellSize;
        }

        for (var pixelX = firstX; pixelX <= sourcePixelWidth; pixelX += safeCellSize)
        {
            var x = imageBounds.Left + pixelX * scaleX;
            DrawGuideLine(
                drawingContext,
                shadowPen,
                guidePen,
                new Point(x, imageBounds.Top),
                new Point(x, imageBounds.Bottom));
        }

        var firstY = normalizedOffsetY;
        while (firstY > 0)
        {
            firstY -= safeCellSize;
        }

        for (var pixelY = firstY; pixelY <= sourcePixelHeight; pixelY += safeCellSize)
        {
            var y = imageBounds.Top + pixelY * scaleY;
            DrawGuideLine(
                drawingContext,
                shadowPen,
                guidePen,
                new Point(imageBounds.Left, y),
                new Point(imageBounds.Right, y));
        }

        var originX = imageBounds.Left + normalizedOffsetX * scaleX;
        var originY = imageBounds.Top + normalizedOffsetY * scaleY;
        var originPen = new Pen(new SolidColorBrush(Color.FromArgb(245, 0, 210, 255)), 2d);
        originPen.Freeze();
        drawingContext.DrawEllipse(null, originPen, new Point(originX, originY), 4d, 4d);
        drawingContext.Pop();
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

    private static int PositiveModulo(int value, int divisor)
    {
        var remainder = value % divisor;
        return remainder < 0 ? remainder + divisor : remainder;
    }
}
