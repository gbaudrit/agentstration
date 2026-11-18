using System.Net.Http.Json;
using Agentstration.Reconciliation.Contracts;
using Agentstration.Resources;
using Agentstration.SystemGraph.Contracts;

namespace Agentstration.Web.Console;

public interface IReconciliationClient
{
    Task<SystemGraphChangeResult> CompareAsync(ResourceScopeRef context, ResourceNamespace ns, string from, string to, CancellationToken ct);
    Task<ReconciliationPreview> PreviewAsync(ResourceScopeRef context, ReconciliationPreviewRequest request, CancellationToken ct);
    Task<ReconciliationPreview> RevalidateAsync(ResourceScopeRef context, ReconciliationRevalidationRequest request, CancellationToken ct);
}
public sealed class ReconciliationApiClient(HttpClient http) : IReconciliationClient
{
    public Task<SystemGraphChangeResult> CompareAsync(ResourceScopeRef context, ResourceNamespace ns, string from, string to, CancellationToken ct) =>
        ApiResponse.ReadAsync<SystemGraphChangeResult>(http, $"api/system-graph/changes?namespace={Uri.EscapeDataString(ns.Value)}&from={Uri.EscapeDataString(from)}&to={Uri.EscapeDataString(to)}&maximumDepth=32&maximumPaths=1000", context, ct);
    public Task<ReconciliationPreview> PreviewAsync(ResourceScopeRef context, ReconciliationPreviewRequest request, CancellationToken ct) =>
        PostAsync("api/system-reconciliation/preview", context, request, ct);
    public Task<ReconciliationPreview> RevalidateAsync(ResourceScopeRef context, ReconciliationRevalidationRequest request, CancellationToken ct) =>
        PostAsync("api/system-reconciliation/revalidate", context, request, ct);
    private async Task<ReconciliationPreview> PostAsync(string path, ResourceScopeRef context, object body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-Agentstration-Context", context.Value);
        using var response = await http.SendAsync(request, ct);
        await ApiResponse.EnsureSuccessAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<ReconciliationPreview>(ct))!;
    }
}
