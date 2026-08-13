using Agentstration.Work;
using Agentstration.Work.Storage.Abstractions;

namespace Agentstration.Application.Work;

public sealed class WorkspaceAdministrationService(IWorkplaceRepository repository, TimeProvider timeProvider)
{
    public Task<IReadOnlyList<WorkplaceWorkspaceDraft>> ListAsync(CancellationToken cancellationToken) => repository.ListWorkspaceDraftsAsync(cancellationToken);

    public async Task<WorkplaceWorkspaceDraft> GetAsync(WorkplaceWorkspaceId id, CancellationToken cancellationToken) =>
        await repository.GetWorkspaceDraftAsync(id, cancellationToken) ?? throw new KeyNotFoundException($"Workspace draft '{id}' was not found.");

    public async Task<WorkplaceWorkspaceDraft> SaveAsync(WorkplaceWorkspaceDraft draft, CancellationToken cancellationToken)
    {
        WorkplaceValidation.Validate(draft);
        var current = await repository.GetWorkspaceDraftAsync(draft.Id, cancellationToken);
        var saved = draft with { Revision = current is null ? 1 : checked(current.Revision + 1), UpdatedAt = timeProvider.GetUtcNow() };
        await repository.UpsertWorkspaceDraftAsync(saved, cancellationToken);
        return saved;
    }

    public async Task<WorkplaceWorkspace> PublishAsync(WorkplaceWorkspaceId id, CancellationToken cancellationToken)
    {
        var draft = await GetAsync(id, cancellationToken);
        WorkplaceValidation.Validate(draft);
        foreach (var reference in draft.Entries)
            _ = await repository.GetEntryAsync(reference.EntryResourceId, cancellationToken)
                ?? throw new WorkValidationException("workspace_entry_not_published", $"Entry '{reference.EntryResourceId}' is not published.");
        var previous = await repository.GetWorkspaceAsync(id, cancellationToken);
        var published = new WorkplaceWorkspace
        {
            Id = draft.Id, Name = draft.Name,
            DisplayName = draft.DisplayName, Description = draft.Description, Entries = draft.Entries,
            Version = previous is null ? 1 : checked(previous.Version + 1), PublishedAt = timeProvider.GetUtcNow()
        };
        await repository.UpsertWorkspaceAsync(published, cancellationToken);
        return published;
    }
}
