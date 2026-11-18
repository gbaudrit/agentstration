using Agentstration.Knowledge.Contracts;

namespace Agentstration.Knowledge;

public interface IKnowledgeOutputPublisher
{
    Task<IReadOnlyList<KnowledgeSnapshotArtifact>> PublishAsync(KnowledgeSourceResource source,
        KnowledgeProjectionResource projection, KnowledgeProjectionManifest manifest, CancellationToken cancellationToken);
}
