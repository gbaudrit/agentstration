using System.Net;
using System.Net.Http.Json;
using Agentstration.ResourcePlanning.Contracts;

namespace Agentstration.Web.Console;

public interface IResourcePlansApiClient
{
    Task<ResourcePlanPage> ListPlansAsync(ResourcePlanStatus? status, int skip, int take, CancellationToken cancellationToken);
    Task<ResourcePlanSnapshot?> GetPlanAsync(Guid id, CancellationToken cancellationToken);
    Task<IReadOnlyList<ResourcePlanActivity>> ListActivitiesAsync(Guid id, CancellationToken cancellationToken);
    Task<ResourcePlanMaterialization> MaterializeAsync(Guid id, CancellationToken cancellationToken);
    Task<ResourceChangeSetPage> ListChangeSetsAsync(Guid planId, int skip, int take, CancellationToken cancellationToken);
    Task<ResourceChangeSetSnapshot> CreateChangeSetAsync(Guid planId, CancellationToken cancellationToken);
    Task<IReadOnlyList<ResourceChangeSetValidation>> ListValidationsAsync(Guid changeSetId, CancellationToken cancellationToken);
    Task<ResourceChangeSetValidation> ValidateChangeSetAsync(Guid changeSetId, CancellationToken cancellationToken);
}

public sealed class ResourcePlansApiClient(HttpClient httpClient) : IResourcePlansApiClient
{
    private const string BasePath = "api/resource-plans";

    public Task<ResourcePlanPage> ListPlansAsync(ResourcePlanStatus? status, int skip, int take, CancellationToken cancellationToken)
    {
        var query = $"?skip={Math.Max(0, skip)}&take={Math.Clamp(take, 1, 200)}";
        if (status is not null) query += $"&status={Uri.EscapeDataString(status.Value.ToString())}";
        return ApiResponse.ReadAsync<ResourcePlanPage>(httpClient, BasePath + query, cancellationToken);
    }

    public async Task<ResourcePlanSnapshot?> GetPlanAsync(Guid id, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync($"{BasePath}/{id:D}", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        return await ReadSnapshotAsync<ResourcePlan, ResourcePlanSnapshot>(response, (value, etag) => new(value, etag), cancellationToken);
    }

    public Task<IReadOnlyList<ResourcePlanActivity>> ListActivitiesAsync(Guid id, CancellationToken cancellationToken) =>
        ReadListAsync<ResourcePlanActivity>($"{BasePath}/{id:D}/activities", cancellationToken);

    public Task<ResourcePlanMaterialization> MaterializeAsync(Guid id, CancellationToken cancellationToken) =>
        PostAsync<ResourcePlanMaterialization>($"{BasePath}/{id:D}/materializations", cancellationToken);

    public Task<ResourceChangeSetPage> ListChangeSetsAsync(Guid planId, int skip, int take, CancellationToken cancellationToken) =>
        ApiResponse.ReadAsync<ResourceChangeSetPage>(httpClient,
            $"{BasePath}/change-sets?planId={planId:D}&skip={Math.Max(0, skip)}&take={Math.Clamp(take, 1, 200)}", cancellationToken);

    public async Task<ResourceChangeSetSnapshot> CreateChangeSetAsync(Guid planId, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsync($"{BasePath}/{planId:D}/change-sets", null, cancellationToken);
        return await ReadSnapshotAsync<ResourceChangeSet, ResourceChangeSetSnapshot>(response, (value, etag) => new(value, etag), cancellationToken);
    }

    public Task<IReadOnlyList<ResourceChangeSetValidation>> ListValidationsAsync(Guid changeSetId, CancellationToken cancellationToken) =>
        ReadListAsync<ResourceChangeSetValidation>($"{BasePath}/change-sets/{changeSetId:D}/validations", cancellationToken);

    public Task<ResourceChangeSetValidation> ValidateChangeSetAsync(Guid changeSetId, CancellationToken cancellationToken) =>
        PostAsync<ResourceChangeSetValidation>($"{BasePath}/change-sets/{changeSetId:D}/validations", cancellationToken);

    private async Task<IReadOnlyList<T>> ReadListAsync<T>(string path, CancellationToken cancellationToken) =>
        await ApiResponse.ReadAsync<T[]>(httpClient, path, cancellationToken);

    private async Task<T> PostAsync<T>(string path, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsync(path, null, cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken)
            ?? throw new AgentstrationApiException("Agentstration API returned an empty Resource Planning response.", Guid.NewGuid().ToString("N"));
    }

    private static async Task<TSnapshot> ReadSnapshotAsync<TValue, TSnapshot>(
        HttpResponseMessage response, Func<TValue, string, TSnapshot> create, CancellationToken cancellationToken)
    {
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
        var value = await response.Content.ReadFromJsonAsync<TValue>(cancellationToken)
            ?? throw new AgentstrationApiException("Agentstration API returned an empty Resource Planning response.", Guid.NewGuid().ToString("N"));
        var etag = response.Headers.ETag?.ToString();
        if (string.IsNullOrWhiteSpace(etag))
            throw new AgentstrationApiException("Agentstration API did not return a Resource Planning ETag.", Guid.NewGuid().ToString("N"));
        return create(value, etag);
    }
}
