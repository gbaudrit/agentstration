using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Net.Http.Json;
using Agentstration.Resources;
using Agentstration.SystemGraph.Contracts;
using Agentstration.Systems.Contracts;
using Agentstration.Agents;
using Agentstration.SystemOperations.Contracts;

namespace Agentstration.Web.Console;

public interface ISystemExplorerClient
{
    Task<AgentDefinitionCatalog?> DefinitionsAsync(ResourceScopeRef context, Guid uid, CancellationToken ct) => Task.FromResult<AgentDefinitionCatalog?>(null);
    Task<IReadOnlyList<AgentDeploymentView>> DeploymentsAsync(ResourceScopeRef context, ResourceNamespace ns, Guid? agentUid, CancellationToken ct) => Task.FromResult<IReadOnlyList<AgentDeploymentView>>([]);
    Task<AgentDeploymentView> AssignAsync(ResourceScopeRef context, AgentDeploymentAssignmentRequest value, string? etag, bool create, CancellationToken ct) => throw new NotSupportedException();
    Task<SystemOperationalSnapshot> ObservedDeploymentAsync(ResourceScopeRef context, ResourceNamespace ns, string root, CancellationToken ct) => Task.FromResult(new SystemOperationalSnapshot(null, DateTimeOffset.UnixEpoch, "unavailable"));
    Task<SystemGraphPage> ReadAsync(ResourceScopeRef context, ResourceNamespace ns, string? root, string? cursor, CancellationToken ct);
    async IAsyncEnumerable<SystemGraphStreamFrame> StreamAsync(ResourceScopeRef context, ResourceNamespace ns, string? root,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // Compatibility for existing clients supplying a bounded, already authorized page.
        var page = await ReadAsync(context, ns, root, null, ct);
        var epoch = Guid.NewGuid();
        yield return new(epoch, 1, "start", page.ObservedAt, [], [], []);
        yield return new(epoch, 2, "append", page.ObservedAt, page.Nodes, page.Relations, page.Scopes);
        yield return new(epoch, 3, "complete", page.ObservedAt, [], [], [], Truncated: page.HasMore);
    }
    Task<IReadOnlyList<SystemView>> ListAsync(ResourceScopeRef context, ResourceScopeRef owner, ResourceNamespace ns, CancellationToken ct);
    Task<SystemView> CreateAsync(ResourceScopeRef context, CreateSystemRequest value, CancellationToken ct);
    Task<SystemView> RenameAsync(ResourceScopeRef context, SystemView value, string displayName, CancellationToken ct);
    Task<SystemView> AddMemberAsync(ResourceScopeRef context, SystemView value, SystemMember member, CancellationToken ct);
    Task<SystemView> RemoveMemberAsync(ResourceScopeRef context, SystemView value, Guid uid, CancellationToken ct);
    Task DeleteAsync(ResourceScopeRef context, SystemView value, CancellationToken ct);
}

public sealed class SystemExplorerApiClient(HttpClient http, TimeProvider? timeProvider = null) : ISystemExplorerClient
{
    public Task<AgentDefinitionCatalog?> DefinitionsAsync(ResourceScopeRef context, Guid uid, CancellationToken ct) =>
        ApiResponse.ReadAsync<AgentDefinitionCatalog?>(http, "api/agent-definition-catalog?uid=" + uid.ToString("D"), context, ct);
    public Task<IReadOnlyList<AgentDeploymentView>> DeploymentsAsync(ResourceScopeRef context, ResourceNamespace ns, Guid? agentUid, CancellationToken ct) =>
        ApiResponse.ReadAsync<IReadOnlyList<AgentDeploymentView>>(http, "api/agent-deployment-assignments?namespace=" + Escape(ns.Value)
            + (agentUid is null ? "" : "&agentUid=" + agentUid.Value.ToString("D")), context, ct);
    public async Task<AgentDeploymentView> AssignAsync(ResourceScopeRef context, AgentDeploymentAssignmentRequest value, string? etag, bool create, CancellationToken ct)
    {
        using var request = Request(create ? HttpMethod.Post : HttpMethod.Put, "api/agent-deployment-assignments/" + Escape(value.Name), context, value, etag);
        using var response = await http.SendAsync(request, ct); await ApiResponse.EnsureSuccessAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<AgentDeploymentView>(ct))!;
    }
    public Task<SystemOperationalSnapshot> ObservedDeploymentAsync(ResourceScopeRef context, ResourceNamespace ns, string root, CancellationToken ct) =>
        ApiResponse.ReadAsync<SystemOperationalSnapshot>(http, "api/system-operations?namespace=" + Escape(ns.Value) + "&root=" + Escape(root), context, ct);
    public async IAsyncEnumerable<SystemGraphStreamFrame> StreamAsync(ResourceScopeRef context, ResourceNamespace ns, string? root,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var request = Request(HttpMethod.Get, "api/system-graph/stream?namespace=" + Escape(ns.Value)
            + (root is null ? "" : "&root=" + Escape(root)), context, null, null);
        request.Headers.Accept.ParseAdd("application/x-ndjson");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        await ApiResponse.EnsureSuccessAsync(response, ct);
        await using var body = await response.Content.ReadAsStreamAsync(ct);
        using var idle = new CancellationTokenSource(TimeSpan.FromSeconds(30), timeProvider ?? TimeProvider.System);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, idle.Token);
        using var reader = new StreamReader(body);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        while (await reader.ReadLineAsync(lifetime.Token) is { } line)
        {
            idle.CancelAfter(TimeSpan.FromSeconds(30));
            if (string.IsNullOrWhiteSpace(line)) continue;
            yield return JsonSerializer.Deserialize<SystemGraphStreamFrame>(line, options)
                ?? throw new JsonException("The graph stream frame is empty.");
        }
    }

    public Task<SystemGraphPage> ReadAsync(ResourceScopeRef context, ResourceNamespace ns, string? root, string? cursor, CancellationToken ct) =>
        ApiResponse.ReadAsync<SystemGraphPage>(http, $"api/system-graph?namespace={Escape(ns.Value)}&limit=80" +
            (root is null ? "" : $"&root={Escape(root)}") + (cursor is null ? "" : $"&cursor={Escape(cursor)}"), context, ct);
    public Task<IReadOnlyList<SystemView>> ListAsync(ResourceScopeRef context, ResourceScopeRef owner, ResourceNamespace ns, CancellationToken ct) =>
        ApiResponse.ReadAsync<IReadOnlyList<SystemView>>(http, $"api/systems/?scopeRef={Escape(owner.Value)}&namespace={Escape(ns.Value)}", context, ct);
    public Task<SystemView> CreateAsync(ResourceScopeRef context, CreateSystemRequest value, CancellationToken ct) =>
        ChangeAsync(HttpMethod.Post, "api/systems/", context, value, null, ct);
    public Task<SystemView> RenameAsync(ResourceScopeRef context, SystemView value, string displayName, CancellationToken ct) =>
        ChangeAsync(HttpMethod.Put, Path(value), context, new RenameSystemRequest(displayName, value.Description), value.ETag, ct);
    public Task<SystemView> AddMemberAsync(ResourceScopeRef context, SystemView value, SystemMember member, CancellationToken ct) =>
        ChangeAsync(HttpMethod.Post, Path(value, "/members"), context, new AddSystemMemberRequest(member), value.ETag, ct);
    public Task<SystemView> RemoveMemberAsync(ResourceScopeRef context, SystemView value, Guid uid, CancellationToken ct) =>
        ChangeAsync(HttpMethod.Delete, Path(value, $"/members/{uid:D}"), context, null, value.ETag, ct);
    public async Task DeleteAsync(ResourceScopeRef context, SystemView value, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Delete, Path(value), context, null, value.ETag);
        using var response = await http.SendAsync(request, ct);
        await ApiResponse.EnsureSuccessAsync(response, ct);
    }
    private async Task<SystemView> ChangeAsync(HttpMethod method, string path, ResourceScopeRef context, object? body, string? etag, CancellationToken ct)
    {
        using var request = Request(method, path, context, body, etag);
        using var response = await http.SendAsync(request, ct);
        await ApiResponse.EnsureSuccessAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<SystemView>(ct))!;
    }
    private static HttpRequestMessage Request(HttpMethod method, string path, ResourceScopeRef context, object? body, string? etag)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-Agentstration-Context", context.Value);
        if (etag is not null) request.Headers.IfMatch.ParseAdd(etag);
        if (body is not null) request.Content = JsonContent.Create(body);
        return request;
    }
    private static string Path(SystemView value, string child = "") =>
        $"api/systems/{Escape(value.Name)}{child}?scopeRef={Escape(value.ScopeRef.Value)}&namespace={Escape(value.Namespace.Value)}";
    private static string Escape(string value) => Uri.EscapeDataString(value);
}
