using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Msagl.Core.Geometry;
using Microsoft.Msagl.Core.Geometry.Curves;
using Microsoft.Msagl.Core.Layout;
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
        var graph = new GeometryGraph();
        var nodes = new Dictionary<string, Node>(StringComparer.Ordinal);
        foreach (var type in document.Types) {
            var id = ClassDiagramDocument.GetNodeId(type);
            var state = document.NodeStates[id];
            var fallbackHeight = state.Collapsed ? CollapsedNodeHeight : ExpandedNodeHeight;
            var size = measuredSizes is not null && measuredSizes.TryGetValue(id, out var measured)
                ? measured
                : new ClassDiagramNodeSize(NodeWidth, fallbackHeight);
            var node = new Node(CurveFactory.CreateRectangle(size.Width, size.Height, new Point(0, 0))) {
                UserData = id
            };
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

        var settings = new SugiyamaLayoutSettings {
            LayerSeparation = 70,
            NodeSeparation = 45
        };
        LayoutHelpers.CalculateLayout(graph, settings, null);

        var left = graph.Nodes.Min(node => node.BoundingBox.Left);
        var top = graph.Nodes.Max(node => node.BoundingBox.Top);
        foreach (var node in graph.Nodes) {
            var state = document.NodeStates[(string)node.UserData];
            state.X = 30 + node.BoundingBox.Left - left;
            state.Y = 30 + top - node.BoundingBox.Top;
        }
        var routes = new List<ClassDiagramRoute>();
        foreach (var edge in graph.Edges.Where(edge => edge.Curve is not null && edge.UserData is LayoutEdgeData)) {
            var data = (LayoutEdgeData)edge.UserData;
            var points = Enumerable.Range(0, 25).Select(index => {
                var parameter = edge.Curve.ParStart + (edge.Curve.ParEnd - edge.Curve.ParStart) * index / 24.0;
                var point = edge.Curve[parameter];
                return new ClassDiagramRoutePoint(30 + point.X - left, 30 + top - point.Y);
            }).ToList();
            if (data.Reverse)
                points.Reverse();
            routes.Add(new ClassDiagramRoute(data.Source, data.Target, data.Kind, data.IsInheritance, points));
        }
        return routes;
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
