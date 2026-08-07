using Agentstration.Work;
using Agentstration.Work.Storage.Abstractions;

namespace Agentstration.Application.Work;

public interface IEntryTargetResolver
{
    Task<EntryResolvedTarget> ResolveAsync(EntryDraft draft, CancellationToken cancellationToken);
    Task<IReadOnlyList<EntryDependency>> GetDependenciesAsync(EntryId entryId, CancellationToken cancellationToken);
}

public sealed record EntryValidationResult(IReadOnlyList<EntryValidationIssue> Issues)
{
    public bool IsValid => Issues.Count == 0;
}

public sealed record EntryValidationIssue(string Code, string Message);

public sealed class EntryAdministrationService(
    IWorkplaceRepository repository,
    IEntryTargetResolver targetResolver,
    TimeProvider timeProvider)
{
    public Task<IReadOnlyList<EntryDraft>> ListAsync(CancellationToken cancellationToken) => repository.ListEntryDraftsAsync(cancellationToken);

    public async Task<EntryDraft> GetAsync(EntryId id, CancellationToken cancellationToken) =>
        await repository.GetEntryDraftAsync(id, cancellationToken)
        ?? throw new KeyNotFoundException($"Entry draft '{id}' was not found.");

    public async Task<EntryDraft> SaveAsync(EntryDraft draft, CancellationToken cancellationToken)
    {
        WorkplaceValidation.Validate(draft);
        var current = await repository.GetEntryDraftAsync(draft.Id, cancellationToken);
        var saved = draft with
        {
            Revision = current is null ? 1 : checked(current.Revision + 1),
            UpdatedAt = timeProvider.GetUtcNow(),
            PublishedBinding = current?.PublishedBinding
        };
        await repository.UpsertEntryDraftAsync(saved, cancellationToken);
        return saved;
    }

    public async Task<EntryValidationResult> ValidateAsync(EntryId id, CancellationToken cancellationToken)
    {
        try
        {
            var draft = await GetAsync(id, cancellationToken);
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

    public async Task<EntryResource> PublishAsync(EntryId id, CancellationToken cancellationToken)
    {
        var draft = await GetAsync(id, cancellationToken);
        WorkplaceValidation.Validate(draft);
        var resolved = await targetResolver.ResolveAsync(draft, cancellationToken);
        var previous = await repository.GetEntryAsync(id, cancellationToken);
        var published = new EntryResource
        {
            Id = draft.Id,
            Name = draft.Name,
            Type = draft.Type,
            ApiVersion = draft.ApiVersion,
            ResourceGroup = draft.ResourceGroup,
            Location = draft.Location,
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

    public Task<IReadOnlyList<EntryDependency>> GetDependenciesAsync(EntryId id, CancellationToken cancellationToken) =>
        targetResolver.GetDependenciesAsync(id, cancellationToken);
}
