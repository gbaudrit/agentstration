using Agentstration.Flow;
using Agentstration.Flow.Contracts;
using Agentstration.Web.Console;
using Agentstration.Web.FlowDesigner.Backend;

namespace Agentstration.Web.Features.Flows.Designer;

public sealed class FlowDesignerBackend(IFlowApiClient client) : IFlowDesignerBackend
{
    public async Task<FlowDraftResponse> GetDraftAsync(string resourceId, CancellationToken cancellationToken)
    {
        try
        {
            return await client.GetDraftAsync(resourceId, cancellationToken);
        }
        catch (AgentstrationApiException exception) when (exception.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            var flow = await client.GetFlowAsync(resourceId, cancellationToken);
            var version = flow.ActiveVersion;
            if (string.IsNullOrWhiteSpace(version))
                version = (await client.GetFlowVersionsAsync(resourceId, cancellationToken)).FirstOrDefault()?.Version;
            if (string.IsNullOrWhiteSpace(version)) throw;
            try
            {
                return await client.CreateDraftFromVersionAsync(resourceId, version, cancellationToken);
            }
            catch (AgentstrationApiException conflict) when (conflict.StatusCode == System.Net.HttpStatusCode.Conflict)
            {
                return await client.GetDraftAsync(resourceId, cancellationToken);
            }
        }
    }
    public Task<FlowSourceResponse> GetSourceAsync(string resourceId, CancellationToken cancellationToken) => client.GetDraftSourceAsync(resourceId, "yaml", cancellationToken);
    public Task<FlowDraftResponse> SaveDraftAsync(string resourceId, UpdateFlowDraftRequest request, string etag, CancellationToken cancellationToken) => client.SaveDraftAsync(resourceId, request, etag, cancellationToken);
    public Task<FlowDraftResponse> ReplaceSourceAsync(string resourceId, ReplaceFlowSourceRequest request, string etag, CancellationToken cancellationToken) => client.ReplaceDraftSourceAsync(resourceId, request, etag, cancellationToken);
    public Task<FlowValidationResponse> ValidateAsync(string resourceId, CancellationToken cancellationToken) => client.ValidateDraftAsync(resourceId, cancellationToken);
    public Task<FlowVersionResponse> PublishAsync(string resourceId, PublishFlowDraftRequest request, CancellationToken cancellationToken) => client.PublishDraftAsync(resourceId, request, cancellationToken);
    public Task<FlowRun> RunDraftAsync(string resourceId, CreateFlowRunRequest request, CancellationToken cancellationToken) => client.CreateDraftRunAsync(resourceId, request, cancellationToken);
}
