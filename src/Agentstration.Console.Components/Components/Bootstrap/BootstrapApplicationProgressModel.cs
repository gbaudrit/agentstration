using Agentstration.Bootstrap.Contracts;
using Agentstration.ResourceManagement.Contracts;

namespace Agentstration.Web.Components.Bootstrap;

public sealed record BootstrapApplicationProgressRow(BootstrapPlannedResource Resource, BootstrapAppliedResource? Result, ProgressStepStatus Status);
internal sealed record BootstrapApplicationProgressGroup(string Id, string Kind, IReadOnlyList<BootstrapApplicationProgressRow> Rows);

internal static class BootstrapApplicationProgressModel
{
    public static IReadOnlyList<BootstrapApplicationProgressGroup> Groups(BootstrapApplicationResource application)
    {
        var definition = application.Definition;
        var results = definition.Resources.ToDictionary(row => (row.Profile, row.Location));
        var plan = definition.ResourcePlan.Count > 0 ? definition.ResourcePlan
            : definition.Resources.Select(row => new BootstrapPlannedResource(row.Profile, row.Location, row.Kind, row.Name))
                .Concat(definition.CurrentResource is { } current ? [new(current.Profile, current.Location, current.Kind, current.Name)] : [])
                .DistinctBy(row => (row.Profile, row.Location)).ToArray();
        var groups = new List<BootstrapApplicationProgressGroup>();
        List<BootstrapApplicationProgressRow>? rows = null;
        foreach (var resource in plan)
        {
            if (groups.Count == 0 || groups[^1].Kind != resource.Kind)
            {
                rows = [];
                groups.Add(new(resource.Profile + "/" + resource.Location, resource.Kind, rows));
            }
            results.TryGetValue((resource.Profile, resource.Location), out var result);
            var state = result is not null ? result.Disposition switch
            {
                BootstrapResourceDisposition.Create or BootstrapResourceDisposition.Skip => ProgressStepStatus.Succeeded,
                _ => ProgressStepStatus.Failed
            } : definition.CurrentResource is { } active && active.Profile == resource.Profile && active.Location == resource.Location ? ProgressStepStatus.Running
                : definition.Status == BootstrapApplicationStatus.Running ? ProgressStepStatus.Pending
                : definition.Status == BootstrapApplicationStatus.Cancelled ? ProgressStepStatus.Cancelled : ProgressStepStatus.Interrupted;
            rows!.Add(new(resource, result, state));
        }
        return groups;
    }

    public static ProgressStepStatus Status(IEnumerable<BootstrapApplicationProgressRow> rows)
    {
        var states = rows.Select(row => row.Status).ToArray();
        return states.Contains(ProgressStepStatus.Failed) ? ProgressStepStatus.Failed
            : states.Contains(ProgressStepStatus.Running) ? ProgressStepStatus.Running
            : states.All(state => state == ProgressStepStatus.Succeeded) ? ProgressStepStatus.Succeeded
            : states.Contains(ProgressStepStatus.Cancelled) ? ProgressStepStatus.Cancelled
            : states.Contains(ProgressStepStatus.Interrupted) ? ProgressStepStatus.Interrupted : ProgressStepStatus.Pending;
    }
}
