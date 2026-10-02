using System.Net.Http.Json;
using Agentstration.Artifacts.Contracts;

namespace Agentstration.Web.Console;

public interface IArtifactsClient
{
    Task<IReadOnlyList<StagedArtifactView>> GetStagedAsync(CancellationToken cancellationToken = default);
    Task<StagedArtifactView?> GetStagedAsync(StagedArtifactId id, CancellationToken cancellationToken = default);
    Task<ArtifactContentChunk> ReadContentAsync(StagedArtifactId id, long offset, int length, CancellationToken cancellationToken = default);
    Task<StagedArtifactView> ExtendRetentionAsync(StagedArtifactId id, DateTimeOffset expiresAt, CancellationToken cancellationToken = default);
    Task PurgeAsync(StagedArtifactId id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<FlowRunArtifactResource>> GetDurableAsync(CancellationToken cancellationToken = default);
}

public sealed class ArtifactsApiClient(HttpClient httpClient) : IArtifactsClient
{
    public Task<IReadOnlyList<StagedArtifactView>> GetStagedAsync(CancellationToken cancellationToken = default) =>
        ApiResponse.ReadAsync<IReadOnlyList<StagedArtifactView>>(httpClient, "api/artifacts/staged", cancellationToken);

    public async Task<StagedArtifactView?> GetStagedAsync(StagedArtifactId id, CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync(Path(id), cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<StagedArtifactView>(cancellationToken);
    }

    public Task<ArtifactContentChunk> ReadContentAsync(StagedArtifactId id, long offset, int length,
        CancellationToken cancellationToken = default) => ApiResponse.ReadAsync<ArtifactContentChunk>(httpClient,
            $"{Path(id)}/content?offset={offset}&length={length}", cancellationToken);

    public async Task<StagedArtifactView> ExtendRetentionAsync(StagedArtifactId id, DateTimeOffset expiresAt,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsJsonAsync($"{Path(id)}/retention",
            new ExtendArtifactRetentionRequest(expiresAt), cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
        return (await response.Content.ReadFromJsonAsync<StagedArtifactView>(cancellationToken))!;
    }

    public async Task PurgeAsync(StagedArtifactId id, CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.DeleteAsync(Path(id), cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
    }

    public Task<IReadOnlyList<FlowRunArtifactResource>> GetDurableAsync(CancellationToken cancellationToken = default) =>
        ApiResponse.ReadAsync<IReadOnlyList<FlowRunArtifactResource>>(httpClient,
            "api/artifacts/flow-run-artifacts", cancellationToken);

    private static string Path(StagedArtifactId id) => $"api/artifacts/staged/{id}";
}
