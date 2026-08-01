using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;
using Agentstration.Domain;

namespace Agentstration.Application.Workflows;

public sealed partial class ContentProcessingWorkflow(
    IPlatformStore store,
    IIntentRouter router,
    IAgentRuntime agentRuntime,
    IMemoryStore memoryStore,
    IEventBus eventBus,
    TimeProvider timeProvider)
{
    public static readonly ActivitySource ActivitySource = new("Agentstration.Workflows");

    public async Task ExecuteAsync(WorkspaceId workspaceId, ItemId itemId, CancellationToken cancellationToken)
    {
        using var activity = ActivitySource.StartActivity("workflow.content-processing");
        activity?.SetTag("workspace.id", workspaceId.ToString());
        activity?.SetTag("item.id", itemId.ToString());
        var item = await store.GetItemAsync(workspaceId, itemId, cancellationToken) ?? throw new InvalidOperationException("Queued item no longer exists.");
        var raw = await store.GetRawContentAsync(workspaceId, itemId, cancellationToken) ?? throw new InvalidOperationException("Raw content no longer exists.");
        await store.SetItemStatusAsync(workspaceId, itemId, ItemStatus.Processing, null, cancellationToken);

        try
        {
            var decision = await router.RouteAsync(new RoutingContext(workspaceId, item, raw), cancellationToken);
            activity?.SetTag("routing.route", decision.Route);
            var normalizedText = Normalize(raw.Value);
            var normalized = new NormalizedContent(itemId, workspaceId, normalizedText, timeProvider.GetUtcNow());
            await store.AddNormalizedContentAsync(normalized, cancellationToken);
            await eventBus.PublishAsync(new ItemNormalized(workspaceId, itemId, timeProvider.GetUtcNow()), cancellationToken);

            if (!decision.StoreOnly)
            {
                var result = await agentRuntime.RunAsync(new AgentExecutionRequest(workspaceId, itemId, normalizedText), cancellationToken);
                await memoryStore.AddAsync(new MemoryEntry(Guid.NewGuid(), workspaceId, itemId, null, "summary", result.Summary, result.Categories, timeProvider.GetUtcNow()), cancellationToken);
            }

            await store.SetItemStatusAsync(workspaceId, itemId, ItemStatus.Processed, null, cancellationToken);
            await eventBus.PublishAsync(new ItemProcessed(workspaceId, itemId, timeProvider.GetUtcNow()), cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await store.SetItemStatusAsync(workspaceId, itemId, ItemStatus.Failed, exception.Message, cancellationToken);
            throw;
        }
    }

    private static string Normalize(string value)
    {
        var decoded = WebUtility.HtmlDecode(HtmlTags().Replace(value, " "));
        return Whitespace().Replace(decoded, " ").Trim();
    }

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex HtmlTags();

    [GeneratedRegex("\\s+")]
    private static partial Regex Whitespace();
}
