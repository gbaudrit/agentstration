using Agentstration.Management.Abstractions;
using Agentstration.Management.Core;
using Agentstration.Flow;
using Agentstration.Flow.Application;
using Agentstration.Work.Storage.Abstractions;

namespace Agentstration.Infrastructure.Flows;

public sealed class EntryResourceDeletionGuard(IWorkplaceRepository workplace) : IManagementResourceDeletionGuard, IFlowDeletionGuard
{
    public async Task ValidateDeleteAsync(ResourceKey key, CancellationToken cancellationToken)
    {
        await workplace.InitializeAsync(cancellationToken);
        var drafts = await workplace.ListEntryDraftsAsync(cancellationToken);
        var publishedIds = (await workplace.ListEntriesAsync(cancellationToken)).Select(value => value.Id).ToHashSet();
        var referenced = drafts.Where(value => publishedIds.Contains(value.Id) && string.Equals(ResourceName(value.PublishedBinding?.ResourceId ?? string.Empty), key.Name, StringComparison.Ordinal)).Select(value => value.Name).ToArray();
        if (referenced.Length > 0)
            throw new AgentDefinitionValidationException("resource_in_use", $"Resource '{key}' is referenced by Entry: {string.Join(", ", referenced)}.");
    }

    async Task IFlowDeletionGuard.ValidateDeleteAsync(FlowId flowId, CancellationToken cancellationToken)
    {
        await workplace.InitializeAsync(cancellationToken);
        var drafts = await workplace.ListEntryDraftsAsync(cancellationToken);
        var publishedIds = (await workplace.ListEntriesAsync(cancellationToken)).Select(value => value.Id).ToHashSet();
        var referenced = drafts.Where(value => publishedIds.Contains(value.Id)
                && value.PublishedBinding?.Kind == Agentstration.Work.EntryBindingKind.Flow
                && string.Equals(ResourceName(value.PublishedBinding.ResourceId), flowId.Value, StringComparison.Ordinal))
            .Select(value => value.Name).ToArray();
        if (referenced.Length > 0)
            throw new FlowValidationException("flow_in_use", $"Flow '{flowId}' is referenced by Entry: {string.Join(", ", referenced)}.");
    }

    private static string ResourceName(string resourceId) => resourceId;
}
