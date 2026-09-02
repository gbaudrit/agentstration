using System.Net.Http.Headers;
using System.Net.Http.Json;
using Agentstration.Flow;
using Agentstration.Flow.Contracts;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Contracts;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Agentstration.Runtime.Contracts;
using Agentstration.Work.Contracts;

namespace Agentstration.Web.Console;

public enum CleanupResourceKind { RuntimeRun, FlowRun, Entry, Flow, Agent }

public sealed record CleanupCandidate(
    CleanupResourceKind Kind,
    string Id,
    string DisplayName,
    ResourceNamespace Namespace,
    string Status,
    DateTimeOffset UpdatedAt,
    string? Detail = null)
{
    public string Key => $"{Kind}:{Namespace.Value}:{Id}";
}

public sealed record CleanupInventory(
    IReadOnlyList<CleanupCandidate> Runs,
    IReadOnlyList<CleanupCandidate> Entries,
    IReadOnlyList<CleanupCandidate> Flows,
    IReadOnlyList<CleanupCandidate> Agents)
{
    public IReadOnlyList<CleanupCandidate> All => [.. Runs, .. Entries, .. Flows, .. Agents];
}

public sealed record CleanupEntryOptions(bool RemoveDashboardReferences, bool CloseInteractions);

public interface ICleanupApiClient
{
    Task<CleanupInventory> GetInventoryAsync(CancellationToken cancellationToken);
    Task DeleteAsync(CleanupCandidate candidate, CleanupEntryOptions entryOptions, CancellationToken cancellationToken);
}

public sealed class CleanupApiClient(IHttpClientFactory httpClientFactory) : ICleanupApiClient
{
    public const string ManagementClient = "Agentstration.Cleanup.Management";
    public const string RuntimeClient = "Agentstration.Cleanup.Runtime";
    public const string FlowClient = "Agentstration.Cleanup.Flow";
    public const string WorkClient = "Agentstration.Cleanup.Work";

    public async Task<CleanupInventory> GetInventoryAsync(CancellationToken cancellationToken)
    {
        var runtimeRunsTask = GetRuntimeRunsAsync(cancellationToken);
        var flowRunsTask = GetFlowRunsAsync(cancellationToken);
        var entriesTask = GetEntriesAsync(cancellationToken);
        var flowsTask = GetFlowsAsync(cancellationToken);
        var agentsTask = GetAgentsAsync(cancellationToken);
        await Task.WhenAll(runtimeRunsTask, flowRunsTask, entriesTask, flowsTask, agentsTask);

        var runs = (await runtimeRunsTask)
            .Concat(await flowRunsTask)
            .OrderByDescending(candidate => candidate.UpdatedAt)
            .ToArray();
        return new(runs, await entriesTask, await flowsTask, await agentsTask);
    }

    public Task DeleteAsync(CleanupCandidate candidate, CleanupEntryOptions entryOptions, CancellationToken cancellationToken) =>
        candidate.Kind switch
        {
            CleanupResourceKind.RuntimeRun => DeleteWithCurrentETagAsync(RuntimeClient, RuntimeRunPath(candidate.Id), cancellationToken),
            CleanupResourceKind.FlowRun => DeleteWithCurrentETagAsync(FlowClient, FlowRunPath(candidate.Id), cancellationToken),
            CleanupResourceKind.Entry => DeleteEntryAsync(candidate, entryOptions, cancellationToken),
            CleanupResourceKind.Flow => DeleteWithCurrentETagAsync(FlowClient, FlowPath(candidate.Namespace, candidate.Id), cancellationToken),
            CleanupResourceKind.Agent => DeleteWithCurrentETagAsync(ManagementClient, AgentPath(candidate.Namespace, candidate.Id), cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(candidate))
        };

    private async Task<IReadOnlyList<CleanupCandidate>> GetRuntimeRunsAsync(CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(RuntimeClient);
        var values = await ReadPagesAsync<RuntimeRun, RuntimeRunPageResponse>(
            client,
            "api/runtime/runs?top=1000",
            page => page.Value,
            page => page.NextLink,
            cancellationToken);
        return values
            .Where(run => run.Status.State.IsTerminal())
            .Select(run => new CleanupCandidate(
                CleanupResourceKind.RuntimeRun,
                run.Id,
                run.Name,
                run.Properties.Agent.Namespace,
                run.Status.State.ToString(),
                run.Status.CompletedAt ?? run.Status.CreatedAt,
                run.Properties.Agent.ResourceId))
            .ToArray();
    }

    private async Task<IReadOnlyList<CleanupCandidate>> GetFlowRunsAsync(CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(FlowClient);
        var values = await ReadPagesAsync<FlowRun, FlowRunPageResponse>(
            client,
            "api/flowRuns?top=200",
            page => page.Value,
            page => page.NextLink,
            cancellationToken);
        return values
            .Where(run => run.Status.IsTerminal())
            .Select(run => new CleanupCandidate(
                CleanupResourceKind.FlowRun,
                run.Id,
                run.FlowId.Value,
                run.FlowId.Namespace,
                run.Status.ToString(),
                run.CompletedAt ?? run.CreatedAt,
                run.FlowVersion))
            .ToArray();
    }

    private async Task<IReadOnlyList<CleanupCandidate>> GetEntriesAsync(CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(WorkClient);
        var values = await ApiResponse.ReadAsync<EntryDraftResponse[]>(client, "api/management/entries", cancellationToken);
        return values
            .Select(entry => new CleanupCandidate(
                CleanupResourceKind.Entry,
                entry.Value.Id.Value,
                entry.Value.DisplayName,
                entry.Value.Id.Namespace,
                entry.Published is null ? "Draft" : "Published",
                entry.Value.UpdatedAt,
                entry.Value.Binding.ResourceId))
            .OrderBy(candidate => candidate.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private async Task<IReadOnlyList<CleanupCandidate>> GetFlowsAsync(CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(FlowClient);
        var values = await ReadPagesAsync<FlowSummaryResponse, FlowPageResponse>(
            client,
            "api/flows?allNamespaces=true&top=200",
            page => page.Value,
            page => page.NextLink,
            cancellationToken);
        return values
            .Select(flow => new CleanupCandidate(
                CleanupResourceKind.Flow,
                flow.Id,
                flow.DisplayName ?? flow.Name,
                flow.Namespace,
                flow.Enabled ? "Active" : "Disabled",
                flow.UpdatedAt,
                flow.ActiveVersion ?? flow.Version))
            .OrderBy(candidate => candidate.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private async Task<IReadOnlyList<CleanupCandidate>> GetAgentsAsync(CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(ManagementClient);
        var values = await ReadPagesAsync<AgentResource, PagedResponse<AgentResource>>(
            client,
            "api/agents?allNamespaces=true&top=1000",
            page => page.Value,
            page => page.NextLink,
            cancellationToken);
        return values
            .Select(agent => new CleanupCandidate(
                CleanupResourceKind.Agent,
                agent.Name,
                agent.Definition.DisplayName,
                agent.Namespace,
                agent.Status.ProvisioningState.ToString(),
                agent.Status.Conditions.Select(condition => condition.LastTransitionTime).OfType<DateTimeOffset>().DefaultIfEmpty(DateTimeOffset.MinValue).Max(),
                agent.Definition.ModelProfile.ResourceId))
            .OrderBy(candidate => candidate.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private async Task DeleteEntryAsync(CleanupCandidate candidate, CleanupEntryOptions options, CancellationToken cancellationToken)
    {
        var query = $"?removeDashboardReferences={options.RemoveDashboardReferences.ToString().ToLowerInvariant()}&closeInteractions={options.CloseInteractions.ToString().ToLowerInvariant()}";
        using var response = await httpClientFactory.CreateClient(WorkClient)
            .DeleteAsync(EntryPath(candidate.Namespace, candidate.Id) + query, cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
    }

    private async Task DeleteWithCurrentETagAsync(string clientName, string path, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(clientName);
        using var current = await client.GetAsync(path, cancellationToken);
        await ApiResponse.EnsureSuccessAsync(current, cancellationToken);
        var etag = current.Headers.ETag?.ToString();
        if (string.IsNullOrWhiteSpace(etag))
            throw new AgentstrationApiException("The resource did not expose an ETag required for deletion.", Guid.NewGuid().ToString("N"));

        using var request = new HttpRequestMessage(HttpMethod.Delete, path);
        request.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(etag));
        using var response = await client.SendAsync(request, cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
    }

    private static async Task<IReadOnlyList<TItem>> ReadPagesAsync<TItem, TPage>(
        HttpClient client,
        string initialPath,
        Func<TPage, IReadOnlyList<TItem>> values,
        Func<TPage, string?> nextLink,
        CancellationToken cancellationToken)
    {
        var result = new List<TItem>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        string? path = initialPath;
        while (path is not null)
        {
            path = NormalizePageLink(path);
            if (!visited.Add(path))
                throw new AgentstrationApiException("The API returned a repeated pagination link.", Guid.NewGuid().ToString("N"));
            var page = await ApiResponse.ReadAsync<TPage>(client, path, cancellationToken);
            result.AddRange(values(page));
            path = string.IsNullOrWhiteSpace(nextLink(page)) ? null : nextLink(page);
        }
        return result;
    }

    private static string NormalizePageLink(string link)
    {
        var candidate = link.Trim();
        if (candidate.StartsWith("//", StringComparison.Ordinal))
            throw new AgentstrationApiException("The API returned an invalid pagination link.", Guid.NewGuid().ToString("N"));
        var normalized = candidate.TrimStart('/');
        if (!normalized.StartsWith("api/", StringComparison.Ordinal))
            throw new AgentstrationApiException("The API returned an invalid pagination link.", Guid.NewGuid().ToString("N"));
        return normalized;
    }

    private static string RuntimeRunPath(string id) => $"api/runtime/runs/{Uri.EscapeDataString(id)}";
    private static string FlowRunPath(string id) => $"api/flowRuns/{Uri.EscapeDataString(id)}";
    private static string AgentPath(ResourceNamespace @namespace, string id) => @namespace.IsDefault
        ? $"api/agents/{Uri.EscapeDataString(id)}"
        : $"api/namespaces/{Uri.EscapeDataString(@namespace.Value)}/agents/{Uri.EscapeDataString(id)}";
    private static string FlowPath(ResourceNamespace @namespace, string id) => @namespace.IsDefault
        ? $"api/flows/{Uri.EscapeDataString(id)}"
        : $"api/namespaces/{Uri.EscapeDataString(@namespace.Value)}/flows/{Uri.EscapeDataString(id)}";
    private static string EntryPath(ResourceNamespace @namespace, string id) => @namespace.IsDefault
        ? $"api/management/entries/{Uri.EscapeDataString(id)}"
        : $"api/namespaces/{Uri.EscapeDataString(@namespace.Value)}/management/entries/{Uri.EscapeDataString(id)}";
}
