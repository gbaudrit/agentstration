using Agentstration.Resources;
using Agentstration.Work;
using Agentstration.Work.Storage.Abstractions;

namespace Agentstration.Application.Work;

public sealed record EntryDiscoveryWorkspace(WorkspaceId WorkspaceId, bool IsCurrent);

public interface IEntryDiscoveryAuthorization
{
    Task<IReadOnlyList<EntryDiscoveryWorkspace>> ListReadableWorkspacesInCurrentTenantAsync(CancellationToken cancellationToken);
}

public sealed class EntryDiscoveryService(
    IWorkplaceRepository repository,
    IEntryDiscoveryAuthorization authorization)
{
    public async Task<IReadOnlyList<EntryResource>> DiscoverAsync(
        EntryExposureSurface surface,
        EntryWorkplacePlacement? workplacePlacement,
        CancellationToken cancellationToken)
    {
        ValidateQuery(surface, workplacePlacement);
        var workspaces = await authorization.ListReadableWorkspacesInCurrentTenantAsync(cancellationToken);
        if (surface == EntryExposureSurface.Workplace
            && workplacePlacement == EntryWorkplacePlacement.OwningSpace)
            workspaces = workspaces.Where(value => value.IsCurrent).ToArray();

        var result = new List<EntryResource>();
        foreach (var workspace in workspaces)
        {
            var entries = await repository.ListEntriesAsync(workspace.WorkspaceId, cancellationToken);
            result.AddRange(entries.Where(value =>
                value.WorkspaceId == workspace.WorkspaceId
                && EntryExposurePolicy.Allows(value.Exposure, surface, workplacePlacement)));
        }

        return result
            .OrderBy(value => value.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(value => value.WorkspaceId.Value)
            .ThenBy(value => value.Id.Namespace.Value, StringComparer.Ordinal)
            .ThenBy(value => value.Id.Value, StringComparer.Ordinal)
            .ToArray();
    }

    private static void ValidateQuery(
        EntryExposureSurface surface,
        EntryWorkplacePlacement? workplacePlacement)
    {
        if (!Enum.IsDefined(surface))
            throw new WorkValidationException("entry_exposure_surface_invalid", "The Entry exposure surface is not supported.");
        if (surface == EntryExposureSurface.Workplace)
        {
            if (workplacePlacement is null || !Enum.IsDefined(workplacePlacement.Value))
                throw new WorkValidationException("entry_workplace_placement_invalid", "Workplace Entry discovery requires a supported placement.");
            return;
        }
        if (workplacePlacement is not null)
            throw new WorkValidationException("entry_workplace_placement_not_allowed", "A Workplace placement can only be used with the Workplace surface.");
    }
}
