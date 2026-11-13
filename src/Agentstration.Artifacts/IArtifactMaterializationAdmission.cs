using Agentstration.Artifacts.Contracts;

namespace Agentstration.Artifacts;

/// <summary>Protects admission against concurrent retirement of source-owned content.</summary>
public interface IArtifactMaterializationAdmission
{
    Task EnsureAsync(FlowRunArtifactId artifactId, CancellationToken cancellationToken);
}
