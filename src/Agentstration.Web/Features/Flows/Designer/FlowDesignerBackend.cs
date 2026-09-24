using Agentstration.Flows;
using Agentstration.Flows.Application;
using Agentstration.Flows.Contracts;
using Agentstration.Web.Console;
using Agentstration.Web.FlowDesigner.Backend;

namespace Agentstration.Web.Features.Flows.Designer;

public sealed class FlowDesignerBackend(IFlowApiClient client) : IFlowDesignerBackend
{
    private const string PackNameMetadata = "agentstration.io/pack.name";

    public async Task<FlowDesignerLoadResult> LoadAsync(FlowDesignerTarget target, CancellationToken cancellationToken)
    {
        var flow = await client.GetFlowAsync(target.Namespace, target.ResourceId, cancellationToken);
        if (flow.Metadata.ContainsKey(PackNameMetadata))
        {
            if (string.IsNullOrWhiteSpace(flow.ActiveVersion))
                throw new InvalidOperationException($"Pack-managed Flow '{target.Namespace.Value}/{target.ResourceId}' has no active published version.");
            var version = await client.GetFlowVersionAsync(target.Namespace, target.ResourceId, flow.ActiveVersion, cancellationToken);
            if (version.Graph is null)
                throw new InvalidOperationException($"Published version {version.Version} is a legacy Flow version without a Graph and cannot be opened in the Designer.");
            var resource = new FlowDesignerResource(new(target.ResourceId), flow.Name, version.Description, version.Metadata, version.Graph);
            return new(resource, FlowDraftService.ToYaml(version.Graph), PublishedVersion: version.Version);
        }

        var workspaceDraft = await GetWorkspaceDraftAsync(target, flow, cancellationToken);
        var source = await client.GetDraftSourceAsync(target.Namespace, target.ResourceId, "yaml", cancellationToken);
        return FlowDesignerLoadResult.FromDraft(workspaceDraft, source.Source);
    }

    private async Task<FlowDraftResponse> GetWorkspaceDraftAsync(FlowDesignerTarget target, FlowResponse flow, CancellationToken cancellationToken)
    {
        try
        {
            return await client.GetDraftAsync(target.Namespace, target.ResourceId, cancellationToken);
        }
        catch (AgentstrationApiException exception) when (exception.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            var version = flow.ActiveVersion;
            if (string.IsNullOrWhiteSpace(version))
                version = (await client.GetFlowVersionsAsync(target.Namespace, target.ResourceId, cancellationToken)).FirstOrDefault()?.Version;
            if (string.IsNullOrWhiteSpace(version)) throw;
            try
            {
                return await client.CreateDraftFromVersionAsync(target.Namespace, target.ResourceId, version, cancellationToken);
            }
            catch (AgentstrationApiException conflict) when (conflict.StatusCode == System.Net.HttpStatusCode.Conflict)
            {
                return await client.GetDraftAsync(target.Namespace, target.ResourceId, cancellationToken);
            }
        }
    }
    public async Task<FlowSourceResponse> GetSourceAsync(FlowDesignerTarget target, CancellationToken cancellationToken)
    {
        await EnsureEditableAsync(target, cancellationToken);
        return await client.GetDraftSourceAsync(target.Namespace, target.ResourceId, "yaml", cancellationToken);
    }

    public async Task<FlowDraftResponse> SaveDraftAsync(FlowDesignerTarget target, UpdateFlowDraftRequest request, string etag, CancellationToken cancellationToken)
    {
        await EnsureEditableAsync(target, cancellationToken);
        return await client.SaveDraftAsync(target.Namespace, target.ResourceId, request, etag, cancellationToken);
    }

    public async Task<FlowDraftResponse> ReplaceSourceAsync(FlowDesignerTarget target, ReplaceFlowSourceRequest request, string etag, CancellationToken cancellationToken)
    {
        await EnsureEditableAsync(target, cancellationToken);
        return await client.ReplaceDraftSourceAsync(target.Namespace, target.ResourceId, request, etag, cancellationToken);
    }

    public async Task<FlowValidationResponse> ValidateAsync(FlowDesignerTarget target, CancellationToken cancellationToken)
    {
        await EnsureEditableAsync(target, cancellationToken);
        return await client.ValidateDraftAsync(target.Namespace, target.ResourceId, cancellationToken);
    }

    public async Task<FlowVersionResponse> PublishAsync(FlowDesignerTarget target, PublishFlowDraftRequest request, CancellationToken cancellationToken)
    {
        await EnsureEditableAsync(target, cancellationToken);
        try { return await client.PublishDraftAsync(target.Namespace, target.ResourceId, request, cancellationToken); }
        catch (AgentstrationApiException exception) when (exception.ProblemTitle == "flow_version_already_published")
        {
            throw new FlowDesignerVersionAlreadyPublishedException(request.Version, exception);
        }
    }

    public async Task<FlowRun> RunDraftAsync(FlowDesignerTarget target, CreateFlowRunRequest request, CancellationToken cancellationToken)
    {
        await EnsureEditableAsync(target, cancellationToken);
        return await client.CreateDraftRunAsync(target.Namespace, target.ResourceId, request, cancellationToken);
    }

    private async Task EnsureEditableAsync(FlowDesignerTarget target, CancellationToken cancellationToken)
    {
        if (target.Namespace.IsDefault) return;
        var flow = await client.GetFlowAsync(target.Namespace, target.ResourceId, cancellationToken);
        if (flow.Metadata.ContainsKey(PackNameMetadata))
            throw new InvalidOperationException($"Pack-managed Flow '{target.Namespace.Value}/{target.ResourceId}' is read-only.");
    }
}
