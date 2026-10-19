using Agentstration.ResourceManagement.Contracts;
using Agentstration.Sources.Contracts;

namespace Agentstration.Bootstrap.Contracts;

public interface IBootstrapProfileApiService
{
    Task<BootstrapManagementView> GetAsync(Guid actorPrincipalId, CancellationToken cancellationToken);
    Task<BootstrapCompositionPreview> PreviewAsync(BootstrapProfileSelection selection, Guid actorPrincipalId, CancellationToken cancellationToken);
    Task<BootstrapProfileSummary> GetSourceProfileAsync(BootstrapSourceProfileSelection selection, Guid actorPrincipalId, CancellationToken cancellationToken);
    Task<IReadOnlyList<BootstrapBindingTargetOption>> GetBindingTargetsAsync(
        BootstrapApplicationTarget? target,
        BootstrapBindingTargetKind targetKind,
        IReadOnlyList<string> profiles,
        Guid actorPrincipalId,
        CancellationToken cancellationToken,
        BootstrapSourceProfileSelection? source = null);
    Task<BootstrapApplicationResource?> GetApplicationAsync(string applicationId, Guid actorPrincipalId, CancellationToken cancellationToken);
    Task<BootstrapApplicationResource> ApplyAsync(BootstrapProfileSelection selection, string expectedDigest, Guid actorPrincipalId, CancellationToken cancellationToken);
}
