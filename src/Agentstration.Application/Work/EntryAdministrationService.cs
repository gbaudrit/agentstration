using Agentstration.Resources;
using Agentstration.Work;
using Agentstration.Work.Storage.Abstractions;

namespace Agentstration.Application.Work;

public interface IWorkplaceContext
{
    WorkspaceId WorkspaceId { get; }
}

public interface IEntryTargetResolver
{
    Task<EntryResolvedTarget> ResolveAsync(EntryDraft draft, CancellationToken cancellationToken);
    Task<IReadOnlyList<EntryDependency>> GetDependenciesAsync(WorkspaceId workspaceId, EntryId entryId, CancellationToken cancellationToken);
}

public sealed record EntryValidationResult(IReadOnlyList<EntryValidationIssue> Issues)
{
    public bool IsValid => Issues.Count == 0;
}

public sealed record EntryValidationIssue(string Code, string Message);

public sealed class EntryAdministrationService(
    IWorkplaceRepository repository,
    IEntryTargetResolver targetResolver,
    TimeProvider timeProvider,
    IWorkplaceContext context)
{
    public Task<IReadOnlyList<EntryDraft>> ListAsync(CancellationToken cancellationToken) => ListAsync(context.WorkspaceId, cancellationToken);
    public Task<IReadOnlyList<EntryDraft>> ListAsync(WorkspaceId workspaceId, CancellationToken cancellationToken) => repository.ListEntryDraftsAsync(workspaceId, cancellationToken);

    public Task<EntryDraft> GetAsync(EntryId id, CancellationToken cancellationToken) => GetAsync(context.WorkspaceId, id, cancellationToken);

    public async Task<EntryDraft> GetAsync(WorkspaceId workspaceId, EntryId id, CancellationToken cancellationToken) =>
        await repository.GetEntryDraftAsync(workspaceId, id, cancellationToken)
        ?? throw new KeyNotFoundException($"Entry draft '{id}' was not found.");

    public async Task<EntryDraft> SaveAsync(EntryDraft draft, CancellationToken cancellationToken)
    {
        WorkplaceValidation.Validate(draft);
        var current = await repository.GetEntryDraftAsync(draft.WorkspaceId, draft.Id, cancellationToken);
        var saved = draft with
        {
            Revision = current is null ? 1 : checked(current.Revision + 1),
            UpdatedAt = timeProvider.GetUtcNow(),
            PublishedBinding = current?.PublishedBinding
        };
        await repository.UpsertEntryDraftAsync(saved, cancellationToken);
        return saved;
    }

    public async Task<EntryValidationResult> ValidateAsync(WorkspaceId workspaceId, EntryId id, CancellationToken cancellationToken)
    {
        try
        {
            var draft = await GetAsync(workspaceId, id, cancellationToken);
            WorkplaceValidation.Validate(draft);
            _ = await targetResolver.ResolveAsync(draft, cancellationToken);
            return new EntryValidationResult([]);
        }
        catch (WorkValidationException exception)
        {
            return new EntryValidationResult([new EntryValidationIssue(exception.Code, exception.Message)]);
        }
        catch (KeyNotFoundException exception)
        {
            return new EntryValidationResult([new EntryValidationIssue("entry_target_not_found", exception.Message)]);
        }
    }

    public Task<EntryValidationResult> ValidateAsync(EntryId id, CancellationToken cancellationToken) => ValidateAsync(context.WorkspaceId, id, cancellationToken);

    public async Task<EntryResource> PublishAsync(WorkspaceId workspaceId, EntryId id, CancellationToken cancellationToken)
    {
        var draft = await GetAsync(workspaceId, id, cancellationToken);
        WorkplaceValidation.Validate(draft);
        var resolved = await targetResolver.ResolveAsync(draft, cancellationToken);
        var previous = await repository.GetEntryAsync(workspaceId, id, cancellationToken);
        var published = new EntryResource
        {
            WorkspaceId = draft.WorkspaceId,
            Id = draft.Id,
            Name = draft.Name,
            Type = draft.Type,
            ApiVersion = draft.ApiVersion,
            DisplayName = draft.DisplayName,
            Description = draft.Description,
            Presentation = draft.Presentation,
            ResolvedTarget = resolved,
            Behavior = draft.Behavior,
            Version = previous is null ? 1 : checked(previous.Version + 1),
            PublishedAt = timeProvider.GetUtcNow()
        };
        WorkplaceValidation.Validate(published);
        await repository.UpsertEntryAsync(published, cancellationToken);
        await repository.UpsertEntryDraftAsync(draft with { PublishedBinding = draft.Binding }, cancellationToken);
        return published;
    }

    public Task<EntryResource> PublishAsync(EntryId id, CancellationToken cancellationToken) => PublishAsync(context.WorkspaceId, id, cancellationToken);

    public Task<IReadOnlyList<EntryDependency>> GetDependenciesAsync(WorkspaceId workspaceId, EntryId id, CancellationToken cancellationToken) =>
        targetResolver.GetDependenciesAsync(workspaceId, id, cancellationToken);

    public Task<IReadOnlyList<EntryDependency>> GetDependenciesAsync(EntryId id, CancellationToken cancellationToken) => GetDependenciesAsync(context.WorkspaceId, id, cancellationToken);

    public Task DeleteAsync(WorkspaceId workspaceId, EntryId id, CancellationToken cancellationToken) =>
        DeleteAsync(workspaceId, id, removeDashboardReferences: false, closeInteractions: false, cancellationToken);

    public async Task DeleteAsync(
        WorkspaceId workspaceId,
        EntryId id,
        bool removeDashboardReferences,
        bool closeInteractions,
        CancellationToken cancellationToken)
    {
        _ = await repository.GetEntryDraftAsync(workspaceId, id, cancellationToken)
            ?? throw new KeyNotFoundException($"Entry draft '{id}' was not found.");
        var exposedBy = new List<string>();
        var draftedBy = new List<string>();
        exposedBy.AddRange((await repository.ListDashboardsAsync(workspaceId, cancellationToken))
                .Where(dashboard => dashboard.Entries.Any(reference => reference.EntryResourceId == id))
                .Select(dashboard => dashboard.Name));
        draftedBy.AddRange((await repository.ListDashboardDraftsAsync(workspaceId, cancellationToken))
                .Where(dashboard => dashboard.Entries.Any(reference => reference.EntryResourceId == id))
                .Select(dashboard => dashboard.Name));
        if ((exposedBy.Count > 0 || draftedBy.Count > 0) && !removeDashboardReferences)
            throw new WorkValidationException("entry_in_use", $"Entry '{id}' is referenced by a Workplace Dashboard.");
        var interactions = await repository.ListEntryInteractionsAsync(workspaceId, id, cancellationToken);
        var active = interactions.Where(value => value.Status is InteractionStatus.Active or InteractionStatus.Processing or InteractionStatus.WaitingForUser).ToArray();
        if (active.Length > 0)
            throw new WorkValidationException("entry_interactions_active", $"Entry '{id}' has {active.Length} active interaction(s) and cannot be deleted.");
        if (interactions.Any(value => value.Status != InteractionStatus.Closed) && !closeInteractions)
            throw new WorkValidationException("entry_in_use", $"Entry '{id}' has durable interactions and cannot be deleted without closing them.");
        if (removeDashboardReferences)
        {
            var now = timeProvider.GetUtcNow();
            foreach (var dashboard in await repository.ListDashboardsAsync(workspaceId, cancellationToken))
            {
                if (!dashboard.Entries.Any(reference => reference.EntryResourceId == id)) continue;
                await repository.UpsertDashboardAsync(dashboard with { Entries = dashboard.Entries.Where(reference => reference.EntryResourceId != id).ToArray(), Version = checked(dashboard.Version + 1), PublishedAt = now }, cancellationToken);
            }
            foreach (var dashboard in await repository.ListDashboardDraftsAsync(workspaceId, cancellationToken))
            {
                if (!dashboard.Entries.Any(reference => reference.EntryResourceId == id)) continue;
                await repository.UpsertDashboardDraftAsync(dashboard with { Entries = dashboard.Entries.Where(reference => reference.EntryResourceId != id).ToArray(), Revision = checked(dashboard.Revision + 1), UpdatedAt = now }, cancellationToken);
            }
        }
        if (closeInteractions)
        {
            foreach (var interaction in interactions.Where(value => value.Status != InteractionStatus.Closed))
                await repository.SaveInteractionAsync(interaction with { Status = InteractionStatus.Closed, ClosedReason = "entry_uninstalled", LastActivityAt = timeProvider.GetUtcNow(), Version = checked(interaction.Version + 1) }, interaction.Version, cancellationToken);
        }
        await repository.DeleteEntryAsync(workspaceId, id, cancellationToken);
        await repository.DeleteEntryDraftAsync(workspaceId, id, cancellationToken);
    }
}
