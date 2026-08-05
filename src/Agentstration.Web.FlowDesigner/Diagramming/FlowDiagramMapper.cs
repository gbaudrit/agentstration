using Agentstration.Flow;
using Agentstration.Web.FlowDesigner.State;
using Blazor.Diagrams.Core.Geometry;
using Blazor.Diagrams.Core.Models;

namespace Agentstration.Web.FlowDesigner.Diagramming;

public sealed class FlowDiagramNode : NodeModel
{
    public FlowDiagramNode(FlowDesignerNode source) : base(source.Name, new Point(source.Position.X, source.Position.Y))
    {
        Source = source;
        Title = source.DisplayName;
        Input = AddPort(PortAlignment.Left);
        Output = AddPort(PortAlignment.Right);
    }

    public FlowDesignerNode Source { get; }
    public PortModel Input { get; }
    public PortModel Output { get; }
}

public sealed record FlowDiagramProjection(
    IReadOnlyList<FlowDiagramNode> Nodes,
    IReadOnlyList<LinkModel> Links,
    IReadOnlyDictionary<string, FlowDiagramNode> NodesByName);

public static class FlowDiagramMapper
{
    public static FlowDiagramProjection Project(FlowDesignerDocument document)
    {
        var nodes = document.Nodes.Select(node => new FlowDiagramNode(node)).ToArray();
        var byName = nodes.ToDictionary(node => node.Source.Name, StringComparer.Ordinal);
        var links = document.Links
            .Where(link => byName.ContainsKey(link.From) && byName.ContainsKey(link.To))
            .Select(link =>
            {
                var model = new LinkModel(link.Id, byName[link.From].Output, byName[link.To].Input);
                model.AddLabel(link.Event, offset: new Point(0, -14));
                return model;
            })
            .ToArray();
        return new(nodes, links, byName);
    }
}
