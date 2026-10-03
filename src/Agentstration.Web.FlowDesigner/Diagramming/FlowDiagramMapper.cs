using Agentstration.Flows;
using Agentstration.Web.FlowDesigner.State;
using Blazor.Diagrams.Core.Geometry;
using Blazor.Diagrams.Core.Models;

namespace Agentstration.Web.FlowDesigner.Diagramming;

public sealed class FlowDiagramNode : NodeModel
{
    public const double RenderedWidth = 256;
    public const double RenderedHeight = 132;

    public FlowDiagramNode(FlowDesignerNode source) : base(source.Name, new Point(source.Position.X, source.Position.Y))
    {
        Source = source;
        Title = source.DisplayName;
        ControlledSize = true;
        Size = new Size(RenderedWidth, RenderedHeight);
        Input = new FlowDiagramPort(this, PortAlignment.Left, FlowDiagramPortDirection.Input);
        Output = new FlowDiagramPort(this, PortAlignment.Right, FlowDiagramPortDirection.Output);
        AddPort(Input);
        AddPort(Output);
    }

    public FlowDesignerNode Source { get; }
    public FlowDiagramPort Input { get; }
    public FlowDiagramPort Output { get; }
}

public enum FlowDiagramPortDirection { Input, Output }

public sealed class FlowDiagramPort(
    FlowDiagramNode parent,
    PortAlignment alignment,
    FlowDiagramPortDirection direction) : PortModel(parent, alignment)
{
    public FlowDiagramPortDirection Direction { get; } = direction;

    public override bool CanAttachTo(Blazor.Diagrams.Core.Models.Base.ILinkable other)
    {
        if (other is not FlowDiagramPort candidate || candidate.Parent == Parent || candidate.Direction == Direction)
            return false;

        var output = Direction == FlowDiagramPortDirection.Output ? this : candidate;
        var input = Direction == FlowDiagramPortDirection.Input ? this : candidate;
        var outputNode = (FlowDiagramNode)output.Parent;
        var inputNode = (FlowDiagramNode)input.Parent;
        return outputNode.Source.Type is not ("output" or "failure") && inputNode.Source.Type != "input";
    }
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
