using Agentstration.Application.Work;
using Agentstration.Flows;
using Agentstration.Flows.Application;
using Agentstration.Work;

namespace Agentstration.Infrastructure.Flows;

public sealed class EntryExecutionResolver(FlowService flows) : IEntryExecutionResolver
{
    public async Task<EntryExecutionResolution> ResolveAsync(
        EntryResource entry,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Behavior.TaskCreationMode == TaskCreationMode.Never)
            return EntryExecutionResolution.Executable;
        if (entry.ResolvedTarget.VersionStrategy != EntryVersionStrategy.Pinned
            || string.IsNullOrWhiteSpace(entry.ResolvedTarget.FlowResourceId)
            || string.IsNullOrWhiteSpace(entry.ResolvedTarget.Version))
            return new(EntryExecutionAvailability.Unavailable, "entry_target_invalid");
        var flowId = new FlowId(entry.ResolvedTarget.FlowResourceId, entry.ResolvedTarget.Namespace);
        var flow = await flows.GetAsync(entry.WorkspaceId, flowId, cancellationToken);
        if (flow is null)
            return new(EntryExecutionAvailability.Unavailable, "entry_flow_unavailable");
        var version = await flows.GetVersionAsync(
            entry.WorkspaceId,
            flowId,
            entry.ResolvedTarget.Version,
            cancellationToken);
        if (version is null)
            return new(EntryExecutionAvailability.Unavailable, "entry_flow_version_unavailable");
        return flow.Value.Enabled
            ? EntryExecutionResolution.Executable
            : new(EntryExecutionAvailability.Disabled, "entry_flow_disabled");
    }
}
