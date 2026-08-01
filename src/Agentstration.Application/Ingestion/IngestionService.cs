using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Agentstration.Application.Common;
using Agentstration.Contracts;
using Agentstration.Domain;

namespace Agentstration.Application.Ingestion;

public sealed class IngestionService(IPlatformStore store, IEventBus eventBus, IContentSourceReader sourceReader, TimeProvider timeProvider)
{
    public static readonly ActivitySource ActivitySource = new("Agentstration.Ingestion");

    public async Task<Result<IngestItemResponse>> IngestAsync(
        WorkspaceId workspaceId,
        InboxId inboxId,
        string? text,
        string? url,
        string? externalId,
        string mediaType,
        CancellationToken cancellationToken)
    {
        using var activity = ActivitySource.StartActivity("ingest.item");
        activity?.SetTag("workspace.id", workspaceId.ToString());
        activity?.SetTag("inbox.id", inboxId.ToString());

        if (await store.GetInboxAsync(workspaceId, inboxId, cancellationToken) is null)
        {
            return Result<IngestItemResponse>.Failure("inbox.not_found", "Inbox was not found in this workspace.");
        }

        string raw;
        string? sourceUrl = null;
        if (!string.IsNullOrWhiteSpace(url))
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                return Result<IngestItemResponse>.Failure("validation.url", "Only absolute HTTP and HTTPS URLs are accepted.");
            }

            raw = await sourceReader.ReadUrlAsync(uri, cancellationToken);
            sourceUrl = uri.ToString();
            mediaType = "text/html";
        }
        else if (!string.IsNullOrWhiteSpace(text))
        {
            raw = text;
        }
        else
        {
            return Result<IngestItemResponse>.Failure("validation.content", "Text or URL is required.");
        }

        if (Encoding.UTF8.GetByteCount(raw) > 2 * 1024 * 1024)
        {
            return Result<IngestItemResponse>.Failure("content.too_large", "Content exceeds the 2 MiB MVP limit.");
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
        var duplicate = await store.FindItemByHashAsync(workspaceId, inboxId, hash, cancellationToken);
        if (duplicate is not null)
        {
            return Result<IngestItemResponse>.Success(new IngestItemResponse(duplicate.Id, duplicate.Status.ToString().ToLowerInvariant(), true));
        }

        var now = timeProvider.GetUtcNow();
        var item = new Item(ItemId.New(), workspaceId, inboxId, mediaType, hash, externalId, ItemStatus.Queued, now);
        var content = new RawContent(item.Id, workspaceId, raw, mediaType, sourceUrl, now);
        await store.AddItemAsync(item, content, cancellationToken);
        await store.AddAuditEntryAsync(new AuditEntry(Guid.NewGuid(), workspaceId, "item.received", item.Id.ToString(), now), cancellationToken);
        await eventBus.PublishAsync(new ItemReceived(workspaceId, item.Id, now), cancellationToken);
        return Result<IngestItemResponse>.Success(new IngestItemResponse(item.Id, "queued", false));
    }

    public async Task<Result<ItemDetails>> GetAsync(WorkspaceId workspaceId, ItemId itemId, CancellationToken cancellationToken)
    {
        var item = await store.GetItemAsync(workspaceId, itemId, cancellationToken);
        var raw = await store.GetRawContentAsync(workspaceId, itemId, cancellationToken);
        if (item is null || raw is null)
        {
            return Result<ItemDetails>.Failure("item.not_found", "Item was not found in this workspace.");
        }

        var normalized = await store.GetNormalizedContentAsync(workspaceId, itemId, cancellationToken);
        var memory = await store.GetItemMemoryAsync(workspaceId, itemId, cancellationToken);
        return Result<ItemDetails>.Success(new ItemDetails(item, raw, normalized, memory));
    }
}

public sealed class ItemReceivedHandler(IItemProcessingQueue queue) : IEventHandler<ItemReceived>
{
    public ValueTask HandleCoreAsync(ItemReceived domainEvent, CancellationToken cancellationToken) =>
        queue.EnqueueAsync(domainEvent.WorkspaceId, domainEvent.ItemId, cancellationToken);

    public async Task HandleAsync(ItemReceived domainEvent, CancellationToken cancellationToken) =>
        await HandleCoreAsync(domainEvent, cancellationToken);
}
