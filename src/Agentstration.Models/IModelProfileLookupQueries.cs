using Agentstration.Resources;

namespace Agentstration.Models;

public sealed record ModelProfileLookupResult(IReadOnlyList<ModelProfileResource> Value, int? NextOffset);

public interface IModelProfileLookupQueries
{
    Task<ModelProfileLookupResult> LookupProfilesAsync(string? search, string? provider, string? ownerKind, int offset, int take, CancellationToken cancellationToken);
}
