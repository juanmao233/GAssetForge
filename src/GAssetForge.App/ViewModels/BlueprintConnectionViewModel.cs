using GAssetForge.App.Infrastructure;
using GAssetForge.Core.Blueprints;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace GAssetForge.App.ViewModels;

public sealed class BlueprintConnectionViewModel : ObservableObject
{
    private readonly BlueprintViewModel _owner;
    private Geometry _geometry = Geometry.Empty;

    internal BlueprintConnectionViewModel(BlueprintViewModel owner, BlueprintConnection model)
    {
        _owner = owner;
        Model = model;
        RemoveCommand = new RelayCommand(() => _owner.RemoveConnection(this));
    }

    internal BlueprintConnection Model { get; }

    public string Id => Model.Id;

    public Geometry Geometry
    {
        get => _geometry;
        private set => SetProperty(ref _geometry, value);
    }

    public ICommand RemoveCommand { get; }

    internal void Refresh()
    {
        var sourceNode = _owner.Nodes.FirstOrDefault(node =>
            string.Equals(node.Id, Model.SourceNodeId, StringComparison.Ordinal));
        var targetNode = _owner.Nodes.FirstOrDefault(node =>
            string.Equals(node.Id, Model.TargetNodeId, StringComparison.Ordinal));
        var sourcePort = sourceNode?.OutputPorts.FirstOrDefault(port =>
            string.Equals(port.Id, Model.SourcePortId, StringComparison.Ordinal));
        var targetPort = targetNode?.InputPorts.FirstOrDefault(port =>
            string.Equals(port.Id, Model.TargetPortId, StringComparison.Ordinal));
        if (sourceNode is null || targetNode is null || sourcePort is null || targetPort is null)
        {
            Geometry = Geometry.Empty;
            return;
        }

        Geometry = BlueprintConnectionGeometry.CreateConnection(sourcePort, targetPort);
    }
}

internal static class BlueprintConnectionGeometry
{
    private const double PortTop = BlueprintNodeLayoutMetrics.FirstPortCenterY;
    private const double PortSpacing = BlueprintNodeLayoutMetrics.PortRowHeight;

    internal static Point GetPortAnchor(BlueprintPortViewModel port)
    {
        ArgumentNullException.ThrowIfNull(port);
        var ports = port.Direction == BlueprintPortDirection.Output
            ? port.Node.OutputPorts
            : port.Node.InputPorts;
        var visualIndex = ports.IndexOf(port);
        if (visualIndex < 0)
        {
            return new Point(port.Node.X, port.Node.Y);
        }

        return new Point(
            port.Direction == BlueprintPortDirection.Output
                ? port.Node.X + port.Node.Width - 1d
                : port.Node.X + 1d,
            port.Node.Y + PortTop + visualIndex * PortSpacing);
    }

    internal static Geometry CreateConnection(
        BlueprintPortViewModel output,
        BlueprintPortViewModel input)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(input);
        return CreateBezier(
            GetPortAnchor(output),
            BlueprintPortDirection.Output,
            GetPortAnchor(input),
            BlueprintPortDirection.Input);
    }

    internal static Geometry CreatePreview(BlueprintPortViewModel port, Point pointer)
    {
        ArgumentNullException.ThrowIfNull(port);
        var opposite = port.Direction == BlueprintPortDirection.Output
            ? BlueprintPortDirection.Input
            : BlueprintPortDirection.Output;
        return CreateBezier(GetPortAnchor(port), port.Direction, pointer, opposite);
    }

    private static Geometry CreateBezier(
        Point start,
        BlueprintPortDirection startDirection,
        Point end,
        BlueprintPortDirection endDirection)
    {
        var handle = Math.Max(72d, Math.Abs(end.X - start.X) * 0.45d);
        var firstHandle = new Point(
            start.X + (startDirection == BlueprintPortDirection.Output ? handle : -handle),
            start.Y);
        var secondHandle = new Point(
            end.X + (endDirection == BlueprintPortDirection.Output ? handle : -handle),
            end.Y);
        var figure = new PathFigure { StartPoint = start, IsClosed = false, IsFilled = false };
        figure.Segments.Add(new BezierSegment(firstHandle, secondHandle, end, isStroked: true));
        return new PathGeometry([figure]);
    }
}
