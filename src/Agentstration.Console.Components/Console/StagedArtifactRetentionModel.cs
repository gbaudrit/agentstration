using System.Globalization;
using Agentstration.Artifacts.Contracts;

namespace Agentstration.Web.Console;

/// <summary>The local retention input and existing UI eligibility rules; commands remain with the screen.</summary>
public sealed class StagedArtifactRetentionModel
{
    public string Expiration { get; set; } = string.Empty;
    private string baseline = string.Empty;
    public bool IsDirty => Expiration != baseline;
    public void Clear() => Expiration = baseline = string.Empty;
    public void Reset(DateTimeOffset expiresAt) => Expiration = baseline = expiresAt.ToLocalTime().ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture);
    public bool CanExtend(DateTimeOffset expiresAt) => TryGetExpiration(out var value) && value > expiresAt;

    public bool TryGetExpiration(out DateTimeOffset value)
    {
        if (DateTime.TryParse(Expiration, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            value = new DateTimeOffset(DateTime.SpecifyKind(parsed, DateTimeKind.Local));
            return true;
        }
        value = default;
        return false;
    }

    public static bool CanPurge(StagedArtifactView? artifact, DateTimeOffset now) => artifact is not null
        && artifact.Status is not (StagedArtifactStatus.Open or StagedArtifactStatus.Persisting or StagedArtifactStatus.Purging or StagedArtifactStatus.Purged)
        && !artifact.Leases.Any(lease => lease.RevokedAt is null && lease.ExpiresAt > now);
}
