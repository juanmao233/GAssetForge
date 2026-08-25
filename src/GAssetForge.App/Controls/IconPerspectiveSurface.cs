using GAssetForge.Core.IconDesign;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace GAssetForge.App.Controls;

public enum IconPerspectiveSurfaceViewKind
{
    SpatialSetup,
    CameraFrame
}

public sealed class IconPerspectiveSurface : FrameworkElement
{
    public static readonly DependencyProperty ProjectionProperty = DependencyProperty.Register(
        nameof(Projection),
        typeof(IconProjectionSnapshot),
        typeof(IconPerspectiveSurface),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ViewKindProperty = DependencyProperty.Register(
        nameof(ViewKind),
        typeof(IconPerspectiveSurfaceViewKind),
        typeof(IconPerspectiveSurface),
        new FrameworkPropertyMetadata(
            IconPerspectiveSurfaceViewKind.SpatialSetup,
            FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BackgroundProperty = RegisterBrush(
        nameof(Background),
        Brushes.Transparent);

    public static readonly DependencyProperty GridBrushProperty = RegisterBrush(
        nameof(GridBrush),
        Brushes.DimGray);

    public static readonly DependencyProperty SubjectBrushProperty = RegisterBrush(
        nameof(SubjectBrush),
        Brushes.SteelBlue);

    public static readonly DependencyProperty SubjectEdgeBrushProperty = RegisterBrush(
        nameof(SubjectEdgeBrush),
        Brushes.WhiteSmoke);

    public static readonly DependencyProperty CameraBrushProperty = RegisterBrush(
        nameof(CameraBrush),
        Brushes.Goldenrod);

    public static readonly DependencyProperty FrustumBrushProperty = RegisterBrush(
        nameof(FrustumBrush),
        Brushes.DeepSkyBlue);

    public static readonly DependencyProperty TextBrushProperty = RegisterBrush(
        nameof(TextBrush),
        Brushes.LightGray);

    public static readonly DependencyProperty WarningBrushProperty = RegisterBrush(
        nameof(WarningBrush),
        Brushes.OrangeRed);

    private static readonly IconVector3 ObserverPosition = new(5d, 4d, 7d);

    public IconPerspectiveSurface()
    {
        ClipToBounds = true;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
    }

    public IconProjectionSnapshot? Projection
    {
        get => (IconProjectionSnapshot?)GetValue(ProjectionProperty);
        set => SetValue(ProjectionProperty, value);
    }

    public IconPerspectiveSurfaceViewKind ViewKind
    {
        get => (IconPerspectiveSurfaceViewKind)GetValue(ViewKindProperty);
        set => SetValue(ViewKindProperty, value);
    }

    public Brush Background
    {
        get => (Brush)GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    public Brush GridBrush
    {
        get => (Brush)GetValue(GridBrushProperty);
        set => SetValue(GridBrushProperty, value);
    }

    public Brush SubjectBrush
    {
        get => (Brush)GetValue(SubjectBrushProperty);
        set => SetValue(SubjectBrushProperty, value);
    }

    public Brush SubjectEdgeBrush
    {
        get => (Brush)GetValue(SubjectEdgeBrushProperty);
        set => SetValue(SubjectEdgeBrushProperty, value);
    }

    public Brush CameraBrush
    {
        get => (Brush)GetValue(CameraBrushProperty);
        set => SetValue(CameraBrushProperty, value);
    }

    public Brush FrustumBrush
    {
        get => (Brush)GetValue(FrustumBrushProperty);
        set => SetValue(FrustumBrushProperty, value);
    }

    public Brush TextBrush
    {
        get => (Brush)GetValue(TextBrushProperty);
        set => SetValue(TextBrushProperty, value);
    }

    public Brush WarningBrush
    {
        get => (Brush)GetValue(WarningBrushProperty);
        set => SetValue(WarningBrushProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var viewport = new Rect(0d, 0d, Math.Max(0d, ActualWidth), Math.Max(0d, ActualHeight));
        drawingContext.DrawRectangle(Background, null, viewport);
        if (viewport.Width < 80d || viewport.Height < 80d)
        {
            return;
        }

        if (Projection is null)
        {
            DrawCenteredText(drawingContext, "等待相机参数", viewport, TextBrush, 12d);
            return;
        }

        if (ViewKind == IconPerspectiveSurfaceViewKind.CameraFrame)
        {
            DrawCameraFrame(drawingContext, viewport, Projection);
        }
        else
        {
            DrawSpatialSetup(drawingContext, viewport, Projection);
        }
    }

    private void DrawSpatialSetup(
        DrawingContext drawingContext,
        Rect viewport,
        IconProjectionSnapshot snapshot)
    {
        var groundY = -snapshot.Parameters.SubjectSize / 2d;
        var gridRadius = Math.Clamp(snapshot.Parameters.CameraDistance * 0.55d, 2d, 7d);
        var frustumDepth = Math.Min(snapshot.Parameters.CameraDistance * 0.55d, 4d);
        var halfHeight = frustumDepth * Math.Tan(
            DegreesToRadians(snapshot.VerticalFieldOfViewDegrees) / 2d);
        var halfWidth = frustumDepth * Math.Tan(
            DegreesToRadians(snapshot.HorizontalFieldOfViewDegrees) / 2d);
        var frustumCenter = snapshot.CameraPosition + snapshot.CameraForward * frustumDepth;
        var frustumCorners = new[]
        {
            frustumCenter - snapshot.CameraRight * halfWidth - snapshot.CameraUp * halfHeight,
            frustumCenter + snapshot.CameraRight * halfWidth - snapshot.CameraUp * halfHeight,
            frustumCenter + snapshot.CameraRight * halfWidth + snapshot.CameraUp * halfHeight,
            frustumCenter - snapshot.CameraRight * halfWidth + snapshot.CameraUp * halfHeight
        };

        var cameraBodyCenter = snapshot.CameraPosition - snapshot.CameraForward * 0.28d;
        var cameraBodyCorners = new[]
        {
            cameraBodyCenter - snapshot.CameraRight * 0.32d - snapshot.CameraUp * 0.22d,
            cameraBodyCenter + snapshot.CameraRight * 0.32d - snapshot.CameraUp * 0.22d,
            cameraBodyCenter + snapshot.CameraRight * 0.32d + snapshot.CameraUp * 0.22d,
            cameraBodyCenter - snapshot.CameraRight * 0.32d + snapshot.CameraUp * 0.22d
        };
        var cameraLens = snapshot.CameraPosition + snapshot.CameraForward * 0.18d;
        var extentPoints = new List<IconVector3>(snapshot.CubeVertices)
        {
            snapshot.CameraPosition,
            cameraLens,
            new(-gridRadius, groundY, -gridRadius),
            new(gridRadius, groundY, -gridRadius),
            new(gridRadius, groundY, gridRadius),
            new(-gridRadius, groundY, gridRadius)
        };
        extentPoints.AddRange(frustumCorners);
        extentPoints.AddRange(cameraBodyCorners);

        var transform = CreateSceneTransform(viewport, extentPoints, 30d);
        var gridPen = CreatePen(GridBrush, 1d);
        var gridStep = gridRadius > 5d ? 2d : 1d;
        var firstGrid = Math.Ceiling(-gridRadius / gridStep) * gridStep;
        for (var coordinate = firstGrid; coordinate <= gridRadius + 0.0001d; coordinate += gridStep)
        {
            drawingContext.DrawLine(
                gridPen,
                transform(new IconVector3(coordinate, groundY, -gridRadius)),
                transform(new IconVector3(coordinate, groundY, gridRadius)));
            drawingContext.DrawLine(
                gridPen,
                transform(new IconVector3(-gridRadius, groundY, coordinate)),
                transform(new IconVector3(gridRadius, groundY, coordinate)));
        }

        DrawAxes(drawingContext, transform, snapshot.Parameters.SubjectSize);

        var frustumPen = CreatePen(FrustumBrush, 1.25d, [5d, 4d]);
        var frustumFill = CreateOpacityBrush(FrustumBrush, 0.09d);
        var frustumGeometry = CreateClosedGeometry(frustumCorners.Select(transform));
        drawingContext.DrawGeometry(frustumFill, frustumPen, frustumGeometry);
        foreach (var corner in frustumCorners)
        {
            drawingContext.DrawLine(frustumPen, transform(cameraLens), transform(corner));
        }

        drawingContext.DrawLine(
            CreatePen(CameraBrush, 1.4d, [7d, 4d]),
            transform(cameraLens),
            transform(snapshot.SubjectCenter));

        DrawSceneCube(drawingContext, snapshot, transform);
        DrawCamera(drawingContext, transform, cameraBodyCorners, cameraLens, snapshot.CameraPosition);

        DrawLabel(
            drawingContext,
            "相机",
            transform(snapshot.CameraPosition) + new Vector(8d, -17d),
            CameraBrush,
            11d);
        DrawLabel(
            drawingContext,
            "物体中心",
            transform(snapshot.SubjectCenter) + new Vector(8d, 7d),
            TextBrush,
            10d);
    }

    private void DrawCameraFrame(
        DrawingContext drawingContext,
        Rect viewport,
        IconProjectionSnapshot snapshot)
    {
        var frame = FitAspectRatio(
            new Rect(
                viewport.X + 28d,
                viewport.Y + 26d,
                Math.Max(1d, viewport.Width - 56d),
                Math.Max(1d, viewport.Height - 52d)),
            snapshot.Parameters.ImageWidth / (double)snapshot.Parameters.ImageHeight);
        var frameFill = CreateOpacityBrush(Background, 0.55d);
        var frameBorderBrush = snapshot.IsFullyInFrame ? FrustumBrush : WarningBrush;
        drawingContext.DrawRectangle(
            frameFill,
            CreatePen(frameBorderBrush, snapshot.IsFullyInFrame ? 1.25d : 2d),
            frame);

        var guidePen = CreatePen(GridBrush, 0.8d, [3d, 4d]);
        drawingContext.DrawLine(
            guidePen,
            new Point(frame.Left + frame.Width / 3d, frame.Top),
            new Point(frame.Left + frame.Width / 3d, frame.Bottom));
        drawingContext.DrawLine(
            guidePen,
            new Point(frame.Left + frame.Width * 2d / 3d, frame.Top),
            new Point(frame.Left + frame.Width * 2d / 3d, frame.Bottom));
        drawingContext.DrawLine(
            guidePen,
            new Point(frame.Left, frame.Top + frame.Height / 3d),
            new Point(frame.Right, frame.Top + frame.Height / 3d));
        drawingContext.DrawLine(
            guidePen,
            new Point(frame.Left, frame.Top + frame.Height * 2d / 3d),
            new Point(frame.Right, frame.Top + frame.Height * 2d / 3d));

        drawingContext.PushClip(new RectangleGeometry(frame));
        var visibleFaces = snapshot.ProjectedFaces
            .Where(face => face.IsCameraFacing)
            .OrderByDescending(face => face.AverageCameraDepth)
            .ToArray();
        foreach (var face in visibleFaces)
        {
            var geometry = CreateClosedGeometry(
                face.VertexIndices.Select(index => MapImagePoint(
                    snapshot.ProjectedVertices[index].ImagePixelPoint,
                    frame,
                    snapshot.Parameters.ImageWidth,
                    snapshot.Parameters.ImageHeight)));
            var opacity = face.WorldNormal.Y > 0.5d
                ? 0.88d
                : face.WorldNormal.Z > 0.5d || face.WorldNormal.X > 0.5d
                    ? 0.72d
                    : 0.58d;
            drawingContext.DrawGeometry(
                CreateOpacityBrush(SubjectBrush, opacity),
                null,
                geometry);
        }

        var edgePen = CreatePen(SubjectEdgeBrush, 1.65d);
        foreach (var edge in snapshot.CubeEdges)
        {
            var start = snapshot.ProjectedVertices[edge.StartVertexIndex];
            var end = snapshot.ProjectedVertices[edge.EndVertexIndex];
            if (!start.ImagePixelPoint.IsFinite || !end.ImagePixelPoint.IsFinite)
            {
                continue;
            }

            drawingContext.DrawLine(
                edgePen,
                MapImagePoint(
                    start.ImagePixelPoint,
                    frame,
                    snapshot.Parameters.ImageWidth,
                    snapshot.Parameters.ImageHeight),
                MapImagePoint(
                    end.ImagePixelPoint,
                    frame,
                    snapshot.Parameters.ImageWidth,
                    snapshot.Parameters.ImageHeight));
        }

        drawingContext.Pop();

        var center = new Point(frame.Left + frame.Width / 2d, frame.Top + frame.Height / 2d);
        var centerPen = CreatePen(FrustumBrush, 1d);
        drawingContext.DrawLine(centerPen, center + new Vector(-7d, 0d), center + new Vector(7d, 0d));
        drawingContext.DrawLine(centerPen, center + new Vector(0d, -7d), center + new Vector(0d, 7d));

        DrawLabel(
            drawingContext,
            $"{snapshot.Parameters.ImageWidth} × {snapshot.Parameters.ImageHeight} px",
            new Point(frame.Left, Math.Max(3d, frame.Top - 20d)),
            TextBrush,
            10d);
        if (!snapshot.IsFullyInFrame)
        {
            DrawLabel(
                drawingContext,
                "主体已超出画幅",
                new Point(frame.Right - 92d, Math.Max(3d, frame.Top - 20d)),
                WarningBrush,
                10d);
        }
    }

    private void DrawSceneCube(
        DrawingContext drawingContext,
        IconProjectionSnapshot snapshot,
        Func<IconVector3, Point> transform)
    {
        var observerDirection = ObserverPosition.Normalize();
        var visibleFaces = snapshot.CubeFaces
            .Where(face => IconVector3.Dot(face.Normal, observerDirection) > 0d)
            .OrderBy(face => face.VertexIndices
                .Average(index => IconVector3.Dot(snapshot.CubeVertices[index], observerDirection)))
            .ToArray();
        foreach (var face in visibleFaces)
        {
            drawingContext.DrawGeometry(
                CreateOpacityBrush(SubjectBrush, face.Normal.Y > 0.5d ? 0.78d : 0.58d),
                null,
                CreateClosedGeometry(face.VertexIndices.Select(index => transform(snapshot.CubeVertices[index]))));
        }

        var edgePen = CreatePen(SubjectEdgeBrush, 1.5d);
        foreach (var edge in snapshot.CubeEdges)
        {
            drawingContext.DrawLine(
                edgePen,
                transform(snapshot.CubeVertices[edge.StartVertexIndex]),
                transform(snapshot.CubeVertices[edge.EndVertexIndex]));
        }
    }

    private void DrawCamera(
        DrawingContext drawingContext,
        Func<IconVector3, Point> transform,
        IReadOnlyList<IconVector3> bodyCorners,
        IconVector3 lens,
        IconVector3 cameraPosition)
    {
        var cameraPen = CreatePen(CameraBrush, 1.6d);
        drawingContext.DrawGeometry(
            CreateOpacityBrush(CameraBrush, 0.22d),
            cameraPen,
            CreateClosedGeometry(bodyCorners.Select(transform)));
        var lensPoint = transform(lens);
        drawingContext.DrawLine(cameraPen, transform(bodyCorners[0]), lensPoint);
        drawingContext.DrawLine(cameraPen, transform(bodyCorners[1]), lensPoint);
        drawingContext.DrawEllipse(CameraBrush, null, transform(cameraPosition), 3.25d, 3.25d);
    }

    private void DrawAxes(
        DrawingContext drawingContext,
        Func<IconVector3, Point> transform,
        double subjectSize)
    {
        var origin = new IconVector3(0d, -subjectSize / 2d, 0d);
        var axisLength = Math.Max(1.2d, subjectSize * 1.4d);
        var xEnd = origin + new IconVector3(axisLength, 0d, 0d);
        var yEnd = origin + new IconVector3(0d, axisLength, 0d);
        var zEnd = origin + new IconVector3(0d, 0d, axisLength);
        drawingContext.DrawLine(CreatePen(WarningBrush, 1.4d), transform(origin), transform(xEnd));
        drawingContext.DrawLine(CreatePen(FrustumBrush, 1.4d), transform(origin), transform(yEnd));
        drawingContext.DrawLine(CreatePen(SubjectBrush, 1.4d), transform(origin), transform(zEnd));
        DrawLabel(drawingContext, "X", transform(xEnd) + new Vector(4d, -6d), WarningBrush, 9d);
        DrawLabel(drawingContext, "Y", transform(yEnd) + new Vector(4d, -6d), FrustumBrush, 9d);
        DrawLabel(drawingContext, "Z", transform(zEnd) + new Vector(4d, -6d), SubjectBrush, 9d);
    }

    private static Func<IconVector3, Point> CreateSceneTransform(
        Rect viewport,
        IReadOnlyCollection<IconVector3> points,
        double padding)
    {
        var projected = points.Select(ProjectObserverPoint).ToArray();
        var minimumX = projected.Min(point => point.X);
        var maximumX = projected.Max(point => point.X);
        var minimumY = projected.Min(point => point.Y);
        var maximumY = projected.Max(point => point.Y);
        var sourceWidth = Math.Max(0.0001d, maximumX - minimumX);
        var sourceHeight = Math.Max(0.0001d, maximumY - minimumY);
        var availableWidth = Math.Max(1d, viewport.Width - padding * 2d);
        var availableHeight = Math.Max(1d, viewport.Height - padding * 2d);
        var scale = Math.Min(availableWidth / sourceWidth, availableHeight / sourceHeight);
        var sourceCenter = new IconPoint2(
            (minimumX + maximumX) / 2d,
            (minimumY + maximumY) / 2d);
        var targetCenter = new Point(
            viewport.Left + viewport.Width / 2d,
            viewport.Top + viewport.Height / 2d);
        return worldPoint =>
        {
            var point = ProjectObserverPoint(worldPoint);
            return new Point(
                targetCenter.X + (point.X - sourceCenter.X) * scale,
                targetCenter.Y + (point.Y - sourceCenter.Y) * scale);
        };
    }

    private static IconPoint2 ProjectObserverPoint(IconVector3 point) =>
        new(
            point.X * 0.8660254038d - point.Z * 0.5d,
            point.X * 0.28d - point.Y + point.Z * 0.42d);

    private static Rect FitAspectRatio(Rect available, double aspectRatio)
    {
        var safeAspect = double.IsFinite(aspectRatio) && aspectRatio > 0d ? aspectRatio : 1d;
        var width = available.Width;
        var height = width / safeAspect;
        if (height > available.Height)
        {
            height = available.Height;
            width = height * safeAspect;
        }

        return new Rect(
            available.Left + (available.Width - width) / 2d,
            available.Top + (available.Height - height) / 2d,
            width,
            height);
    }

    private static Point MapImagePoint(
        IconPoint2 imagePoint,
        Rect frame,
        int imageWidth,
        int imageHeight) =>
        new(
            frame.Left + imagePoint.X / imageWidth * frame.Width,
            frame.Top + imagePoint.Y / imageHeight * frame.Height);

    private static StreamGeometry CreateClosedGeometry(IEnumerable<Point> points)
    {
        var materialized = points.ToArray();
        var geometry = new StreamGeometry();
        if (materialized.Length > 0)
        {
            using var context = geometry.Open();
            context.BeginFigure(materialized[0], true, true);
            for (var index = 1; index < materialized.Length; index++)
            {
                context.LineTo(materialized[index], true, false);
            }
        }

        if (geometry.CanFreeze)
        {
            geometry.Freeze();
        }

        return geometry;
    }

    private void DrawCenteredText(
        DrawingContext drawingContext,
        string text,
        Rect bounds,
        Brush brush,
        double fontSize)
    {
        var formatted = CreateFormattedText(text, brush, fontSize);
        drawingContext.DrawText(
            formatted,
            new Point(
                bounds.Left + Math.Max(0d, (bounds.Width - formatted.Width) / 2d),
                bounds.Top + Math.Max(0d, (bounds.Height - formatted.Height) / 2d)));
    }

    private void DrawLabel(
        DrawingContext drawingContext,
        string text,
        Point point,
        Brush brush,
        double fontSize) =>
        drawingContext.DrawText(CreateFormattedText(text, brush, fontSize), point);

    private FormattedText CreateFormattedText(string text, Brush brush, double fontSize) =>
        new(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            fontSize,
            brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private static Brush CreateOpacityBrush(Brush source, double opacity)
    {
        var clone = source.CloneCurrentValue();
        clone.Opacity = Math.Clamp(opacity, 0d, 1d);
        if (clone.CanFreeze)
        {
            clone.Freeze();
        }

        return clone;
    }

    private static Pen CreatePen(Brush brush, double thickness, DoubleCollection? dashPattern = null)
    {
        var pen = new Pen(brush, thickness);
        if (dashPattern is not null)
        {
            pen.DashStyle = new DashStyle(dashPattern, 0d);
        }

        if (pen.CanFreeze)
        {
            pen.Freeze();
        }

        return pen;
    }

    private static DependencyProperty RegisterBrush(string name, Brush defaultValue) =>
        DependencyProperty.Register(
            name,
            typeof(Brush),
            typeof(IconPerspectiveSurface),
            new FrameworkPropertyMetadata(defaultValue, FrameworkPropertyMetadataOptions.AffectsRender));

    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180d;
}
