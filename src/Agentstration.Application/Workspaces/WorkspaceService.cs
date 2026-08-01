using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Agentstration.Application.Common;
using Agentstration.Contracts;
using Agentstration.Domain;

namespace Agentstration.Application.Workspaces;

public sealed partial class WorkspaceService(IPlatformStore store, TimeProvider timeProvider)
{
    public async Task<Result<Workspace>> CreateAsync(string name, CancellationToken cancellationToken)
    {
        name = name.Trim();
        if (name.Length is < 2 or > 100)
        {
            return Result<Workspace>.Failure("validation.name", "Workspace name must contain between 2 and 100 characters.");
        }

        var workspace = new Workspace(WorkspaceId.New(), name, timeProvider.GetUtcNow());
        await store.AddWorkspaceAsync(workspace, cancellationToken);
        return Result<Workspace>.Success(workspace);
    }

    public Task<IReadOnlyList<Workspace>> ListAsync(CancellationToken cancellationToken) => store.ListWorkspacesAsync(cancellationToken);

    public async Task<Result<InboxCreatedResponse>> CreateInboxAsync(WorkspaceId workspaceId, CreateInboxRequest request, CancellationToken cancellationToken)
    {
        if (await store.GetWorkspaceAsync(workspaceId, cancellationToken) is null)
        {
            return Result<InboxCreatedResponse>.Failure("workspace.not_found", "Workspace was not found.");
        }

        var name = request.Name.Trim();
        if (name.Length is < 2 or > 100)
        {
            return Result<InboxCreatedResponse>.Failure("validation.name", "Inbox name must contain between 2 and 100 characters.");
        }

        var slug = SlugCharacters().Replace((request.Slug ?? name).Trim().ToLowerInvariant(), "-").Trim('-');
        if (string.IsNullOrWhiteSpace(slug))
        {
            return Result<InboxCreatedResponse>.Failure("validation.slug", "Inbox slug is invalid.");
        }

        var apiKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(apiKey))).ToLowerInvariant();
        var inbox = new Inbox(InboxId.New(), workspaceId, name, slug, request.Description?.Trim() ?? string.Empty, hash, timeProvider.GetUtcNow());
        await store.AddInboxAsync(inbox, cancellationToken);
        await store.AddAuditEntryAsync(new AuditEntry(Guid.NewGuid(), workspaceId, "inbox.created", inbox.Id.ToString(), timeProvider.GetUtcNow()), cancellationToken);
        return Result<InboxCreatedResponse>.Success(new InboxCreatedResponse(inbox, apiKey));
    }

    public Task<IReadOnlyList<Inbox>> ListInboxesAsync(WorkspaceId workspaceId, CancellationToken cancellationToken) => store.ListInboxesAsync(workspaceId, cancellationToken);

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex SlugCharacters();
}
