using Agentstration.Web.FlowDesigner.Diagramming;
using Blazor.Diagrams.Core.Behaviors;
using Blazor.Diagrams.Core.Events;
using Blazor.Diagrams.Core.Models;
using Blazor.Diagrams.Core.Models.Base;
using Microsoft.AspNetCore.Components;

namespace Agentstration.Web.FlowDesigner.Components;

public partial class FlowCanvas
{
    [Parameter] public Func<string, string, string, string, string?>? ConnectionProblem { get; set; }
    [Parameter] public Func<string, string?>? DescribeResource { get; set; }
    private FlowDiagramPort? hoveredPort;
    private string? previewProblem;
    private LinkModel? previewLink;
    private string? previewColor, previewSelectedColor;
    private readonly Dictionary<FlowDiagramPort, string?> persistentProblems = [];

    private string? Problem(FlowDiagramPort first, FlowDiagramPort second)
    {
        var output = first.Direction == FlowDiagramPortDirection.Output ? first : second;
        var input = first.Direction == FlowDiagramPortDirection.Input ? first : second;
        if (!output.CanAttachTo(input) && !IsReadOnly) return null;
        return ConnectionProblem?.Invoke(((FlowDiagramNode)output.Parent).Source.Name, output.EventName ?? "output",
            ((FlowDiagramNode)input.Parent).Source.Name, input.EventName ?? "input");
    }
    private void RefreshCompatibility()
    {
        persistentProblems.Clear();
        foreach (var port in diagram.Nodes.OfType<FlowDiagramNode>().SelectMany(node => node.Ports).OfType<FlowDiagramPort>()) port.ContractProblem = null;
        foreach (var link in diagram.Links.OfType<LinkModel>())
        {
            if (link.Source.Model is not FlowDiagramPort source || link.Target.Model is not FlowDiagramPort target) continue;
            var problem = Problem(source, target);
            if (problem is not null)
            {
                link.Color = link.SelectedColor = "var(--color-danger)";
                target.ContractProblem = problem; persistentProblems[target] = problem;
            }
        }
    }
    private void PreviewEnter(Model? model, PointerEventArgs args)
    {
        if (model is FlowDiagramPort port) hoveredPort = port;
        RefreshPreview(model, args);
    }
    private void PreviewLeave(Model? model, PointerEventArgs args)
    {
        if (model == hoveredPort) { RestoreHoveredPort(); hoveredPort = null; }
        RefreshPreview(null, args);
    }
    private void RefreshPreview(Model? model, PointerEventArgs args)
    {
        var ongoing = diagram.GetBehavior<DragNewLinkBehavior>()?.OngoingLink as LinkModel;
        if (IsReadOnly || ongoing is null) { ClearPreview(); return; }
        if (previewLink != ongoing)
        {
            ClearPreview(); previewLink = ongoing; previewColor = ongoing.Color; previewSelectedColor = ongoing.SelectedColor;
        }
        var fixedPort = ongoing.Source.Model as FlowDiagramPort ?? ongoing.Target.Model as FlowDiagramPort;
        var point = diagram.GetRelativeMousePoint(args.ClientX, args.ClientY);
        var candidate = model as FlowDiagramPort ?? diagram.Nodes.OfType<FlowDiagramNode>()
            .SelectMany(node => node.Ports).OfType<FlowDiagramPort>()
            .FirstOrDefault(port => port.Initialized && port.Visible && fixedPort is not null
                && fixedPort.CanAttachTo(port) && port.GetBounds().ContainsPoint(point));
        if (candidate != hoveredPort) { RestoreHoveredPort(); hoveredPort = candidate; }
        var problem = candidate is not null && fixedPort is not null ? Problem(fixedPort, candidate) : null;
        ongoing.Color = problem is null ? previewColor! : "var(--color-danger)";
        ongoing.SelectedColor = problem is null ? previewSelectedColor! : "var(--color-danger)";
        ongoing.Refresh();
        if (candidate is not null)
        {
            candidate.ContractProblem = problem ?? persistentProblems.GetValueOrDefault(candidate);
            candidate.Parent.Refresh();
        }
        if (previewProblem == problem) return;
        previewProblem = problem; _ = InvokeAsync(StateHasChanged);
    }
    private void RestoreHoveredPort()
    {
        if (hoveredPort is null) return;
        hoveredPort.ContractProblem = persistentProblems.GetValueOrDefault(hoveredPort); hoveredPort.Parent.Refresh();
    }
    private void ClearPreview()
    {
        RestoreHoveredPort();
        if (previewLink is not null) { previewLink.Color = previewColor!; previewLink.SelectedColor = previewSelectedColor!; previewLink.Refresh(); }
        previewLink = null;
        if (previewProblem is not null) { previewProblem = null; _ = InvokeAsync(StateHasChanged); }
    }
    private void PreviewEnd(Model? model, PointerEventArgs args) { ClearPreview(); hoveredPort = null; }
    private void PreviewKeyDown(KeyboardEventArgs args) { if (args.Key == "Escape") ClearPreview(); }
}
