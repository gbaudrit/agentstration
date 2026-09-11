using System.Net;
using System.Text;
using Agentstration.Management.Abstractions;
using Agentstration.ResourceManagement;
using Agentstration.Resources;

namespace Agentstration.Management.Core;

public sealed class SourceRegistryTrustEvaluationService(
    IResourceStore store,
    ISourceRegistryCacheStore cache,
    ISourceRegistryReader registryReader,
    TimeProvider timeProvider) : ISourceVerificationEvidenceProvider
{
    public async Task<SourceRegistryOriginTrustView?> EvaluateOriginAsync(
        string registrationName,
        CancellationToken cancellationToken)
    {
        var registration = await GetRegistrationAsync(registrationName, cancellationToken);
        if (registration is null) return null;
        var observation = await GetObservationAsync(registration.Uid, cancellationToken);
        return Origin(registration, observation, timeProvider.GetUtcNow());
    }

    public async Task<SourceRegistrySourceTrustView> EvaluateSourceAsync(
        string publisher,
        string sourceName,
        string version,
        string? manifestDigest,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publisher);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        var evaluatedAt = timeProvider.GetUtcNow();
        var registrations = await store.ListExactAsync<SourceRegistryRegistrationResource>(
            ResourceScopeRef.Instance, ResourceKinds.SourceRegistryRegistration, 0, 1000, cancellationToken);
        var publisherEvidence = new List<SourceRegistryPublisherEvidenceView>();
        var versionEvidence = new List<SourceRegistryVersionEvidenceView>();
        var unavailable = false;

        foreach (var stored in registrations.OrderBy(value => value.Value.Name, StringComparer.Ordinal))
        {
            var registration = stored.Value;
            if (!registration.Definition.Enabled) continue;
            var observation = await GetObservationAsync(registration.Uid, cancellationToken);
            if (observation is null) continue;
            var origin = Origin(registration, observation, evaluatedAt);
            SourceRegistryCachedPublication? publication;
            try
            {
                publication = await cache.GetAsync(observation.Id, cancellationToken);
            }
            catch (IOException)
            {
                unavailable = true;
                continue;
            }
            if (publication is null)
            {
                unavailable = true;
                continue;
            }

            foreach (var catalog in observation.Catalogs.OrderBy(value => value.Name, StringComparer.Ordinal))
            {
                if (!publication.Catalogs.TryGetValue(catalog.CachePath, out var bytes))
                {
                    unavailable = true;
                    continue;
                }
                ParsedSourceRegistry parsed;
                try
                {
                    parsed = registryReader.Read(Encoding.UTF8.GetString(bytes), catalog.CachePath);
                }
                catch (SourceValidationException)
                {
                    unavailable = true;
                    continue;
                }
                if (!string.Equals(parsed.RegistryDigest, catalog.RegistryDigest, StringComparison.Ordinal))
                {
                    unavailable = true;
                    continue;
                }

                var registryEvidence = new SourceRegistryEvidence
                {
                    EvidenceSource = "registry",
                    RegistrationUid = registration.Uid,
                    RegistrationName = registration.Name,
                    ObservationId = observation.Id,
                    IndexDigest = observation.IndexDigest,
                    CatalogName = catalog.Name,
                    CatalogDigest = catalog.RegistryDigest,
                    ObservedAt = observation.FetchedAt
                };
                var declaredPublisher = parsed.Manifest.Definition.Publishers.SingleOrDefault(value =>
                    string.Equals(value.Name, publisher, StringComparison.Ordinal));
                if (declaredPublisher is null) continue;
                var asserted = ParseStatus(declaredPublisher.Status);
                var accepted = Accept(asserted, registration.Definition.TrustPolicy);
                publisherEvidence.Add(new(
                    asserted,
                    accepted,
                    new SourcePublisher
                    {
                        Name = declaredPublisher.Name,
                        DisplayName = declaredPublisher.DisplayName,
                        Url = declaredPublisher.Url
                    },
                    registration.Definition.TrustPolicy,
                    origin.Classification,
                    registryEvidence));

                foreach (var source in parsed.Manifest.Definition.Sources.Where(value =>
                    string.Equals(value.Publisher, publisher, StringComparison.Ordinal)
                    && string.Equals(value.Name, sourceName, StringComparison.Ordinal)))
                {
                    foreach (var candidate in source.Versions.Where(value =>
                        string.Equals(value.Version, version, StringComparison.Ordinal)))
                    {
                        versionEvidence.Add(new(
                            publisher,
                            sourceName,
                            candidate.Version,
                            candidate.ManifestDigest,
                            ResolveManifestUrl(observation.FinalIndexUrl, candidate.ManifestUrl),
                            accepted,
                            registryEvidence));
                    }
                }
            }
        }

        var publisherView = Publisher(publisher, publisherEvidence, evaluatedAt);
        var acceptedVersions = versionEvidence.Where(value =>
            value.PublisherStatus is SourceRegistryPublisherStatus.Verified or SourceRegistryPublisherStatus.Official or SourceRegistryPublisherStatus.Revoked).ToArray();
        SourceVerificationStatus status;
        string reason;
        if (publisherView.EffectiveStatus == SourceRegistryPublisherStatus.Revoked)
        {
            status = SourceVerificationStatus.Revoked;
            reason = "source_registry_publisher_revoked";
        }
        else if (acceptedVersions.Select(value => value.ManifestDigest).Distinct(StringComparer.Ordinal).Skip(1).Any())
        {
            status = SourceVerificationStatus.Conflict;
            reason = "source_registry_manifest_digest_conflict";
        }
        else if (manifestDigest is not null && acceptedVersions.Any(value =>
            string.Equals(value.ManifestDigest, manifestDigest, StringComparison.Ordinal)))
        {
            status = SourceVerificationStatus.Verified;
            reason = "source_definition_verified_by_registry";
        }
        else if (unavailable && acceptedVersions.Length == 0)
        {
            status = SourceVerificationStatus.Unavailable;
            reason = "source_registry_evidence_unavailable";
        }
        else
        {
            status = SourceVerificationStatus.Unverified;
            reason = acceptedVersions.Length == 0
                ? "source_registry_trusted_evidence_not_found"
                : "source_manifest_digest_not_listed";
        }

        return new(publisherView, status, reason, manifestDigest, evaluatedAt, versionEvidence);
    }

    async Task<SourceDefinitionVerificationView?> ISourceVerificationEvidenceProvider.VerifyDefinitionAsync(
        SourceVersionResource version,
        CancellationToken cancellationToken)
    {
        var evaluated = await EvaluateSourceAsync(
            version.Definition.Publisher,
            version.Definition.SourceName,
            version.Definition.Version,
            version.Definition.ManifestDigest,
            cancellationToken);
        if (evaluated.VersionStatus == SourceVerificationStatus.Unverified
            && evaluated.Evidence.Count == 0
            && evaluated.Publisher.Evidence.Count == 0)
            return null;

        var exact = evaluated.Evidence.Where(value =>
                string.Equals(value.ManifestDigest, version.Definition.ManifestDigest, StringComparison.Ordinal))
            .ToArray();
        var evidence = exact.FirstOrDefault()?.Evidence;
        var verifiedPublisher = evaluated.Publisher.Evidence.FirstOrDefault(value =>
            value.AcceptedStatus is SourceRegistryPublisherStatus.Verified or SourceRegistryPublisherStatus.Official)?.Publisher;
        return new(
            evaluated.VersionStatus,
            evaluated.VersionReasonCode,
            version.Definition.PublishedDefinition.Publisher,
            evaluated.VersionStatus == SourceVerificationStatus.Verified ? verifiedPublisher : null,
            evidence is null ? null : new SourceVerificationEvidence
            {
                Type = "source-registry-observation",
                Authority = evidence.RegistrationName,
                Reference = evidence.ObservationId.ToString("D")
            },
            exact.Select(value => new VerifiedSourceManifestLocation { Url = value.ManifestUrl }).Distinct().ToArray());
    }

    public static SourceRegistryOriginClassification ClassifyOrigin(
        SourceRegistryRegistrationResource registration,
        Uri origin)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(origin);
        if (string.Equals(registration.Name, SourceRegistryWellKnown.OfficialName, StringComparison.Ordinal)
            && registration.Uid != Guid.Empty
            && registration.Metadata.Annotations.TryGetValue(ResourceProvenanceAnnotations.BuiltIn, out var builtIn)
            && string.Equals(builtIn, "true", StringComparison.OrdinalIgnoreCase))
            return SourceRegistryOriginClassification.Official;
        if (IsAgentstrationOwnedHttpsOrigin(origin))
            return SourceRegistryOriginClassification.AgentstrationOwned;
        if (IsInternalOrigin(origin))
            return SourceRegistryOriginClassification.Internal;
        return SourceRegistryOriginClassification.External;
    }

    public static bool IsAgentstrationOwnedHttpsOrigin(Uri origin)
    {
        ArgumentNullException.ThrowIfNull(origin);
        if (!origin.IsAbsoluteUri || origin.Scheme != Uri.UriSchemeHttps || !origin.IsDefaultPort
            || !string.IsNullOrEmpty(origin.UserInfo)) return false;
        var host = origin.IdnHost;
        return string.Equals(host, "agentstration.io", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".agentstration.io", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsInternalOrigin(Uri origin)
    {
        if (string.Equals(origin.IdnHost, "localhost", StringComparison.OrdinalIgnoreCase)
            || origin.IdnHost.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)) return true;
        return IPAddress.TryParse(origin.IdnHost, out var address)
            && !SourceRegistryNetworkPolicy.IsAddressAllowed(address, allowPrivateNetwork: false);
    }

    private static SourceRegistryPublisherTrustView Publisher(
        string publisher,
        IReadOnlyList<SourceRegistryPublisherEvidenceView> evidence,
        DateTimeOffset evaluatedAt)
    {
        var effective = evidence.Any(value => value.AcceptedStatus == SourceRegistryPublisherStatus.Revoked)
            ? SourceRegistryPublisherStatus.Revoked
            : evidence.Any(value => value.AcceptedStatus == SourceRegistryPublisherStatus.Official)
                ? SourceRegistryPublisherStatus.Official
                : evidence.Any(value => value.AcceptedStatus == SourceRegistryPublisherStatus.Verified)
                    ? SourceRegistryPublisherStatus.Verified
                    : SourceRegistryPublisherStatus.Declared;
        var reason = effective switch
        {
            SourceRegistryPublisherStatus.Revoked => "source_registry_publisher_revoked",
            SourceRegistryPublisherStatus.Official => "source_registry_publisher_official",
            SourceRegistryPublisherStatus.Verified => "source_registry_publisher_verified",
            _ => evidence.Count == 0 ? "source_registry_publisher_not_observed" : "source_registry_publisher_declared"
        };
        return new(publisher, effective, reason, evaluatedAt, evidence);
    }

    private static SourceRegistryPublisherStatus Accept(
        SourceRegistryPublisherStatus asserted,
        SourceRegistryTrustPolicy policy) =>
        (asserted, policy) switch
        {
            (SourceRegistryPublisherStatus.Revoked, SourceRegistryTrustPolicy.Trusted or SourceRegistryTrustPolicy.Authoritative) => SourceRegistryPublisherStatus.Revoked,
            (SourceRegistryPublisherStatus.Official, SourceRegistryTrustPolicy.Authoritative) => SourceRegistryPublisherStatus.Official,
            (SourceRegistryPublisherStatus.Official or SourceRegistryPublisherStatus.Verified, SourceRegistryTrustPolicy.Trusted or SourceRegistryTrustPolicy.Authoritative) => SourceRegistryPublisherStatus.Verified,
            _ => SourceRegistryPublisherStatus.Declared
        };

    private static SourceRegistryPublisherStatus ParseStatus(string status) => status switch
    {
        SourceRegistryPublisherStatuses.Declared => SourceRegistryPublisherStatus.Declared,
        SourceRegistryPublisherStatuses.Verified => SourceRegistryPublisherStatus.Verified,
        SourceRegistryPublisherStatuses.Official => SourceRegistryPublisherStatus.Official,
        SourceRegistryPublisherStatuses.Revoked => SourceRegistryPublisherStatus.Revoked,
        _ => throw new SourceValidationException("source_registry_publisher_status_invalid", "The registry publisher status is unsupported.")
    };

    private static SourceRegistryOriginTrustView Origin(
        SourceRegistryRegistrationResource registration,
        SourceRegistryObservation? observation,
        DateTimeOffset evaluatedAt)
    {
        var origin = observation?.FinalIndexUrl ?? registration.Definition.IndexUrl;
        var classification = ClassifyOrigin(registration, origin);
        var reason = classification == SourceRegistryOriginClassification.Official
            ? "source_registry_origin_official_registration"
            : classification == SourceRegistryOriginClassification.AgentstrationOwned
                ? "source_registry_origin_agentstration_owned_informational"
                : classification == SourceRegistryOriginClassification.Internal
                    ? "source_registry_origin_internal"
                    : "source_registry_origin_external";
        return new(
            registration.Uid,
            registration.Name,
            new Uri(origin.GetLeftPart(UriPartial.Authority)),
            classification,
            registration.Definition.TrustPolicy,
            reason,
            evaluatedAt,
            observation?.Id,
            observation?.IndexDigest);
    }

    private async Task<SourceRegistryRegistrationResource?> GetRegistrationAsync(
        string name,
        CancellationToken cancellationToken) =>
        (await store.GetExactAsync<SourceRegistryRegistrationResource>(
            ScopedResourceAddress.Create(ResourceScopeRef.Instance, ResourceNamespace.Default, ResourceKinds.SourceRegistryRegistration, name),
            cancellationToken))?.Value;

    private async Task<SourceRegistryObservation?> GetObservationAsync(
        Guid registrationUid,
        CancellationToken cancellationToken)
    {
        var states = await store.ListExactAsync<SourceRegistryObservedStateResource>(
            ResourceScopeRef.Instance, ResourceKinds.SourceRegistryObservedState, 0, 1000, cancellationToken);
        return states.SingleOrDefault(value => value.Value.Definition.RegistrationUid == registrationUid)
            ?.Value.Definition.Current;
    }

    private static string ResolveManifestUrl(Uri finalIndexUrl, string manifestUrl)
    {
        if (Uri.TryCreate(manifestUrl, UriKind.Absolute, out var absolute)) return absolute.AbsoluteUri;
        var builder = new UriBuilder(finalIndexUrl) { Query = string.Empty, Fragment = string.Empty };
        var slash = builder.Path.LastIndexOf('/');
        builder.Path = slash < 0 ? "/" : builder.Path[..(slash + 1)];
        return new Uri(builder.Uri, manifestUrl).AbsoluteUri;
    }
}
