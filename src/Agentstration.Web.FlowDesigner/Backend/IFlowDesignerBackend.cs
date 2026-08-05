using Agentstration.Flow;
using Agentstration.Flow.Contracts;

namespace Agentstration.Web.FlowDesigner.Backend;

public interface IFlowDesignerBackend
{
    Task<FlowDraftResponse> GetDraftAsync(string resourceId, CancellationToken cancellationToken);
    Task<FlowSourceResponse> GetSourceAsync(string resourceId, CancellationToken cancellationToken);
    Task<FlowDraftResponse> SaveDraftAsync(string resourceId, UpdateFlowDraftRequest request, string etag, CancellationToken cancellationToken);
    Task<FlowDraftResponse> ReplaceSourceAsync(string resourceId, ReplaceFlowSourceRequest request, string etag, CancellationToken cancellationToken);
    Task<FlowValidationResponse> ValidateAsync(string resourceId, CancellationToken cancellationToken);
    Task<FlowVersionResponse> PublishAsync(string resourceId, PublishFlowDraftRequest request, CancellationToken cancellationToken);
    Task<FlowRun> RunDraftAsync(string resourceId, CreateFlowRunRequest request, CancellationToken cancellationToken);
}
