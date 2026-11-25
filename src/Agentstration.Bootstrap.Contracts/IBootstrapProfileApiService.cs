using Agentstration.ResourceManagement.Contracts;
using Agentstration.Sources.Contracts;

namespace Agentstration.Bootstrap.Contracts;

public interface IBootstrapProfileApiService
{
    Task<WorkspaceSeedReceipt> IngestWorkspaceSeedAsync(IngestWorkspaceSeedRequest request, Guid actorPrincipalId, CancellationToken cancellationToken);
    Task<WorkspaceSeedReceipt> GetWorkspaceSeedAsync(Guid id, BootstrapApplicationTarget target, Guid actorPrincipalId, CancellationToken cancellationToken);
    Task<BootstrapProfileFiles> GetFilesAsync(BootstrapProfileFilesRequest request, Guid actorPrincipalId, CancellationToken cancellationToken);
    Task<BootstrapManagementView> GetAsync(Guid actorPrincipalId, CancellationToken cancellationToken);
    Task<BootstrapCompositionPreview> PreviewAsync(BootstrapProfileSelection selection, Guid actorPrincipalId, CancellationToken cancellationToken);
    Task<BootstrapProfileSummary> GetSourceProfileAsync(BootstrapSourceProfileSelection selection, Guid actorPrincipalId, CancellationToken cancellationToken);
    Task<IReadOnlyList<BootstrapBindingTargetOption>> GetBindingTargetsAsync(
        BootstrapApplicationTarget? target,
        BootstrapBindingTargetKind targetKind,
        IReadOnlyList<string> profiles,
        Guid actorPrincipalId,
        CancellationToken cancellationToken,
        BootstrapSourceProfileSelection? source = null,
        WorkspaceSeedSelection? seed = null);
    Task<IReadOnlyList<BootstrapBindingTargetOption>> GetPlanBindingTargetsAsync(BootstrapBindingTargetsRequest request, Guid actorPrincipalId, CancellationToken cancellationToken);
    Task<BootstrapApplicationResource?> GetApplicationAsync(string applicationId, Guid actorPrincipalId, CancellationToken cancellationToken);
    Task<BootstrapApplicationResource?> CancelAsync(string applicationId, Guid actorPrincipalId, CancellationToken cancellationToken);
    Task<BootstrapApplicationResource> StartAsync(ApplyBootstrapProfilesRequest request, Guid actorPrincipalId, CancellationToken cancellationToken);
    Task<BootstrapApplicationResource> ApplyAsync(BootstrapProfileSelection selection, string expectedDigest, Guid actorPrincipalId, CancellationToken cancellationToken);
}
