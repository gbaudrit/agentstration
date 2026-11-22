using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.Sources.Contracts;

namespace Agentstration.Bootstrap.Contracts;

public interface IBootstrapVersionHistory
{
    Task<IReadOnlyList<AppliedBootstrapProfile>> ListProfilesAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<AppliedBootstrapVersion>> ListVersionsAsync(string profileId, CancellationToken cancellationToken);
    Task<IReadOnlyList<ScopedBootstrapApplication>> ListHistoryAsync(ResourceScopeRef scope, string profileId, string version,
        CancellationToken cancellationToken);
    Task<ScopedBootstrapApplication?> GetAsync(ResourceScopeRef scope, Guid id, CancellationToken cancellationToken);
}

public sealed record AppliedBootstrapProfile(string Id, string Name, string DisplayName, BootstrapProfileScope Scope);

public sealed record AppliedBootstrapVersion(string ProfileId, string Version, BootstrapProfileScope Scope, string Digest,
    DateTimeOffset RegisteredAt, BootstrapSourceProvenance? Source);

public sealed record ScopedBootstrapApplication(
    Guid Id, Guid CorrelationId, ResourceScopeRef Scope, BootstrapApplicationSource Source,
    DateTimeOffset StartedAt, DateTimeOffset? CompletedAt, BootstrapApplicationStatus Status,
    IReadOnlyList<AppliedBootstrapVersion> Versions, IReadOnlyList<BootstrapBindingSelection> Bindings,
    IReadOnlyList<BootstrapAppliedResource> Resources);
