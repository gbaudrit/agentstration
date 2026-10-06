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
}
