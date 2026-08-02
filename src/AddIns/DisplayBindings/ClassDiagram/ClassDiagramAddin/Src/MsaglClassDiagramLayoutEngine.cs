using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Msagl.Core.Geometry;
using Microsoft.Msagl.Core.Geometry.Curves;
using Microsoft.Msagl.Core.Layout;
using Microsoft.Msagl.Core.Routing;
using Microsoft.Msagl.Layout.Layered;
using Microsoft.Msagl.Miscellaneous;

namespace ICSharpCode.ClassDiagram;

public sealed class MsaglClassDiagramLayoutEngine
{
    const double NodeWidth = 280;
    const double ExpandedNodeHeight = 315;
    const double CollapsedNodeHeight = 42;

    public IReadOnlyList<ClassDiagramRoute> Arrange(
        ClassDiagramDocument document,
        IReadOnlyDictionary<string, ClassDiagramNodeSize> measuredSizes = null)
    {
        var graph = CreateGraph(document, measuredSizes, useSavedPositions: false);

        var settings = new SugiyamaLayoutSettings {
            LayerSeparation = 70,
            NodeSeparation = 45
        };
        ConfigureRouting(settings);
        LayoutHelpers.CalculateLayout(graph, settings, null);

        var left = graph.Nodes.Min(node => node.BoundingBox.Left);
        var top = graph.Nodes.Max(node => node.BoundingBox.Top);
        foreach (var node in graph.Nodes) {
            var state = document.NodeStates[(string)node.UserData];
            state.X = 30 + node.BoundingBox.Left - left;
            state.Y = 30 + top - node.BoundingBox.Top;
        }
        return ExtractRoutes(graph, point => new ClassDiagramRoutePoint(30 + point.X - left, 30 + top - point.Y));
    }

    public IReadOnlyList<ClassDiagramRoute> Route(
        ClassDiagramDocument document,
        IReadOnlyDictionary<string, ClassDiagramNodeSize> measuredSizes = null)
    {
        var graph = CreateGraph(document, measuredSizes, useSavedPositions: true);
        var settings = new SugiyamaLayoutSettings();
        ConfigureRouting(settings);
        LayoutHelpers.RouteAndLabelEdges(graph, settings, graph.Edges, 0, null);
        return ExtractRoutes(graph, point => new ClassDiagramRoutePoint(point.X, -point.Y));
    }

    static void ConfigureRouting(LayoutAlgorithmSettings settings)
    {
        settings.EdgeRoutingSettings.EdgeRoutingMode = EdgeRoutingMode.Rectilinear;
        settings.EdgeRoutingSettings.Padding = 12;
        settings.EdgeRoutingSettings.PolylinePadding = 6;
        settings.EdgeRoutingSettings.CornerRadius = 0;
        settings.EdgeRoutingSettings.BendPenalty = 4;
    }

    static GeometryGraph CreateGraph(
        ClassDiagramDocument document,
        IReadOnlyDictionary<string, ClassDiagramNodeSize> measuredSizes,
        bool useSavedPositions)
    {
        var graph = new GeometryGraph();
        var nodes = new Dictionary<string, Node>(StringComparer.Ordinal);
        foreach (var type in document.Types) {
            var id = ClassDiagramDocument.GetNodeId(type);
            var state = document.NodeStates[id];
            var fallbackHeight = state.Collapsed ? CollapsedNodeHeight : ExpandedNodeHeight;
            var size = measuredSizes is not null && measuredSizes.TryGetValue(id, out var measured)
                ? measured
                : new ClassDiagramNodeSize(NodeWidth, fallbackHeight);
            var center = useSavedPositions
                ? new Point(state.X + size.Width / 2, -(state.Y + size.Height / 2))
                : new Point(0, 0);
            var node = new Node(CurveFactory.CreateRectangle(size.Width, size.Height, center)) { UserData = id };
            graph.Nodes.Add(node);
            nodes[id] = node;
        }

        foreach (var child in document.Types) {
            var childNode = nodes[ClassDiagramDocument.GetNodeId(child)];
            foreach (var baseType in child.BaseTypeIdentities) {
                var parent = document.Types.FirstOrDefault(type => type.QualifiedName == baseType);
                if (parent is not null)
                    graph.Edges.Add(new Edge(nodes[ClassDiagramDocument.GetNodeId(parent)], childNode) {
                        UserData = new LayoutEdgeData(child.QualifiedName, parent.QualifiedName, null, true)
                    });
            }
        }
        foreach (var relationship in document.Relationships) {
            var source = document.Types.FirstOrDefault(type => type.QualifiedName == relationship.SourceType);
            var target = document.Types.FirstOrDefault(type => type.QualifiedName == relationship.TargetType);
            if (source is not null && target is not null)
                graph.Edges.Add(new Edge(nodes[ClassDiagramDocument.GetNodeId(source)], nodes[ClassDiagramDocument.GetNodeId(target)]) {
                    UserData = new LayoutEdgeData(relationship.SourceType, relationship.TargetType, relationship.Kind, false)
                });
        }
        return graph;
    }

    static IReadOnlyList<ClassDiagramRoute> ExtractRoutes(
        GeometryGraph graph,
        Func<Point, ClassDiagramRoutePoint> transform)
    {
        var routes = new List<ClassDiagramRoute>();
        foreach (var edge in graph.Edges.Where(edge => edge.Curve is not null && edge.UserData is LayoutEdgeData)) {
            var data = (LayoutEdgeData)edge.UserData;
            var points = GetCurvePoints(edge.Curve).Select(transform).ToList();
            if (data.Reverse)
                points.Reverse();
            routes.Add(new ClassDiagramRoute(data.Source, data.Target, data.Kind, data.IsInheritance, points));
        }
        return routes;
    }

    static IEnumerable<Point> GetCurvePoints(ICurve curve)
    {
        if (curve is Curve composite) {
            var first = true;
            foreach (var segment in composite.Segments) {
                foreach (var point in GetCurvePoints(segment)) {
                    if (first || point != segment.Start)
                        yield return point;
                    first = false;
                }
            }
        } else {
            yield return curve.Start;
            yield return curve.End;
        }
    }

    sealed record LayoutEdgeData(
        string Source,
        string Target,
        ClassDiagramRelationshipKind? Kind,
        bool IsInheritance)
    {
        public bool Reverse => IsInheritance;
    }
}

public readonly record struct ClassDiagramNodeSize(double Width, double Height);
public readonly record struct ClassDiagramRoutePoint(double X, double Y);
public sealed record ClassDiagramRoute(
    string Source,
    string Target,
    ClassDiagramRelationshipKind? Kind,
    bool IsInheritance,
    IReadOnlyList<ClassDiagramRoutePoint> Points);
