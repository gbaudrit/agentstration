using System.Net.Http.Json;
using Agentstration.DataSources.Contracts;
using Agentstration.Flows;

namespace Agentstration.Web.Console;

public interface ISourceAuthoringClient
{
    Task<IReadOnlyList<ConsumedSampleCandidate>> CandidatesAsync(Agentstration.Resources.ResourceReference source, CancellationToken ct);
    Task<SourceSample> PrepareConsumedAsync(PrepareConsumedSampleRequest request, CancellationToken ct);
    Task<Agentstration.Knowledge.Contracts.PreparedKnowledgePreview> PrepareKnowledgeAsync(Agentstration.Knowledge.Contracts.PrepareKnowledgePreviewRequest request, CancellationToken ct);
    Task<SourceSample> PrepareDataSourceAsync(PrepareDataSourceSampleRequest request, CancellationToken ct) => throw new NotSupportedException();
    Task CancelAsync(string id, CancellationToken ct) => throw new NotSupportedException();
    Task<SourceSample> AcquireAsync(AcquireSourceSampleRequest request, CancellationToken ct);
    Task<SourceSample> GetAsync(string id, CancellationToken ct);
    Task DeleteAsync(string id, CancellationToken ct);
    Task<SourceSequenceAnalysis> AnalyzeAsync(SourceSequenceRequest request, CancellationToken ct);
    Task<SourceSequencePreview> ReplayAsync(string id, ReplaySourceSampleRequest request, CancellationToken ct);
}
public sealed class SourceAuthoringApiClient(HttpClient http) : ISourceAuthoringClient
{
    public Task<IReadOnlyList<ConsumedSampleCandidate>> CandidatesAsync(Agentstration.Resources.ResourceReference source, CancellationToken ct) =>
        ApiResponse.ReadAsync<IReadOnlyList<ConsumedSampleCandidate>>(http, $"api/source-samples/consumed-candidates?name={Uri.EscapeDataString(source.Name)}&scopeRef={Uri.EscapeDataString(source.ScopeRef!.Value.Value)}&namespace={Uri.EscapeDataString((source.Namespace ?? Agentstration.Resources.ResourceNamespace.Default).Value)}", ct);
    public Task<SourceSample> PrepareConsumedAsync(PrepareConsumedSampleRequest request, CancellationToken ct) => Post<SourceSample>("api/source-samples/consumed", request, ct);
    public Task<Agentstration.Knowledge.Contracts.PreparedKnowledgePreview> PrepareKnowledgeAsync(Agentstration.Knowledge.Contracts.PrepareKnowledgePreviewRequest request, CancellationToken ct) => Post<Agentstration.Knowledge.Contracts.PreparedKnowledgePreview>("api/knowledge-previews/prepare", request, ct);
    public Task<SourceSample> PrepareDataSourceAsync(PrepareDataSourceSampleRequest request, CancellationToken ct) => Post<SourceSample>("api/source-samples/prepared-source", request, ct);
    public async Task CancelAsync(string id, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync("api/source-samples/" + Uri.EscapeDataString(id) + "/cancel", new { }, ct);
        await ApiResponse.EnsureSuccessAsync(response, ct);
    }
    public Task<SourceSample> AcquireAsync(AcquireSourceSampleRequest request, CancellationToken ct) => Post<SourceSample>("api/source-samples", request, ct);
    public Task<SourceSample> GetAsync(string id, CancellationToken ct) => ApiResponse.ReadAsync<SourceSample>(http, "api/source-samples/" + Uri.EscapeDataString(id), ct);
    public async Task DeleteAsync(string id, CancellationToken ct)
    {
        using var response = await http.DeleteAsync("api/source-samples/" + Uri.EscapeDataString(id), ct);
        await ApiResponse.EnsureSuccessAsync(response, ct);
    }
    public Task<SourceSequenceAnalysis> AnalyzeAsync(SourceSequenceRequest request, CancellationToken ct) => Post<SourceSequenceAnalysis>("api/source-sequences/contracts", request, ct);
    public Task<SourceSequencePreview> ReplayAsync(string id, ReplaySourceSampleRequest request, CancellationToken ct) => Post<SourceSequencePreview>("api/source-samples/" + Uri.EscapeDataString(id) + "/preview", request, ct);
    private async Task<T> Post<T>(string path, object body, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync(path, body, ct);
        await ApiResponse.EnsureSuccessAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<T>(ct))!;
    }
}
