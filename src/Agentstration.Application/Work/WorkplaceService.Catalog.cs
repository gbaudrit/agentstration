using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentstration.Resources;
using Agentstration.Work;
using Agentstration.Work.Contracts;
using Agentstration.Work.Storage.Abstractions;

namespace Agentstration.Application.Work;

public sealed partial class WorkplaceService
{
    public Task InitializeAsync(CancellationToken cancellationToken) => repository.InitializeAsync(cancellationToken);

    public Task<IReadOnlyList<WorkplaceDashboard>> ListDashboardsAsync(WorkspaceId workspaceId, CancellationToken cancellationToken) => repository.ListDashboardsAsync(workspaceId, cancellationToken);

    public Task<IReadOnlyList<EntryResource>> ListEntriesAsync(WorkspaceId workspaceId, CancellationToken cancellationToken) => repository.ListEntriesAsync(workspaceId, cancellationToken);

    public Task<IReadOnlyList<EntryResource>> ListEntriesAsync(CancellationToken cancellationToken) => ListEntriesAsync(context.WorkspaceId, cancellationToken);

    public Task UpsertEntryAsync(EntryResource entry, CancellationToken cancellationToken) => repository.UpsertEntryAsync(entry, cancellationToken);

    public async Task<WorkplaceDashboard> GetDashboardAsync(WorkspaceId workspaceId, DashboardId id, CancellationToken cancellationToken) =>
        await repository.GetDashboardAsync(workspaceId, id, cancellationToken)
        ?? throw new KeyNotFoundException($"Dashboard '{id}' was not found in Workspace '{workspaceId}'.");

    public async Task<WorkplaceDashboard> GetDefaultDashboardAsync(WorkspaceId workspaceId, CancellationToken cancellationToken) =>
        (await repository.ListDashboardsAsync(workspaceId, cancellationToken)).SingleOrDefault(value => value.IsDefault)
        ?? throw new KeyNotFoundException($"Workspace '{workspaceId}' has no default Dashboard.");

    public async Task<EntryResource> GetEntryAsync(WorkspaceId workspaceId, EntryId id, CancellationToken cancellationToken) => await repository.GetEntryAsync(workspaceId, id, cancellationToken) ?? throw new KeyNotFoundException($"Entry '{id}' was not found in Workspace '{workspaceId}'.");

    public Task<EntryResource> GetEntryAsync(EntryId id, CancellationToken cancellationToken) => GetEntryAsync(context.WorkspaceId, id, cancellationToken);

    public async Task<IReadOnlyList<EntryResource>> ResolveEntriesAsync(WorkspaceId workspaceId, DashboardId dashboardId, CancellationToken cancellationToken)
    {
        var dashboard = await GetDashboardAsync(workspaceId, dashboardId, cancellationToken);
        var entries = new List<EntryResource>(dashboard.Entries.Count);
        foreach (var reference in dashboard.Entries.OrderBy(value => value.Order)) entries.Add(await GetEntryAsync(workspaceId, reference.EntryResourceId, cancellationToken));
        return entries;
    }
}

