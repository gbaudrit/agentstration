using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Contracts;
using Agentstration.Resources;

namespace Agentstration.Web.Console;

public interface ITriggerApiClient
{
    Task<IReadOnlyList<TriggerResource>> ListAsync(CancellationToken cancellationToken);
    Task<ResourceSnapshot<TriggerResource>?> GetAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken);
    Task<ResourceSnapshot<TriggerResource>> SaveAsync(TriggerResource resource, string? etag, CancellationToken cancellationToken);
    Task DeleteAsync(ResourceNamespace @namespace, string name, string etag, CancellationToken cancellationToken);
    Task<TriggerOccurrence> RunNowAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken);
    Task<IReadOnlyList<TriggerOccurrence>> ListHistoryAsync(ResourceNamespace @namespace, string name, int take, CancellationToken cancellationToken);
    Task<IReadOnlyList<DateTimeOffset>> PreviewScheduleAsync(TriggerSchedule schedule, int count, CancellationToken cancellationToken);
}

public sealed class TriggerApiClient(HttpClient httpClient) : ITriggerApiClient
{
    public async Task<IReadOnlyList<TriggerResource>> ListAsync(CancellationToken cancellationToken) =>
        await ApiResponse.ReadAsync<TriggerResource[]>(httpClient, "api/triggers", cancellationToken);

    public async Task<ResourceSnapshot<TriggerResource>?> GetAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(Path(@namespace, name), cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        return await ReadResourceAsync(response, cancellationToken);
    }

    public async Task<ResourceSnapshot<TriggerResource>> SaveAsync(TriggerResource resource, string? etag, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, Path(resource.Namespace, resource.Name))
        {
            Content = JsonContent.Create(resource)
        };
        if (!string.IsNullOrWhiteSpace(etag)) request.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(etag));
        using var response = await httpClient.SendAsync(request, cancellationToken);
        return await ReadResourceAsync(response, cancellationToken);
    }

    public async Task DeleteAsync(ResourceNamespace @namespace, string name, string etag, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, Path(@namespace, name));
        request.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(etag));
        using var response = await httpClient.SendAsync(request, cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task<TriggerOccurrence> RunNowAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsync($"{Path(@namespace, name)}/run", null, cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<TriggerOccurrence>(cancellationToken)
            ?? throw new AgentstrationApiException("Agentstration API returned an empty Trigger occurrence.", Guid.NewGuid().ToString("N"));
    }

    public async Task<IReadOnlyList<TriggerOccurrence>> ListHistoryAsync(ResourceNamespace @namespace, string name, int take, CancellationToken cancellationToken) =>
        await ApiResponse.ReadAsync<TriggerOccurrence[]>(httpClient, $"{Path(@namespace, name)}/occurrences?take={Math.Clamp(take, 1, 200)}", cancellationToken);

    public async Task<IReadOnlyList<DateTimeOffset>> PreviewScheduleAsync(TriggerSchedule schedule, int count, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync(
            "api/triggers/schedule-preview",
            new TriggerSchedulePreviewRequest(schedule, Math.Clamp(count, 1, 20)),
            cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
        return (await response.Content.ReadFromJsonAsync<TriggerSchedulePreviewResponse>(cancellationToken)
            ?? throw new AgentstrationApiException("Agentstration API returned an empty Trigger schedule preview.", Guid.NewGuid().ToString("N"))).Occurrences;
    }

    private static async Task<ResourceSnapshot<TriggerResource>> ReadResourceAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
        var value = await response.Content.ReadFromJsonAsync<TriggerResource>(cancellationToken)
            ?? throw new AgentstrationApiException("Agentstration API returned an empty Trigger.", Guid.NewGuid().ToString("N"));
        var etag = response.Headers.ETag?.ToString();
        if (string.IsNullOrWhiteSpace(etag))
            throw new AgentstrationApiException("Agentstration API did not return the Trigger ETag.", Guid.NewGuid().ToString("N"));
        return new(value, etag);
    }

    private static string Path(ResourceNamespace @namespace, string name) => @namespace.IsDefault
        ? $"api/triggers/{Uri.EscapeDataString(name)}"
        : $"api/namespaces/{Uri.EscapeDataString(@namespace.Value)}/triggers/{Uri.EscapeDataString(name)}";
}
