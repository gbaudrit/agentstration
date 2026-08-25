using Agentstration.Flow;
using Agentstration.Flow.Application;
using Agentstration.Flow.Contracts;
using Agentstration.Web.Console;
using Agentstration.Web.FlowDesigner.Backend;

namespace Agentstration.Web.Features.Flows.Designer;

public sealed class FlowDesignerBackend(IFlowApiClient client) : IFlowDesignerBackend
{
    public async Task<FlowDesignerLoadResult> LoadAsync(FlowDesignerTarget target, CancellationToken cancellationToken)
    {
        if (target.IsReadOnly)
        {
            var flow = await client.GetFlowAsync(target.Namespace, target.ResourceId, cancellationToken);
            if (string.IsNullOrWhiteSpace(flow.ActiveVersion))
                throw new InvalidOperationException($"Namespaced Flow '{target.Namespace.Value}/{target.ResourceId}' has no active published version.");
            var version = await client.GetFlowVersionAsync(target.Namespace, target.ResourceId, flow.ActiveVersion, cancellationToken);
            if (version.Graph is null)
                throw new InvalidOperationException($"Published version {version.Version} is a legacy Flow version without a Graph and cannot be opened in the Designer.");
            var resource = new FlowDesignerResource(new(target.ResourceId), flow.Name, version.Description, version.Metadata, version.Graph);
            return new(resource, FlowDraftService.ToYaml(version.Graph), PublishedVersion: version.Version);
        }

        var workspaceDraft = await GetWorkspaceDraftAsync(target.ResourceId, cancellationToken);
        var source = await client.GetDraftSourceAsync(target.ResourceId, "yaml", cancellationToken);
        return FlowDesignerLoadResult.FromDraft(workspaceDraft, source.Source);
    }

    private async Task<FlowDraftResponse> GetWorkspaceDraftAsync(string resourceId, CancellationToken cancellationToken)
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
    public Task<FlowSourceResponse> GetSourceAsync(FlowDesignerTarget target, CancellationToken cancellationToken)
    {
        EnsureWorkspace(target);
        return client.GetDraftSourceAsync(target.ResourceId, "yaml", cancellationToken);
    }

    public Task<FlowDraftResponse> SaveDraftAsync(FlowDesignerTarget target, UpdateFlowDraftRequest request, string etag, CancellationToken cancellationToken)
    {
        EnsureWorkspace(target);
        return client.SaveDraftAsync(target.ResourceId, request, etag, cancellationToken);
    }

    public Task<FlowDraftResponse> ReplaceSourceAsync(FlowDesignerTarget target, ReplaceFlowSourceRequest request, string etag, CancellationToken cancellationToken)
    {
        EnsureWorkspace(target);
        return client.ReplaceDraftSourceAsync(target.ResourceId, request, etag, cancellationToken);
    }

    public Task<FlowValidationResponse> ValidateAsync(FlowDesignerTarget target, CancellationToken cancellationToken)
    {
        EnsureWorkspace(target);
        return client.ValidateDraftAsync(target.ResourceId, cancellationToken);
    }

    public Task<FlowVersionResponse> PublishAsync(FlowDesignerTarget target, PublishFlowDraftRequest request, CancellationToken cancellationToken)
    {
        EnsureWorkspace(target);
        return client.PublishDraftAsync(target.ResourceId, request, cancellationToken);
    }

    public Task<FlowRun> RunDraftAsync(FlowDesignerTarget target, CreateFlowRunRequest request, CancellationToken cancellationToken)
    {
        EnsureWorkspace(target);
        return client.CreateDraftRunAsync(target.ResourceId, request, cancellationToken);
    }

    private static void EnsureWorkspace(FlowDesignerTarget target)
    {
        if (target.IsReadOnly)
            throw new InvalidOperationException($"Namespaced Flow '{target.Namespace.Value}/{target.ResourceId}' is read-only.");
    }
}
