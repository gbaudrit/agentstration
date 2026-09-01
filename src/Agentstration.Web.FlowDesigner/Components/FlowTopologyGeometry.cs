using System.Globalization;

namespace Agentstration.Web.FlowDesigner.Components;

internal sealed record FlowTopologyGeometry(
    IReadOnlyList<FlowTopologyPositionedNode> Nodes,
    IReadOnlyList<FlowTopologyPositionedEdge> Edges,
    double CanvasWidth,
    double CanvasHeight)
{
    public const double NodeWidth = 172;
    public const double NodeHeight = 76;

    private const double OuterPadding = 72;
    private const double PortGap = 14;
    private const double BypassLaneGap = 16;
    private const double BypassLaneDistance = 52;

    public static FlowTopologyGeometry Calculate(FlowTopologyGraph graph)
    {
        if (graph.Nodes.Count == 0) return new([], [], 600, 260);

        var validEdges = graph.Edges
            .Where(edge => graph.Nodes.Any(node => node.Id == edge.From) && graph.Nodes.Any(node => node.Id == edge.To))
            .ToArray();
        var minimumX = graph.Nodes.Min(node => node.X);
        var minimumY = graph.Nodes.Min(node => node.Y);
        var nodes = graph.Nodes
            .Select(node => new FlowTopologyPositionedNode(node, node.X - minimumX + OuterPadding, node.Y - minimumY + OuterPadding))
            .ToArray();
        var byId = nodes.ToDictionary(node => node.Node.Id, StringComparer.Ordinal);
        var bypasses = validEdges
            .ToDictionary(edge => edge.Id, edge => Bypass(edge, byId), StringComparer.Ordinal);
        var endpointSides = validEdges
            .Select(edge => Sides(edge, byId[edge.From], byId[edge.To], bypasses[edge.Id]))
            .ToDictionary(item => item.Edge.Id, StringComparer.Ordinal);
        var portOffsets = PortOffsets(validEdges, byId, endpointSides);
        var edges = validEdges.Select(edge =>
        {
            var sides = endpointSides[edge.Id];
            var start = Anchor(byId[edge.From], sides.Start, portOffsets[new EdgeEndpoint(edge.Id, true)]);
            var end = Anchor(byId[edge.To], sides.End, portOffsets[new EdgeEndpoint(edge.Id, false)]);
            var hasReverse = validEdges.Any(candidate => candidate.From == edge.To && candidate.To == edge.From);
            return Route(edge, start, end, sides.Start, sides.End, hasReverse, bypasses[edge.Id]);
        }).ToArray();

        var canvasWidth = Math.Max(600, nodes.Max(node => node.X) + NodeWidth + OuterPadding);
        var canvasHeight = Math.Max(240, nodes.Max(node => node.Y) + NodeHeight + OuterPadding);
        return new(nodes, edges, canvasWidth, canvasHeight);
    }

    private static BypassDirection Bypass(
        FlowTopologyEdge edge,
        IReadOnlyDictionary<string, FlowTopologyPositionedNode> nodes)
    {
        var from = nodes[edge.From];
        var to = nodes[edge.To];
        var fromX = CenterX(from);
        var fromY = CenterY(from);
        var toX = CenterX(to);
        var toY = CenterY(to);
        var horizontal = Math.Abs(toX - fromX) >= Math.Abs(toY - fromY);
        var obstructed = nodes.Values.Any(node =>
        {
            if (node.Node.Id == edge.From || node.Node.Id == edge.To) return false;
            var nodeX = CenterX(node);
            var nodeY = CenterY(node);
            if (horizontal)
            {
                if (nodeX <= Math.Min(fromX, toX) || nodeX >= Math.Max(fromX, toX)) return false;
                var progress = (nodeX - fromX) / (toX - fromX);
                var pathY = fromY + ((toY - fromY) * progress);
                return Math.Abs(nodeY - pathY) < NodeHeight;
            }

            if (nodeY <= Math.Min(fromY, toY) || nodeY >= Math.Max(fromY, toY)) return false;
            var verticalProgress = (nodeY - fromY) / (toY - fromY);
            var pathX = fromX + ((toX - fromX) * verticalProgress);
            return Math.Abs(nodeX - pathX) < NodeWidth;
        });
        if (!obstructed) return BypassDirection.None;
        return horizontal ? BypassDirection.Below : BypassDirection.Right;
    }

    private static EdgeSides Sides(
        FlowTopologyEdge edge,
        FlowTopologyPositionedNode from,
        FlowTopologyPositionedNode to,
        BypassDirection bypass)
    {
        if (edge.From == edge.To) return new(edge, PortSide.Right, PortSide.Top);
        if (bypass == BypassDirection.Right) return new(edge, PortSide.Right, PortSide.Right);
        if (bypass == BypassDirection.Below) return new(edge, PortSide.Bottom, PortSide.Bottom);

        var differenceX = CenterX(to) - CenterX(from);
        var differenceY = CenterY(to) - CenterY(from);
        if (Math.Abs(differenceX) >= Math.Abs(differenceY))
        {
            return differenceX >= 0
                ? new(edge, PortSide.Right, PortSide.Left)
                : new(edge, PortSide.Left, PortSide.Right);
        }

        return differenceY >= 0
            ? new(edge, PortSide.Bottom, PortSide.Top)
            : new(edge, PortSide.Top, PortSide.Bottom);
    }

    private static IReadOnlyDictionary<EdgeEndpoint, double> PortOffsets(
        IReadOnlyList<FlowTopologyEdge> edges,
        IReadOnlyDictionary<string, FlowTopologyPositionedNode> nodes,
        IReadOnlyDictionary<string, EdgeSides> sides)
    {
        var endpoints = edges.SelectMany(edge => new[]
        {
            Endpoint(edge, true, edge.From, edge.To, sides[edge.Id].Start, nodes),
            Endpoint(edge, false, edge.To, edge.From, sides[edge.Id].End, nodes)
        });
        var offsets = new Dictionary<EdgeEndpoint, double>();
        foreach (var group in endpoints.GroupBy(endpoint => (endpoint.NodeId, endpoint.Side)))
        {
            var ordered = group
                .OrderBy(endpoint => endpoint.CounterpartCoordinate)
                .ThenBy(endpoint => endpoint.Key.EdgeId, StringComparer.Ordinal)
                .ThenBy(endpoint => endpoint.Key.IsStart)
                .ToArray();
            var availableSpan = group.Key.Side is PortSide.Left or PortSide.Right ? NodeHeight - 28 : NodeWidth - 40;
            var span = Math.Min(availableSpan, Math.Max(0, ordered.Length - 1) * PortGap);
            for (var index = 0; index < ordered.Length; index++)
            {
                offsets[ordered[index].Key] = ordered.Length == 1 ? 0 : (-span / 2) + (span * index / (ordered.Length - 1));
            }
        }

        return offsets;
    }

    private static PortEndpoint Endpoint(
        FlowTopologyEdge edge,
        bool isStart,
        string nodeId,
        string counterpartId,
        PortSide side,
        IReadOnlyDictionary<string, FlowTopologyPositionedNode> nodes)
    {
        var counterpart = nodes[counterpartId];
        var coordinate = side is PortSide.Left or PortSide.Right ? CenterY(counterpart) : CenterX(counterpart);
        return new(new(edge.Id, isStart), nodeId, side, coordinate);
    }

    private static FlowTopologyPoint Anchor(FlowTopologyPositionedNode node, PortSide side, double offset) => side switch
    {
        PortSide.Left => new(node.X, CenterY(node) + offset),
        PortSide.Right => new(node.X + NodeWidth, CenterY(node) + offset),
        PortSide.Top => new(CenterX(node) + offset, node.Y),
        PortSide.Bottom => new(CenterX(node) + offset, node.Y + NodeHeight),
        _ => throw new ArgumentOutOfRangeException(nameof(side))
    };

    private static FlowTopologyPositionedEdge Route(
        FlowTopologyEdge edge,
        FlowTopologyPoint start,
        FlowTopologyPoint end,
        PortSide startSide,
        PortSide endSide,
        bool hasReverse,
        BypassDirection bypass)
    {
        if (edge.From == edge.To)
        {
            var loopX = start.X + 54;
            var loopY = end.Y - 42;
            return new(edge,
                Svg($"M {start.X} {start.Y} C {loopX} {start.Y}, {loopX} {loopY}, {end.X} {loopY} C {end.X - 32} {loopY}, {end.X - 32} {end.Y}, {end.X} {end.Y}"),
                loopX, loopY - 12, LabelWidth(edge.Label));
        }

        if (start == end) return new(edge, Svg($"M {start.X} {start.Y}"), start.X, start.Y, LabelWidth(edge.Label));

        var laneOffset = hasReverse && string.CompareOrdinal(edge.From, edge.To) > 0 ? BypassLaneGap : 0;
        if (bypass == BypassDirection.Right)
        {
            var laneX = Math.Max(start.X, end.X) + BypassLaneDistance + laneOffset;
            return new(edge,
                Svg($"M {start.X} {start.Y} C {laneX} {start.Y}, {laneX} {end.Y}, {end.X} {end.Y}"),
                laneX, ((start.Y + end.Y) / 2) - 12, LabelWidth(edge.Label));
        }

        if (bypass == BypassDirection.Below)
        {
            var laneY = Math.Max(start.Y, end.Y) + BypassLaneDistance + laneOffset;
            return new(edge,
                Svg($"M {start.X} {start.Y} C {start.X} {laneY}, {end.X} {laneY}, {end.X} {end.Y}"),
                (start.X + end.X) / 2, laneY - 12, LabelWidth(edge.Label));
        }

        var horizontal = startSide is PortSide.Left or PortSide.Right && endSide is PortSide.Left or PortSide.Right;
        var controlX = horizontal ? (start.X + end.X) / 2 : start.X;
        var controlY = horizontal ? start.Y : (start.Y + end.Y) / 2;
        var secondControlX = horizontal ? controlX : end.X;
        var secondControlY = horizontal ? end.Y : controlY;
        return new(edge,
            Svg($"M {start.X} {start.Y} C {controlX} {controlY}, {secondControlX} {secondControlY}, {end.X} {end.Y}"),
            (start.X + end.X) / 2, ((start.Y + end.Y) / 2) - 12, LabelWidth(edge.Label));
    }

    private static double LabelWidth(string? label) => label is null ? 0 : Math.Clamp(30 + (label.Length * 5.5), 84, 184);

    private static double CenterX(FlowTopologyPositionedNode node) => node.X + (NodeWidth / 2);

    private static double CenterY(FlowTopologyPositionedNode node) => node.Y + (NodeHeight / 2);

    private static string Svg(FormattableString value) => value.ToString(CultureInfo.InvariantCulture);

    private enum PortSide
    {
        Left,
        Right,
        Top,
        Bottom
    }

    private enum BypassDirection
    {
        None,
        Right,
        Below
    }

    private readonly record struct EdgeEndpoint(string EdgeId, bool IsStart);
    private sealed record PortEndpoint(EdgeEndpoint Key, string NodeId, PortSide Side, double CounterpartCoordinate);
    private sealed record EdgeSides(FlowTopologyEdge Edge, PortSide Start, PortSide End);
}

internal sealed record FlowTopologyPositionedNode(FlowTopologyNode Node, double X, double Y);
internal sealed record FlowTopologyPositionedEdge(FlowTopologyEdge Edge, string Path, double LabelX, double LabelY, double LabelWidth);
internal readonly record struct FlowTopologyPoint(double X, double Y);
