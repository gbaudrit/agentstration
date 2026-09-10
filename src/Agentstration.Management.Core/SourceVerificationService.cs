using Agentstration.Management.Abstractions;

namespace Agentstration.Management.Core;

public sealed class SourceVerificationService(
    ISourceVerificationIndexProvider indexes,
    IEnumerable<ISourceVerificationEvidenceProvider> evidenceProviders)
{
    public async Task<SourceDefinitionVerificationView> VerifyDefinitionAsync(
        SourceVersionResource version,
        CancellationToken cancellationToken)
    {
        var loaded = await LoadAsync(cancellationToken);
        return await VerifyDefinitionAsync(version, loaded, cancellationToken);
    }

    private async Task<SourceDefinitionVerificationView> VerifyDefinitionAsync(
        SourceVersionResource version,
        LoadedIndex loaded,
        CancellationToken cancellationToken)
    {
        var current = VerifyDefinition(version, loaded.Index, loaded.Unavailable);
        foreach (var provider in evidenceProviders)
        {
            var candidate = await provider.VerifyDefinitionAsync(version, cancellationToken);
            if (candidate is null) continue;
            if (candidate.Status is SourceVerificationStatus.Revoked or SourceVerificationStatus.Conflict)
                return candidate;
            if (candidate.Status == SourceVerificationStatus.Verified)
                current = candidate;
            else if (current.Status != SourceVerificationStatus.Verified
                && candidate.Status != SourceVerificationStatus.Unverified)
                current = candidate;
        }
        return current;
    }

    public async Task<SourceChannelSnapshotVerificationView> VerifySnapshotAsync(
        SourceVersionResource version,
        SourceChannelSnapshotResource snapshot,
        CancellationToken cancellationToken)
    {
        if (snapshot.Definition.SourceVersionUid != version.Uid)
            throw new SourceValidationException("source_snapshot_version_mismatch", "The snapshot does not belong to the requested Source Version.");

        var loaded = await LoadAsync(cancellationToken);
        var definition = await VerifyDefinitionAsync(version, loaded, cancellationToken);
        if (definition.Status != SourceVerificationStatus.Verified)
        {
            return new(
                definition.Status,
                definition.Status == SourceVerificationStatus.Unavailable
                    ? "source_verification_index_unavailable"
                    : "source_definition_not_verified",
                definition,
                snapshot.Definition.Channel,
                snapshot.Definition.ResolvedRevision,
                snapshot.Definition.Artifact.Sha256,
                null);
        }

        var entry = loaded.Index is null ? null : ExactDefinition(version, loaded.Index);
        var channel = entry?.Channels.SingleOrDefault(candidate =>
            string.Equals(candidate.Name, snapshot.Definition.Channel, StringComparison.Ordinal)
            && string.Equals(candidate.Revision, snapshot.Definition.ResolvedRevision, StringComparison.Ordinal)
            && string.Equals(candidate.SnapshotDigest, snapshot.Definition.Artifact.Sha256, StringComparison.Ordinal));
        return channel is null
            ? new(
                SourceVerificationStatus.Unverified,
                "source_channel_snapshot_not_listed",
                definition,
                snapshot.Definition.Channel,
                snapshot.Definition.ResolvedRevision,
                snapshot.Definition.Artifact.Sha256,
                null)
            : new(
                SourceVerificationStatus.Verified,
                "source_channel_snapshot_verified",
                definition,
                snapshot.Definition.Channel,
                snapshot.Definition.ResolvedRevision,
                snapshot.Definition.Artifact.Sha256,
                channel.Evidence);
    }

    private static SourceDefinitionVerificationView VerifyDefinition(
        SourceVersionResource version,
        VerifiedSourceIndexManifest? index,
        bool unavailable)
    {
        var declared = version.Definition.PublishedDefinition.Publisher;
        if (unavailable)
            return new(SourceVerificationStatus.Unavailable, "source_verification_index_unavailable", declared, null, null, []);
        if (index is null)
            return new(SourceVerificationStatus.Unverified, "source_verification_index_not_configured", declared, null, null, []);

        var exact = ExactDefinition(version, index);
        if (exact is not null)
            return new(SourceVerificationStatus.Verified, "source_definition_verified", declared, exact.Publisher, exact.Evidence, exact.ManifestLocations);

        var identityListed = index.Definition.Sources.Any(candidate =>
            string.Equals(candidate.Source.Publisher, version.Definition.Publisher, StringComparison.Ordinal)
            && string.Equals(candidate.Source.Name, version.Definition.SourceName, StringComparison.Ordinal)
            && string.Equals(candidate.Version, version.Definition.Version, StringComparison.Ordinal));
        return new(
            SourceVerificationStatus.Unverified,
            identityListed ? "source_manifest_digest_not_listed" : "source_definition_not_listed",
            declared,
            null,
            null,
            []);
    }

    private static VerifiedSourceDefinition? ExactDefinition(
        SourceVersionResource version,
        VerifiedSourceIndexManifest index) =>
        index.Definition.Sources.SingleOrDefault(candidate =>
            string.Equals(candidate.Source.Publisher, version.Definition.Publisher, StringComparison.Ordinal)
            && string.Equals(candidate.Source.Name, version.Definition.SourceName, StringComparison.Ordinal)
            && string.Equals(candidate.Version, version.Definition.Version, StringComparison.Ordinal)
            && string.Equals(candidate.ManifestDigest, version.Definition.ManifestDigest, StringComparison.Ordinal));

    private async Task<LoadedIndex> LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            return new(await indexes.GetAsync(cancellationToken), false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is SourceValidationException
            or SourceRetrievalException
            or HttpRequestException
            or IOException
            or OperationCanceledException)
        {
            return new(null, true);
        }
    }

    private sealed record LoadedIndex(VerifiedSourceIndexManifest? Index, bool Unavailable);
}
