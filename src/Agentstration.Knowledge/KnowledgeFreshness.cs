using Agentstration.Knowledge.Contracts;

namespace Agentstration.Knowledge;

public static class KnowledgeFreshness
{
    public static bool IsInputFresh(KnowledgeDataSourceBinding binding, KnowledgeProjectionInputEvidence? evidence, TimeProvider clock) =>
        evidence?.UsesAcquisitionLifecycle == true ? evidence.DirectInput is null
            && (evidence.AcquisitionTimeToLive is not { } ttl || clock.GetUtcNow() < evidence.AcquiredAt + ttl)
            : IsFresh(binding, evidence?.AcquiredAt, clock);
    public static bool InputRequiresRefresh(KnowledgeDataSourceBinding binding, KnowledgeProjectionInputEvidence? evidence, TimeProvider clock) =>
        evidence?.UsesAcquisitionLifecycle == true ? !IsInputFresh(binding, evidence, clock)
            : RequiresRefresh(binding, evidence?.AcquiredAt, clock);

    public static bool IsFresh(KnowledgeDataSourceBinding binding, DateTimeOffset? succeededAt, TimeProvider clock) =>
        succeededAt is { } completed && (binding.MaximumAge is null || binding.MaximumAge == TimeSpan.Zero
            || clock.GetUtcNow() - completed <= binding.MaximumAge.Value);

    public static bool RequiresRefresh(KnowledgeDataSourceBinding binding, DateTimeOffset? succeededAt, TimeProvider clock) =>
        binding.RefreshIfNecessary && (binding.MaximumAge is null || binding.MaximumAge == TimeSpan.Zero
            || !IsFresh(binding, succeededAt, clock));
}
