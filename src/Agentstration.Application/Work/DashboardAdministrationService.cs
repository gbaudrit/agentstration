using Agentstration.Resources;
using Agentstration.Work;
using Agentstration.Work.Storage.Abstractions;

namespace Agentstration.Application.Work;

public sealed class DashboardAdministrationService(IWorkplaceRepository repository, TimeProvider timeProvider)
{
    public Task<IReadOnlyList<WorkplaceDashboardDraft>> ListAsync(
        WorkspaceId workspaceId,
        CancellationToken cancellationToken) =>
        repository.ListDashboardDraftsAsync(workspaceId, cancellationToken);

    public async Task<WorkplaceDashboardDraft> GetAsync(
        WorkspaceId workspaceId,
        DashboardId id,
        CancellationToken cancellationToken) =>
        await repository.GetDashboardDraftAsync(workspaceId, id, cancellationToken)
        ?? throw new KeyNotFoundException($"Dashboard draft '{id}' was not found in Workspace '{workspaceId}'.");

    public async Task<WorkplaceDashboardDraft> SaveAsync(
        WorkplaceDashboardDraft draft,
        CancellationToken cancellationToken)
    {
        WorkplaceValidation.Validate(draft);
        var current = await repository.GetDashboardDraftAsync(draft.WorkspaceId, draft.Id, cancellationToken);
        var saved = draft with
        {
            Revision = current is null ? 1 : checked(current.Revision + 1),
            UpdatedAt = timeProvider.GetUtcNow()
        };
        if (saved.IsDefault)
        {
            var drafts = await repository.ListDashboardDraftsAsync(saved.WorkspaceId, cancellationToken);
            foreach (var other in drafts.Where(value => value.Id != saved.Id && value.IsDefault))
            {
                await repository.UpsertDashboardDraftAsync(other with
                {
                    IsDefault = false,
                    Revision = checked(other.Revision + 1),
                    UpdatedAt = saved.UpdatedAt
                }, cancellationToken);
            }
        }
        await repository.UpsertDashboardDraftAsync(saved, cancellationToken);
        return saved;
    }

    public async Task<WorkplaceDashboard> PublishAsync(
        WorkspaceId workspaceId,
        DashboardId id,
        CancellationToken cancellationToken)
    {
        var draft = await GetAsync(workspaceId, id, cancellationToken);
        WorkplaceValidation.Validate(draft);
        foreach (var reference in draft.Entries)
        {
            _ = await repository.GetEntryAsync(workspaceId, reference.EntryResourceId, cancellationToken)
                ?? throw new WorkValidationException(
                    "dashboard_entry_not_published",
                    $"Entry '{reference.EntryResourceId}' is not published.");
        }

        var dashboards = await repository.ListDashboardsAsync(workspaceId, cancellationToken);
        var previous = dashboards.SingleOrDefault(value => value.Id == id);
        if (!draft.IsDefault && dashboards.Where(value => value.Id != id).All(value => !value.IsDefault))
            throw new WorkValidationException(
                "dashboard_default_required",
                "The first published Dashboard in a Workspace must be the default Dashboard.");

        var published = new WorkplaceDashboard
        {
            Id = draft.Id,
            WorkspaceId = draft.WorkspaceId,
            Name = draft.Name,
            Type = draft.Type,
            ApiVersion = draft.ApiVersion,
            DisplayName = draft.DisplayName,
            Description = draft.Description,
            IsDefault = draft.IsDefault,
            Entries = draft.Entries,
            Version = previous is null ? 1 : checked(previous.Version + 1),
            PublishedAt = timeProvider.GetUtcNow()
        };
        WorkplaceValidation.Validate(published);
        if (published.IsDefault)
            await repository.ReplaceDefaultDashboardAsync(published, cancellationToken);
        else
            await repository.UpsertDashboardAsync(published, cancellationToken);
        return published;
    }

    public async Task DeleteAsync(
        WorkspaceId workspaceId,
        DashboardId id,
        CancellationToken cancellationToken)
    {
        var draft = await repository.GetDashboardDraftAsync(workspaceId, id, cancellationToken);
        var published = await repository.GetDashboardAsync(workspaceId, id, cancellationToken);
        if (draft is null && published is null)
            throw new KeyNotFoundException($"Dashboard '{id}' was not found in Workspace '{workspaceId}'.");
        if (published?.IsDefault == true)
            throw new WorkValidationException(
                "dashboard_default_delete_conflict",
                "The default Dashboard cannot be deleted. Set another Dashboard as default first.");
        await repository.DeleteDashboardAsync(workspaceId, id, cancellationToken);
        await repository.DeleteDashboardDraftAsync(workspaceId, id, cancellationToken);
    }

    public async Task<WorkplaceDashboard> EnsureHomeAsync(
        WorkspaceId workspaceId,
        CancellationToken cancellationToken)
    {
        var existing = await repository.ListDashboardsAsync(workspaceId, cancellationToken);
        var currentDefault = existing.SingleOrDefault(value => value.IsDefault);
        if (currentDefault is not null) return currentDefault;

        var homeId = new DashboardId("home");
        var draft = await repository.GetDashboardDraftAsync(workspaceId, homeId, cancellationToken)
            ?? new WorkplaceDashboardDraft
            {
                Id = homeId,
                WorkspaceId = workspaceId,
                Name = homeId.Value,
                DisplayName = "Home",
                IsDefault = true
            };
        if (!draft.IsDefault) draft = draft with { IsDefault = true };
        var saved = await SaveAsync(draft, cancellationToken);
        return await PublishAsync(workspaceId, saved.Id, cancellationToken);
    }
}
