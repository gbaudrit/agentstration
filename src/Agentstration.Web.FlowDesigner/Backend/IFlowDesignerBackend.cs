using Agentstration.Flow;
using Agentstration.Flow.Contracts;
using Agentstration.Resources;

namespace Agentstration.Web.FlowDesigner.Backend;

public sealed record FlowDesignerTarget(ResourceNamespace Namespace, string ResourceId)
{
    public bool IsReadOnly => !Namespace.IsDefault;
}

public sealed record FlowDesignerResource(FlowId FlowId, string DisplayName, string? Description, IReadOnlyDictionary<string, string> Tags, FlowGraphDefinition Definition, long? DraftRevision = null);

public sealed record FlowDesignerLoadResult(FlowDesignerResource Resource, string Source, string? ETag = null, string? PublishedVersion = null)
{
    public bool IsReadOnly => PublishedVersion is not null;

    public static FlowDesignerLoadResult FromDraft(FlowDraftResponse response, string source) => new(
        new(response.Value.FlowId, response.Value.DisplayName, response.Value.Description, response.Value.Tags, response.Value.Definition, response.Value.Revision),
        source,
        response.ETag);
}

public interface IFlowDesignerBackend
{
    Task<FlowDesignerLoadResult> LoadAsync(FlowDesignerTarget target, CancellationToken cancellationToken);
    Task<FlowSourceResponse> GetSourceAsync(FlowDesignerTarget target, CancellationToken cancellationToken);
    Task<FlowDraftResponse> SaveDraftAsync(FlowDesignerTarget target, UpdateFlowDraftRequest request, string etag, CancellationToken cancellationToken);
    Task<FlowDraftResponse> ReplaceSourceAsync(FlowDesignerTarget target, ReplaceFlowSourceRequest request, string etag, CancellationToken cancellationToken);
    Task<FlowValidationResponse> ValidateAsync(FlowDesignerTarget target, CancellationToken cancellationToken);
    Task<FlowVersionResponse> PublishAsync(FlowDesignerTarget target, PublishFlowDraftRequest request, CancellationToken cancellationToken);
    Task<FlowRun> RunDraftAsync(FlowDesignerTarget target, CreateFlowRunRequest request, CancellationToken cancellationToken);
}
