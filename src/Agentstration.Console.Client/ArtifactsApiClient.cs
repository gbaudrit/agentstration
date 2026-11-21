using System.Net.Http.Json;
using Agentstration.Artifacts.Contracts;
using Agentstration.Resources;

namespace Agentstration.Web.Console;

public interface IArtifactsClient
{
    Task<StagedArtifactSavedSummary> GetStagedAtAddressAsync(ScopedResourceAddress address, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    Task<FlowRunArtifactResource> GetDurableAtAddressAsync(ScopedResourceAddress address, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    Task<ArtifactRecordContent> ReadRecordAsync(FlowRunArtifactId id, string memberId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    Task<IReadOnlyList<StagedArtifactView>> GetStagedAsync(CancellationToken cancellationToken = default);
    Task<StagedArtifactView?> GetStagedAsync(StagedArtifactId id, CancellationToken cancellationToken = default);
    Task<ArtifactContentChunk> ReadContentAsync(StagedArtifactId id, long offset, int length, CancellationToken cancellationToken = default);
    string GetDownloadUrl(StagedArtifactId id);
    Task<StagedArtifactView> ExtendRetentionAsync(StagedArtifactId id, DateTimeOffset expiresAt, CancellationToken cancellationToken = default);
    Task PurgeAsync(StagedArtifactId id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<FlowRunArtifactResource>> GetDurableAsync(CancellationToken cancellationToken = default);
    Task<FlowRunArtifactResource?> GetDurableAsync(FlowRunArtifactId id, CancellationToken cancellationToken = default);
    Task<FlowRunArtifactMaterialization> StartMaterializationAsync(FlowRunArtifactId id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<FlowRunArtifactMaterialization>> GetMaterializationsAsync(FlowRunArtifactId id, CancellationToken cancellationToken = default);
    Task<FlowRunArtifactMaterialization?> GetMaterializationAsync(FlowRunArtifactId id, string flowRunId, CancellationToken cancellationToken = default);
}

public sealed class ArtifactsApiClient(HttpClient httpClient) : IArtifactsClient
{
    public Task<StagedArtifactSavedSummary> GetStagedAtAddressAsync(ScopedResourceAddress address, CancellationToken cancellationToken = default) =>
        ApiResponse.ReadAsync<StagedArtifactSavedSummary>(httpClient, ExactPath(address, ArtifactResourceKinds.StagedArtifact, "staged"), cancellationToken);
    public Task<FlowRunArtifactResource> GetDurableAtAddressAsync(ScopedResourceAddress address, CancellationToken cancellationToken = default) =>
        ApiResponse.ReadAsync<FlowRunArtifactResource>(httpClient, ExactPath(address, ArtifactResourceKinds.FlowRunArtifact, "flow-run-artifacts"), cancellationToken);
    private static string ExactPath(ScopedResourceAddress address, string kind, string route)
    {
        if (address.Kind != kind || address.ScopeRef == default || !address.Namespace.IsDefault || !Guid.TryParseExact(address.Name, "N", out _))
            throw new ArgumentException("Invalid Artifact address.", nameof(address));
        var suffix = kind == ArtifactResourceKinds.StagedArtifact ? "/summary" : "";
        return $"api/artifacts/{route}/{address.Name}{suffix}?scopeRef={Uri.EscapeDataString(address.ScopeRef.Value)}&namespace={Uri.EscapeDataString(address.Namespace.Value)}";
    }
    public Task<ArtifactRecordContent> ReadRecordAsync(FlowRunArtifactId id, string memberId, CancellationToken cancellationToken = default) =>
        ApiResponse.ReadAsync<ArtifactRecordContent>(httpClient, $"{DurablePath(id)}/records/{Uri.EscapeDataString(memberId)}/content", cancellationToken);
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

    public string GetDownloadUrl(StagedArtifactId id) => $"/{Path(id)}/download";

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

    public async Task<FlowRunArtifactResource?> GetDurableAsync(FlowRunArtifactId id,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync(DurablePath(id), cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<FlowRunArtifactResource>(cancellationToken);
    }

    public async Task<FlowRunArtifactMaterialization> StartMaterializationAsync(FlowRunArtifactId id,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsJsonAsync($"{DurablePath(id)}/materializations",
            new MaterializeFlowRunArtifactRequest(), cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
        return (await response.Content.ReadFromJsonAsync<FlowRunArtifactMaterialization>(cancellationToken))!;
    }

    public async Task<FlowRunArtifactMaterialization?> GetMaterializationAsync(FlowRunArtifactId id, string flowRunId,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync(
            $"{DurablePath(id)}/materializations/{Uri.EscapeDataString(flowRunId)}", cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<FlowRunArtifactMaterialization>(cancellationToken);
    }

    public async Task<IReadOnlyList<FlowRunArtifactMaterialization>> GetMaterializationsAsync(FlowRunArtifactId id,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync($"{DurablePath(id)}/materializations?maximum=50", cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<IReadOnlyList<FlowRunArtifactMaterialization>>(cancellationToken) ?? [];
    }

    private static string Path(StagedArtifactId id) => $"api/artifacts/staged/{id}";
    private static string DurablePath(FlowRunArtifactId id) => $"api/artifacts/flow-run-artifacts/{id}";
}
