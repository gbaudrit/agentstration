using System.Net.Http.Json;
using Agentstration.Resources;
using Agentstration.SystemGraph.Contracts;
using Agentstration.Systems.Contracts;

namespace Agentstration.Web.Console;

public interface ISystemExplorerClient
{
    Task<SystemGraphPage> ReadAsync(ResourceScopeRef context, ResourceNamespace ns, string? root, string? cursor, CancellationToken ct);
    Task<IReadOnlyList<SystemView>> ListAsync(ResourceScopeRef context, ResourceScopeRef owner, ResourceNamespace ns, CancellationToken ct);
    Task<SystemView> CreateAsync(ResourceScopeRef context, CreateSystemRequest value, CancellationToken ct);
    Task<SystemView> RenameAsync(ResourceScopeRef context, SystemView value, string displayName, CancellationToken ct);
    Task<SystemView> AddMemberAsync(ResourceScopeRef context, SystemView value, SystemMember member, CancellationToken ct);
    Task<SystemView> RemoveMemberAsync(ResourceScopeRef context, SystemView value, Guid uid, CancellationToken ct);
    Task DeleteAsync(ResourceScopeRef context, SystemView value, CancellationToken ct);
}

public sealed class SystemExplorerApiClient(HttpClient http) : ISystemExplorerClient
{
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
