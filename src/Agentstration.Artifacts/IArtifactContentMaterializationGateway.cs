using Agentstration.Artifacts.Contracts;

namespace Agentstration.Artifacts;

public interface IArtifactContentMaterializationGateway
{
    Task<FlowRunArtifactMaterialization> StartAsync(
        FlowRunArtifactId artifactId,
        MaterializeFlowRunArtifactRequest request,
        CancellationToken cancellationToken);

    Task<FlowRunArtifactMaterialization?> GetAsync(
        FlowRunArtifactId artifactId,
        string flowRunId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<FlowRunArtifactMaterialization>> ListAsync(
        FlowRunArtifactId artifactId,
        int maximum,
        CancellationToken cancellationToken);
}
