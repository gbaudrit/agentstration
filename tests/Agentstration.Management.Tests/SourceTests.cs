using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Agentstration.Infrastructure.Sources;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Contracts;
using Agentstration.Management.Core;
using Agentstration.Management.Storage.Sqlite;
using Agentstration.ModelProviders;
using Agentstration.Resources;
using Agentstration.Tools.SourceRegistry;
using Agentstration.Web.Hosting;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class SourceTests
{
    [TestMethod]
    public void VerificationIndexContractAcceptsMovingAndImmutableManifestLocations()
    {
        var index = new SourceVerificationIndexReader().Read($$"""
            apiVersion: agentstration.io/v1
            kind: VerifiedSourceIndex
            metadata:
              name: official
            definition:
              sources:
                - source:
                    publisher: agentstration
                    name: bootstrap-samples
                  version: "1"
                  manifestDigest: sha256:{{new string('a', 64)}}
                  publisher:
                    name: agentstration
                    displayName: Agentstration
                  manifestLocations:
                    - url: https://registry.agentstration.io/latest/source.yaml
                      mutable: true
                    - url: https://registry.agentstration.io/versions/1/source.yaml
                  evidence:
                    type: official-static-index
                    authority: agentstration
                  channels: []
            """);

        Assert.AreEqual(2, index.Definition.Sources.Single().ManifestLocations.Count);
        Assert.IsTrue(index.Definition.Sources.Single().ManifestLocations.Single(value => value.Mutable).Mutable);
    }

    [TestMethod]
    public async Task ExactDefinitionDigestIsVerifiedForYamlAndUrlImportsWithoutBecomingRequiredAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var system = fixture.Context.PushSystem();
        var manifest = Manifest("1", "Published name", includeChannel: false);
        var parsed = new SourceManifestReader().Read(manifest);
        fixture.VerificationIndex.Index = VerificationIndex("1", parsed.Digest);

        var yaml = await fixture.Service.ImportYamlAsync(manifest, default);
        fixture.Retriever.Content = manifest;
        var url = await fixture.Service.ImportUrlAsync(new Uri("https://registry.example/source.yaml"), default);

        Assert.AreEqual(SourceVerificationStatus.Verified, yaml.Verification.Status);
        Assert.AreEqual(SourceVerificationStatus.Verified, url.Verification.Status);
        Assert.AreEqual(SourceImportOutcome.Unchanged, url.Outcome);

        fixture.VerificationIndex.Index = VerificationIndex("2", parsed.Digest);
        var unlisted = await fixture.Service.ImportYamlAsync(Manifest("2", "Changed", includeChannel: false), default);
        Assert.AreEqual(SourceVerificationStatus.Unverified, unlisted.Verification.Status);

        fixture.VerificationIndex.Failure = new HttpRequestException("offline");
        var unavailable = await fixture.Verification.VerifyDefinitionAsync(yaml.Version, default);
        Assert.AreEqual(SourceVerificationStatus.Unavailable, unavailable.Status);
        Assert.IsNotNull(await fixture.Service.GetVersionExactAsync(
            ResourceScopeRef.Instance, "agentstration", "official-samples", yaml.Version.Uid, default));
    }

    [TestMethod]
    public async Task SnapshotVerificationRequiresItsExactChannelRevisionAndContentDigestAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var system = fixture.Context.PushSystem();
        await fixture.CreateSourceProviderAsync();
        var imported = await fixture.Service.ImportYamlAsync(Manifest("1", "Published name", includeChannel: true), default);
        _ = await fixture.Bindings.ConfigureAsync(
            "agentstration", "official-samples", imported.Version.Uid, [Selection()], imported.Source.Configuration.ETag!, default);
        var refreshed = await fixture.Snapshots.RefreshExactAsync(
            ResourceScopeRef.Instance, "agentstration", "official-samples", imported.Version.Uid, "stable", default);
        var evidence = new SourceVerificationEvidence { Type = "official-snapshot", Authority = "agentstration" };
        var exactChannel = new VerifiedSourceChannelDefinition
        {
            Name = "stable",
            Revision = refreshed.Snapshot.Definition.ResolvedRevision,
            SnapshotDigest = refreshed.Snapshot.Definition.Artifact.Sha256,
            Evidence = evidence
        };
        fixture.VerificationIndex.Index = VerificationIndex(
            imported.Version.Definition.Version,
            imported.Version.Definition.ManifestDigest,
            [exactChannel]);

        var verified = await fixture.Verification.VerifySnapshotAsync(imported.Version, refreshed.Snapshot, default);
        Assert.AreEqual(SourceVerificationStatus.Verified, verified.Status);
        Assert.AreEqual(evidence, verified.Evidence);

        fixture.VerificationIndex.Index = VerificationIndex(
            imported.Version.Definition.Version,
            imported.Version.Definition.ManifestDigest,
            [exactChannel with { SnapshotDigest = $"sha256:{new string('0', 64)}" }]);
        var changed = await fixture.Verification.VerifySnapshotAsync(imported.Version, refreshed.Snapshot, default);
        Assert.AreEqual(SourceVerificationStatus.Unverified, changed.Status);
        Assert.IsNull(changed.Evidence);
    }

    [TestMethod]
    public void ChannelCompatibilityUsesSemanticPrereleaseOrderingAndExclusiveMaximum()
    {
        var versions = new FakeAgentstrationVersionProvider();
        var evaluator = new SourceChannelCompatibilityEvaluator(versions);
        var channel = CompatibilityChannel("0.2.0-alpha.1", "0.2.0");

        versions.CurrentVersion = "0.2.0-alpha.0";
        Assert.AreEqual(SourceChannelCompatibilityStatus.Incompatible, evaluator.Evaluate(channel).Status);
        versions.CurrentVersion = "0.2.0-alpha.1+build.42";
        Assert.AreEqual(SourceChannelCompatibilityStatus.Compatible, evaluator.Evaluate(channel).Status);
        versions.CurrentVersion = "0.2.0-beta.1";
        Assert.AreEqual(SourceChannelCompatibilityStatus.Compatible, evaluator.Evaluate(channel).Status);
        versions.CurrentVersion = "0.2.0";
        Assert.AreEqual(SourceChannelCompatibilityStatus.Incompatible, evaluator.Evaluate(channel).Status);
        versions.CurrentVersion = null;
        Assert.AreEqual(SourceChannelCompatibilityStatus.CompatibilityUnknown, evaluator.Evaluate(channel).Status);
        var unknownError = Assert.ThrowsExactly<SourceValidationException>(() => evaluator.RequireCompatible(channel));
        Assert.AreEqual("source_channel_compatibility_unknown", unknownError.Code);

        var openEnded = CompatibilityChannel("0.2.0-alpha.1", null);
        versions.CurrentVersion = "99.0.0";
        Assert.AreEqual(SourceChannelCompatibilityStatus.Compatible, evaluator.Evaluate(openEnded).Status);
    }

    [TestMethod]
    public async Task ImportRejectsInvalidCompatibilityVersionsAndIntervalsAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var system = fixture.Context.PushSystem();
        var invalidVersion = Manifest("invalid", "Name", includeChannel: true)
            .Replace("0.2.0-alpha.1", "0.02.0", StringComparison.Ordinal);
        var versionError = await Assert.ThrowsExactlyAsync<SourceValidationException>(() =>
            fixture.Service.ImportYamlAsync(invalidVersion, default));
        Assert.AreEqual("source_compatibility_version_invalid", versionError.Code);

        var missing = Manifest("missing", "Name", includeChannel: true)
            .Replace("      compatibility:\n        agentstration:\n          minVersion: 0.2.0-alpha.1\n", string.Empty, StringComparison.Ordinal);
        var missingError = await Assert.ThrowsExactlyAsync<SourceValidationException>(() =>
            fixture.Service.ImportYamlAsync(missing, default));
        Assert.AreEqual("source_channel_compatibility_missing", missingError.Code);

        var invalidInterval = Manifest("interval", "Name", includeChannel: true)
            .Replace("minVersion: 0.2.0-alpha.1", "minVersion: 0.2.0\n          maxVersionExclusive: 0.2.0-alpha.1", StringComparison.Ordinal);
        var intervalError = await Assert.ThrowsExactlyAsync<SourceValidationException>(() =>
            fixture.Service.ImportYamlAsync(invalidInterval, default));
        Assert.AreEqual("source_compatibility_interval_invalid", intervalError.Code);
    }

    [TestMethod]
    public async Task CompatibilityIsRecalculatedAndBlocksRefreshAndCatalogConsumptionAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var system = fixture.Context.PushSystem();
        await fixture.CreateSourceProviderAsync();
        fixture.Materializer.Content = CatalogArchive(
            ("catalog.yaml", BootstrapCatalog()),
            ("profiles/solution-discovery/fr-FR/profile.yaml", BootstrapProfile("workspace")),
            ("profiles/solution-discovery/en-US/profile.yaml", BootstrapProfile("workspace")));
        fixture.Versions.CurrentVersion = "0.1.0";
        var manifest = ManifestWithCatalog("1").Replace(
            "minVersion: 0.2.0-alpha.1", "minVersion: 0.2.0-alpha.1\n          maxVersionExclusive: 0.3.0", StringComparison.Ordinal);
        var imported = await fixture.Service.ImportYamlAsync(manifest, default);
        _ = await fixture.Bindings.ConfigureAsync(
            "agentstration", "official-samples", imported.Version.Uid, [Selection()], imported.Source.Configuration.ETag!, default);

        var refreshError = await Assert.ThrowsExactlyAsync<SourceValidationException>(() => fixture.Snapshots.RefreshExactAsync(
            ResourceScopeRef.Instance, "agentstration", "official-samples", imported.Version.Uid, "stable", default));
        Assert.AreEqual("source_channel_incompatible", refreshError.Code);
        Assert.AreEqual(0, fixture.Materializer.MaterializeCount);

        fixture.Versions.CurrentVersion = "0.2.0";
        var refreshed = await fixture.Snapshots.RefreshExactAsync(
            ResourceScopeRef.Instance, "agentstration", "official-samples", imported.Version.Uid, "stable", default);
        fixture.Versions.CurrentVersion = "0.3.0";
        var status = await fixture.Snapshots.GetStatusAsync(
            ResourceScopeRef.Instance, "agentstration", "official-samples", imported.Version.Uid, "stable", default);
        var browseError = await Assert.ThrowsExactlyAsync<SourceValidationException>(() => fixture.Catalogs.BrowseAsync(
            ResourceScopeRef.Instance, "agentstration", "official-samples", imported.Version.Uid,
            "stable", refreshed.Snapshot.Uid, null, default));

        Assert.AreEqual(SourceChannelCompatibilityStatus.Incompatible, status.Compatibility.Status);
        Assert.AreEqual("source_running_version_at_or_above_maximum", status.Compatibility.ReasonCode);
        Assert.AreEqual(refreshed.Snapshot.Uid, status.Refresh?.Definition.CurrentSnapshotUid);
        Assert.AreEqual("source_channel_incompatible", browseError.Code);
        Assert.IsNotNull(await fixture.Snapshots.GetAsync(
            ResourceScopeRef.Instance, "agentstration", "official-samples", imported.Version.Uid,
            "stable", refreshed.Snapshot.Uid, default));
    }

    [TestMethod]
    public async Task CatalogDiscoveryMatchesRegistryContractAndPinsLocaleAndProvenanceAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var system = fixture.Context.PushSystem();
        await fixture.CreateSourceProviderAsync();
        fixture.Materializer.Content = CatalogArchive(
            ("catalog.yaml", BootstrapCatalog()),
            ("profiles/solution-discovery/fr-FR/profile.yaml", BootstrapProfile("workspace")),
            ("profiles/solution-discovery/en-US/profile.yaml", BootstrapProfile("workspace")),
            ("unreferenced/catalog.yaml", "not: a catalog"));
        var imported = await fixture.Service.ImportYamlAsync(ManifestWithCatalog("1"), default);
        _ = await fixture.Bindings.ConfigureAsync(
            "agentstration", "official-samples", imported.Version.Uid, [Selection()], imported.Source.Configuration.ETag!, default);
        var refreshed = await fixture.Snapshots.RefreshExactAsync(
            ResourceScopeRef.Instance, "agentstration", "official-samples", imported.Version.Uid, "stable", default);

        var exact = await fixture.Catalogs.BrowseAsync(
            ResourceScopeRef.Instance, "agentstration", "official-samples", imported.Version.Uid,
            "stable", refreshed.Snapshot.Uid, "en-US", default);
        var fallback = await fixture.Catalogs.BrowseAsync(
            ResourceScopeRef.Instance, "agentstration", "official-samples", imported.Version.Uid,
            "stable", refreshed.Snapshot.Uid, "fr-CA", default);

        var catalog = exact.Single();
        var entry = catalog.BootstrapEntries.Single();
        Assert.AreEqual("en-US", entry.ResolvedLocale);
        Assert.AreEqual("profiles/solution-discovery/en-US", entry.ResolvedPath);
        CollectionAssert.AreEquivalent(new[] { "en-US", "fr-FR" }, entry.Variants.Select(value => value.Locale).ToArray());
        Assert.AreEqual("fr-FR", fallback.Single().BootstrapEntries.Single().ResolvedLocale);
        Assert.AreEqual(refreshed.Snapshot.Uid, catalog.Provenance.SnapshotUid);
        Assert.AreEqual(refreshed.Snapshot.Definition.Artifact.Sha256, catalog.Provenance.SnapshotDigest);
        Assert.AreEqual("catalog.yaml", catalog.Provenance.CatalogPath);
    }

    [TestMethod]
    public async Task SourceBootstrapUsesPinnedLocaleAndPersistsCompleteProvenanceAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var system = fixture.Context.PushSystem();
        await fixture.CreateSourceProviderAsync();
        fixture.Materializer.Content = CatalogArchive(
            ("catalog.yaml", BootstrapCatalog()),
            ("profiles/solution-discovery/fr-FR/profile.yaml", BootstrapProfileWithoutBindings("instance")),
            ("profiles/solution-discovery/fr-FR/10-recording.yaml", RecordingResource("accueil-fr")),
            ("profiles/solution-discovery/en-US/profile.yaml", BootstrapProfileWithoutBindings("instance")),
            ("profiles/solution-discovery/en-US/10-recording.yaml", RecordingResource("welcome-en")));
        var imported = await fixture.Service.ImportYamlAsync(ManifestWithCatalog("1"), default);
        _ = await fixture.Bindings.ConfigureAsync(
            "agentstration", "official-samples", imported.Version.Uid, [Selection()], imported.Source.Configuration.ETag!, default);
        var refreshed = await fixture.Snapshots.RefreshExactAsync(
            ResourceScopeRef.Instance, "agentstration", "official-samples", imported.Version.Uid, "stable", default);

        var configuration = new ConfigurationBuilder().Build();
        var localCatalog = new BootstrapProfileCatalog(configuration, new TestHostEnvironment());
        var sourceLoader = new SourceBootstrapProfileLoader(
            localCatalog, fixture.Catalogs, fixture.Service, fixture.Snapshots, fixture.ContentReader);
        var handler = new RecordingBootstrapHandler();
        var bootstrap = new DeclarativeBootstrapService(
            configuration, localCatalog, [handler], NullLogger<DeclarativeBootstrapService>.Instance, sourceLoader);
        var management = new BootstrapProfileManagementService(
            localCatalog, sourceLoader, bootstrap, null!, new AllowPlatformAdministrator(), fixture.Context,
            fixture.Store, new RecordingAuditWriter(), TimeProvider.System, new BootstrapApplicationLock());
        var french = new BootstrapSourceProfileSelection(
            ResourceScopeRef.Instance, "agentstration", "official-samples", imported.Version.Uid, "stable",
            refreshed.Snapshot.Uid, "official-bootstrap-samples", "solution-discovery", "fr-FR",
            "profiles/solution-discovery/fr-FR");
        var selection = new BootstrapProfileSelection([], Source: french);
        var actor = Guid.NewGuid();

        var preview = await management.PreviewAsync(selection, actor, default);
        Assert.AreEqual("fr-FR", preview.SourceProvenance?.Locale);
        Assert.AreEqual("accueil-fr", preview.Resources.Single().Name);

        var englishSelection = selection with
        {
            Source = french with { Locale = "en-US", Path = "profiles/solution-discovery/en-US" }
        };
        var stale = await Assert.ThrowsExactlyAsync<DeclarativeBootstrapException>(() =>
            management.ApplyAsync(englishSelection, preview.Digest, actor, default));
        StringAssert.Contains(stale.Message, "changed after preview");

        fixture.Materializer.Revision = "revision-2";
        fixture.Materializer.Content = CatalogArchive(
            ("catalog.yaml", BootstrapCatalog()),
            ("profiles/solution-discovery/fr-FR/profile.yaml", BootstrapProfileWithoutBindings("instance")),
            ("profiles/solution-discovery/fr-FR/10-recording.yaml", RecordingResource("newer-fr")),
            ("profiles/solution-discovery/en-US/profile.yaml", BootstrapProfileWithoutBindings("instance")),
            ("profiles/solution-discovery/en-US/10-recording.yaml", RecordingResource("newer-en")));
        var newer = await fixture.Snapshots.RefreshExactAsync(
            ResourceScopeRef.Instance, "agentstration", "official-samples", imported.Version.Uid, "stable", default);
        Assert.AreNotEqual(refreshed.Snapshot.Uid, newer.Snapshot.Uid);

        var application = await management.ApplyAsync(selection, preview.Digest, actor, default);
        var provenance = application.Definition.SourceProvenance;
        Assert.IsNotNull(provenance);
        Assert.AreEqual(imported.Source.Source.Uid, provenance.SourceUid);
        Assert.AreEqual(imported.Version.Uid, provenance.SourceVersionUid);
        Assert.AreEqual(imported.Version.Definition.ManifestDigest, provenance.SourceVersionDigest);
        Assert.AreEqual(refreshed.Snapshot.Definition.ResolvedRevision, provenance.ProviderRevision);
        Assert.AreEqual(refreshed.Snapshot.Uid, provenance.SnapshotUid);
        Assert.AreEqual(refreshed.Snapshot.Definition.Artifact.Sha256, provenance.SnapshotDigest);
        Assert.AreEqual("official-bootstrap-samples", provenance.CatalogName);
        Assert.AreEqual("solution-discovery", provenance.EntryName);
        Assert.AreEqual("fr-FR", provenance.Locale);
        Assert.AreEqual("profiles/solution-discovery/fr-FR", provenance.Path);
        CollectionAssert.AreEqual(new[] { "accueil-fr" }, handler.Applied.ToArray());

        var mismatch = await Assert.ThrowsExactlyAsync<DeclarativeBootstrapException>(() =>
            management.PreviewAsync(selection with { Source = french with { Publisher = "different-publisher" } }, actor, default));
        StringAssert.Contains(mismatch.Message, "publisher mismatch is not rewritten");
    }

    [TestMethod]
    public async Task BootstrapTranslationsMustKeepTheSameScopeAndBindingsAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var system = fixture.Context.PushSystem();
        await fixture.CreateSourceProviderAsync();
        fixture.Materializer.Content = CatalogArchive(
            ("catalog.yaml", BootstrapCatalog()),
            ("profiles/solution-discovery/fr-FR/profile.yaml", BootstrapProfile("workspace")),
            ("profiles/solution-discovery/en-US/profile.yaml", BootstrapProfile("tenant")));
        var imported = await fixture.Service.ImportYamlAsync(ManifestWithCatalog("1"), default);
        _ = await fixture.Bindings.ConfigureAsync(
            "agentstration", "official-samples", imported.Version.Uid, [Selection()], imported.Source.Configuration.ETag!, default);
        var refreshed = await fixture.Snapshots.RefreshExactAsync(
            ResourceScopeRef.Instance, "agentstration", "official-samples", imported.Version.Uid, "stable", default);

        var error = await Assert.ThrowsExactlyAsync<SourceValidationException>(() => fixture.Catalogs.BrowseAsync(
            ResourceScopeRef.Instance, "agentstration", "official-samples", imported.Version.Uid,
            "stable", refreshed.Snapshot.Uid, null, default));

        Assert.AreEqual("source_bootstrap_variant_contract_mismatch", error.Code);
    }

    [TestMethod]
    public async Task CatalogContentRejectsTraversalAndSymbolicLinksAsync()
    {
        var store = new MemorySourceSnapshotArtifactStore();
        var reader = new ZipSourceSnapshotContentReader(store);
        var traversal = CatalogArchive(("../catalog.yaml", "value"));
        var traversalReference = await store.SaveAsync(Materialized(traversal), default);
        var traversalError = await Assert.ThrowsExactlyAsync<SourceValidationException>(() => reader.OpenAsync(traversalReference, default));
        Assert.AreEqual("source_path_invalid", traversalError.Code);

        var symbolicLink = CatalogArchive([("catalog.yaml", "target")], symbolicLink: true);
        var symbolicLinkReference = await store.SaveAsync(Materialized(symbolicLink), default);
        var linkError = await Assert.ThrowsExactlyAsync<SourceValidationException>(() => reader.OpenAsync(symbolicLinkReference, default));
        Assert.AreEqual("source_snapshot_link_forbidden", linkError.Code);
    }

    [TestMethod]
    public void CatalogManifestRejectsNonCanonicalAndMissingDefaultLocales()
    {
        var reader = new SourceCatalogManifestReader();
        var nonCanonical = Assert.ThrowsExactly<SourceValidationException>(() => reader.ReadCatalog(
            BootstrapCatalog().Replace("fr-FR", "fr-fr", StringComparison.Ordinal), SourceCatalogKinds.Bootstrap));
        Assert.AreEqual("source_catalog_locale_invalid", nonCanonical.Code);

        var missingDefault = Assert.ThrowsExactly<SourceValidationException>(() => reader.ReadCatalog(
            BootstrapCatalog().Replace("defaultLocale: fr-FR", "defaultLocale: de-DE", StringComparison.Ordinal),
            SourceCatalogKinds.Bootstrap));
        Assert.AreEqual("source_catalog_default_locale_missing", missingDefault.Code);
    }

    [TestMethod]
    public async Task PackCatalogResolvesOnlyDeclaredSnapshotDescendantsAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var system = fixture.Context.PushSystem();
        await fixture.CreateSourceProviderAsync();
        fixture.Materializer.Content = CatalogArchive(
            ("catalogs/packs.yaml", PackCatalog()),
            ("catalogs/packs/who-am-i.zip", "immutable pack bytes"));
        var imported = await fixture.Service.ImportYamlAsync(
            ManifestWithCatalog("1", SourceCatalogKinds.Pack, "catalogs/packs.yaml"), default);
        _ = await fixture.Bindings.ConfigureAsync(
            "agentstration", "official-samples", imported.Version.Uid, [Selection()], imported.Source.Configuration.ETag!, default);
        var refreshed = await fixture.Snapshots.RefreshExactAsync(
            ResourceScopeRef.Instance, "agentstration", "official-samples", imported.Version.Uid, "stable", default);

        var result = await fixture.Catalogs.BrowseAsync(
            ResourceScopeRef.Instance, "agentstration", "official-samples", imported.Version.Uid,
            "stable", refreshed.Snapshot.Uid, null, default);

        Assert.AreEqual("catalogs/packs/who-am-i.zip", result.Single().PackEntries.Single().Path);
    }

    [TestMethod]
    public void SourceResourceFamilySupportsEveryOwnershipScope()
    {
        var kinds = new[]
        {
            ResourceKinds.Source,
            ResourceKinds.SourceVersion,
            ResourceKinds.SourceConfiguration,
            ResourceKinds.SourceObservedState,
            ResourceKinds.SourceImportRecord,
            ResourceKinds.SourceChannelSnapshot,
            ResourceKinds.SourceChannelObservedState
        };

        foreach (var kind in kinds)
            CollectionAssert.AreEquivalent(
                new[] { ResourceScopeKind.Instance, ResourceScopeKind.Tenant, ResourceScopeKind.Workspace },
                ResourceScopePolicy.AllowedScopes(kind).ToArray());
    }

    [TestMethod]
    public async Task ImportIsIdempotentAndRejectsSameVersionWithAnotherDigestAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var system = fixture.Context.PushSystem();
        var first = await fixture.Service.ImportYamlAsync(Manifest("1", "Published name", includeChannel: true), default);
        var repeated = await fixture.Service.ImportYamlAsync(Manifest("1", "Published name", includeChannel: true), default);

        Assert.AreEqual(SourceImportOutcome.Created, first.Outcome);
        Assert.AreEqual(SourceImportOutcome.Unchanged, repeated.Outcome);
        Assert.AreEqual(first.Source.Source.Uid, repeated.Source.Source.Uid);
        Assert.AreEqual(first.Version.Uid, repeated.Version.Uid);
        Assert.AreEqual(1, repeated.Source.VersionCount);

        var conflict = await Assert.ThrowsExactlyAsync<SourceVersionConflictException>(() =>
            fixture.Service.ImportYamlAsync(Manifest("1", "Changed without a version change", includeChannel: false), default));
        StringAssert.Contains(conflict.Message, "different manifest digest");
        Assert.HasCount(1, await fixture.Service.ListVersionsAsync("agentstration", "official-samples", default));

        var records = await fixture.Store.ListAllAsync<SourceImportRecordResource>(ResourceKinds.SourceImportRecord, default);
        CollectionAssert.AreEquivalent(
            new[] { SourceImportOutcome.Created, SourceImportOutcome.Unchanged, SourceImportOutcome.Rejected },
            records.Select(value => value.Value.Definition.Outcome).ToArray());
    }

    [TestMethod]
    public async Task NewVersionKeepsHistoryAndNeverOverwritesLocalDisplayNameAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var system = fixture.Context.PushSystem();
        var imported = await fixture.Service.ImportYamlAsync(Manifest("release-a", "Published name", includeChannel: true), default);
        var configured = await fixture.Service.UpdateDisplayNameAsync(
            "agentstration", "official-samples", "My local name", imported.Source.Configuration.ETag!, default);

        var next = await fixture.Service.ImportYamlAsync(Manifest("release-b", "New published name", includeChannel: false), default);
        Assert.AreEqual("My local name", next.Source.Configuration.Definition.DisplayName);
        Assert.AreEqual(configured.Value.Uid, next.Source.Configuration.Uid);
        Assert.AreEqual(2, next.Source.VersionCount);
        var versions = await fixture.Service.ListVersionsAsync("agentstration", "official-samples", default);
        Assert.IsTrue(versions.Single(value => value.Definition.Version == "release-a").Definition.PublishedDefinition.Channels.Any(value => value.Name == "stable"));
        Assert.IsEmpty(versions.Single(value => value.Definition.Version == "release-b").Definition.PublishedDefinition.Channels);
        Assert.IsFalse(typeof(SourceResource).GetProperties().Any(property => property.Name.Equals("ActiveVersion", StringComparison.OrdinalIgnoreCase)));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => fixture.Store.PutAsync(next.Source.Source, next.Source.Source.ETag, false, default));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => fixture.Store.PutAsync(next.Version, next.Version.ETag, false, default));
    }

    [TestMethod]
    public async Task BindingSelectionIsExplicitReusableAndExcludedFromPublishedVersionsAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var system = fixture.Context.PushSystem();
        await fixture.CreateSourceProviderAsync();
        var first = await fixture.Service.ImportYamlAsync(Manifest("release-a", "Published name", includeChannel: true), default);

        var unresolved = await fixture.Bindings.GetStatusAsync("agentstration", "official-samples", first.Version.Uid, default);
        Assert.IsFalse(unresolved.Ready);
        Assert.AreEqual("unresolved", unresolved.Bindings.Single().Status);

        var configured = await fixture.Bindings.ConfigureAsync(
            "agentstration",
            "official-samples",
            first.Version.Uid,
            [Selection()],
            first.Source.Configuration.ETag!,
            default);
        Assert.IsTrue(configured.Status.Ready);
        Assert.AreEqual("ready", configured.Status.Bindings.Single().Status);
        Assert.AreEqual(ResourceScopeRef.Instance, configured.Configuration.Definition.Bindings.Single().Target.ScopeRef);
        Assert.IsFalse(first.Version.Definition.RawManifest.Contains("git-local", StringComparison.Ordinal));

        var second = await fixture.Service.ImportYamlAsync(Manifest("release-b", "Published name", includeChannel: true), default);
        var reused = await fixture.Bindings.GetStatusAsync("agentstration", "official-samples", second.Version.Uid, default);
        Assert.IsTrue(reused.Ready);
        Assert.AreEqual("git-local", reused.Bindings.Single().Target?.Name);

        var cleared = await fixture.Bindings.ConfigureAsync(
            "agentstration", "official-samples", second.Version.Uid, [], second.Source.Configuration.ETag!, default);
        Assert.IsFalse(cleared.Status.Ready);
        Assert.AreEqual("unresolved", cleared.Status.Bindings.Single().Status);
        var restored = await fixture.Bindings.ConfigureAsync(
            "agentstration", "official-samples", second.Version.Uid, [Selection()], cleared.Configuration.ETag!, default);
        Assert.IsTrue(restored.Status.Ready);

        var removed = await fixture.Service.ImportYamlAsync(Manifest("release-c", "Published name", includeChannel: false), default);
        var removedStatus = await fixture.Bindings.GetStatusAsync("agentstration", "official-samples", removed.Version.Uid, default);
        Assert.IsTrue(removedStatus.Ready);
        Assert.IsEmpty(removedStatus.Bindings);
        Assert.AreEqual("git-distribution", removedStatus.StaleSelections.Single().Name);
    }

    [TestMethod]
    public async Task BindingConfigurationRejectsDuplicateUnknownWrongKindAndMissingSelectionsAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var system = fixture.Context.PushSystem();
        await fixture.CreateSourceProviderAsync();
        var imported = await fixture.Service.ImportYamlAsync(Manifest("1", "Published name", includeChannel: true), default);
        var etag = imported.Source.Configuration.ETag!;

        await AssertCodeAsync("source_binding_selection_duplicate", [Selection(), Selection()]);
        await AssertCodeAsync("source_binding_selection_unknown", [Selection() with { Name = "other" }]);
        await AssertCodeAsync("source_binding_selection_kind_invalid", [Selection() with { TargetKind = "modelProvider" }]);
        await AssertCodeAsync("source_binding_provider_missing", [Selection() with { Target = new("missing") }]);

        async Task AssertCodeAsync(string code, IReadOnlyList<SourceBindingSelection> selections)
        {
            var exception = await Assert.ThrowsExactlyAsync<SourceValidationException>(() => fixture.Bindings.ConfigureAsync(
                "agentstration", "official-samples", imported.Version.Uid, selections, etag, default));
            Assert.AreEqual(code, exception.Code);
        }
    }

    [TestMethod]
    public async Task BindingStatusReportsUnavailableContributionAndSchemaIncompatibilityAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var system = fixture.Context.PushSystem();
        await fixture.CreateSourceProviderAsync();
        var imported = await fixture.Service.ImportYamlAsync(Manifest("1", "Published name", includeChannel: true), default);
        var configured = await fixture.Bindings.ConfigureAsync(
            "agentstration", "official-samples", imported.Version.Uid, [Selection()], imported.Source.Configuration.ETag!, default);
        Assert.IsTrue(configured.Status.Ready);

        fixture.Inspector.Status = "unavailable";
        var unavailable = await fixture.Bindings.GetStatusAsync("agentstration", "official-samples", imported.Version.Uid, default);
        Assert.AreEqual("unavailable", unavailable.Bindings.Single().Status);
        Assert.AreEqual("source_binding_extension_unavailable", unavailable.Bindings.Single().Issues.Single().Code);

        fixture.Inspector.Status = "available";
        fixture.Inspector.IncludeContribution = false;
        var missingContribution = await fixture.Bindings.GetStatusAsync("agentstration", "official-samples", imported.Version.Uid, default);
        Assert.AreEqual("source_binding_contribution_missing", missingContribution.Bindings.Single().Issues.Single().Code);

        fixture.Inspector.IncludeContribution = true;
        fixture.Inspector.SchemaDigest = "sha256:changed";
        var incompatible = await fixture.Bindings.GetStatusAsync("agentstration", "official-samples", imported.Version.Uid, default);
        Assert.AreEqual("incompatible", incompatible.Bindings.Single().Status);
        Assert.AreEqual("source_channel_schema_digest_mismatch", incompatible.Bindings.Single().Issues.Single().Code);
    }

    [TestMethod]
    public async Task ChannelRefreshCreatesImmutableSnapshotAndReturnsStablePinWhenUnchangedAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var system = fixture.Context.PushSystem();
        await fixture.CreateSourceProviderAsync();
        var imported = await fixture.Service.ImportYamlAsync(Manifest("1", "Published name", includeChannel: true), default);
        _ = await fixture.Bindings.ConfigureAsync(
            "agentstration", "official-samples", imported.Version.Uid, [Selection()], imported.Source.Configuration.ETag!, default);

        var first = await fixture.Snapshots.RefreshExactAsync(
            ResourceScopeRef.Instance, "agentstration", "official-samples", imported.Version.Uid, "stable", default);
        var repeated = await fixture.Snapshots.RefreshExactAsync(
            ResourceScopeRef.Instance, "agentstration", "official-samples", imported.Version.Uid, "stable", default);

        Assert.AreEqual(SourceChannelRefreshOutcome.Created, first.Outcome);
        Assert.AreEqual(SourceChannelRefreshOutcome.Unchanged, repeated.Outcome);
        Assert.AreEqual(first.Snapshot.Uid, repeated.Snapshot.Uid);
        Assert.AreEqual(first.Pin, repeated.Pin);
        Assert.AreEqual(1, fixture.Materializer.MaterializeCount);
        Assert.HasCount(1, await fixture.Snapshots.ListAsync(
            ResourceScopeRef.Instance, "agentstration", "official-samples", imported.Version.Uid, "stable", default));
        Assert.AreEqual("git", first.Snapshot.Definition.Provider.ContributionId);
        Assert.AreEqual("revision-1", first.Snapshot.Definition.ResolvedRevision);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            fixture.Store.PutExactAsync(ResourceScopeRef.Instance, first.Snapshot, first.Snapshot.ETag, false, default));
    }

    [TestMethod]
    public async Task MovedRevisionCreatesSnapshotAndFailureRetainsLastUsablePinAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var system = fixture.Context.PushSystem();
        await fixture.CreateSourceProviderAsync();
        var imported = await fixture.Service.ImportYamlAsync(Manifest("1", "Published name", includeChannel: true), default);
        _ = await fixture.Bindings.ConfigureAsync(
            "agentstration", "official-samples", imported.Version.Uid, [Selection()], imported.Source.Configuration.ETag!, default);
        var first = await fixture.Snapshots.RefreshExactAsync(
            ResourceScopeRef.Instance, "agentstration", "official-samples", imported.Version.Uid, "stable", default);

        fixture.Materializer.Revision = "revision-2";
        var moved = await fixture.Snapshots.RefreshExactAsync(
            ResourceScopeRef.Instance, "agentstration", "official-samples", imported.Version.Uid, "stable", default);
        fixture.Materializer.Failure = new SourceRetrievalException("provider_failed", "Provider failed.");
        var error = await Assert.ThrowsExactlyAsync<SourceRetrievalException>(() => fixture.Snapshots.RefreshExactAsync(
            ResourceScopeRef.Instance, "agentstration", "official-samples", imported.Version.Uid, "stable", default));
        var observed = await fixture.Snapshots.GetObservedAsync(
            ResourceScopeRef.Instance, "agentstration", "official-samples", imported.Version.Uid, "stable", default);

        Assert.AreEqual("provider_failed", error.Code);
        Assert.AreNotEqual(first.Snapshot.Uid, moved.Snapshot.Uid);
        Assert.HasCount(2, await fixture.Snapshots.ListAsync(
            ResourceScopeRef.Instance, "agentstration", "official-samples", imported.Version.Uid, "stable", default));
        Assert.IsNotNull(observed);
        Assert.AreEqual(SourceChannelRefreshOutcome.Failed, observed.Definition.LastOutcome);
        Assert.AreEqual(moved.Snapshot.Uid, observed.Definition.CurrentSnapshotUid);
        Assert.AreEqual("provider_failed", observed.Definition.ErrorCode);
    }

    [TestMethod]
    public async Task ConcurrentRefreshMaterializesOnlyOnceAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var system = fixture.Context.PushSystem();
        await fixture.CreateSourceProviderAsync();
        var imported = await fixture.Service.ImportYamlAsync(Manifest("1", "Published name", includeChannel: true), default);
        _ = await fixture.Bindings.ConfigureAsync(
            "agentstration", "official-samples", imported.Version.Uid, [Selection()], imported.Source.Configuration.ETag!, default);
        fixture.Materializer.Delay = TimeSpan.FromMilliseconds(50);

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => fixture.Snapshots.RefreshExactAsync(
            ResourceScopeRef.Instance, "agentstration", "official-samples", imported.Version.Uid, "stable", default)));

        Assert.AreEqual(1, fixture.Materializer.MaterializeCount);
        Assert.AreEqual(1, results.Count(value => value.Outcome == SourceChannelRefreshOutcome.Created));
        Assert.AreEqual(3, results.Count(value => value.Outcome == SourceChannelRefreshOutcome.Unchanged));
        Assert.AreEqual(1, results.Select(value => value.Snapshot.Uid).Distinct().Count());
    }

    [TestMethod]
    public async Task CancelledRefreshPublishesNeitherFailureNorSnapshotAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var system = fixture.Context.PushSystem();
        await fixture.CreateSourceProviderAsync();
        var imported = await fixture.Service.ImportYamlAsync(Manifest("1", "Published name", includeChannel: true), default);
        _ = await fixture.Bindings.ConfigureAsync(
            "agentstration", "official-samples", imported.Version.Uid, [Selection()], imported.Source.Configuration.ETag!, default);
        fixture.Materializer.Delay = TimeSpan.FromSeconds(10);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(25));

        await Assert.ThrowsAsync<OperationCanceledException>(() => fixture.Snapshots.RefreshExactAsync(
            ResourceScopeRef.Instance, "agentstration", "official-samples", imported.Version.Uid, "stable", cancellation.Token));

        Assert.IsNull(await fixture.Snapshots.GetObservedAsync(
            ResourceScopeRef.Instance, "agentstration", "official-samples", imported.Version.Uid, "stable", default));
        Assert.IsEmpty(await fixture.Snapshots.ListAsync(
            ResourceScopeRef.Instance, "agentstration", "official-samples", imported.Version.Uid, "stable", default));
    }

    [TestMethod]
    public async Task SnapshotArtifactStoreIsContentAddressedAndRejectsInvalidIntegrityAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"agentstration-source-artifacts-{Guid.NewGuid():N}");
        try
        {
            var store = new FileSystemSourceSnapshotArtifactStore(directory);
            var content = "archive"u8.ToArray();
            var materialized = new MaterializedSourceRevision(
                "revision", "application/zip", content, content.Length, 1, new("sha256", Digest(content)));
            var first = await store.SaveAsync(materialized, default);
            var repeated = await store.SaveAsync(materialized, default);
            await using var stream = await store.OpenReadAsync(first, default);
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory);

            Assert.AreEqual(first, repeated);
            CollectionAssert.AreEqual(content, memory.ToArray());
            var invalid = materialized with { Integrity = new("sha256", new string('0', 64)) };
            var error = await Assert.ThrowsExactlyAsync<SourceRetrievalException>(() => store.SaveAsync(invalid, default));
            Assert.AreEqual("source_integrity_mismatch", error.Code);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task InvalidLaterDefinitionPreservesVersionsAndExposesObservedFailureAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var system = fixture.Context.PushSystem();
        _ = await fixture.Service.ImportYamlAsync(Manifest("1", "Published name", includeChannel: false), default);
        var invalid = Manifest("2", "Published name", includeChannel: true)
            .Replace("binding: git-distribution", "binding: missing", StringComparison.Ordinal);

        var exception = await Assert.ThrowsExactlyAsync<SourceValidationException>(() => fixture.Service.ImportYamlAsync(invalid, default));
        Assert.AreEqual("source_channel_binding_invalid", exception.Code);
        var source = await fixture.Service.GetAsync("agentstration", "official-samples", default);
        Assert.IsNotNull(source);
        Assert.AreEqual(1, source.VersionCount);
        Assert.AreEqual(SourceImportOutcome.Rejected, source.Observed.Definition.LastOutcome);
        Assert.AreEqual("source_channel_binding_invalid", source.Observed.Definition.ErrorCode);
        Assert.IsNotNull(source.Observed.Definition.LastSuccessfulVersionUid);
    }

    [TestMethod]
    public async Task ImportedVersionsSurviveAStoreRestartAsync()
    {
        var database = Path.Combine(Path.GetTempPath(), $"agentstration-source-{Guid.NewGuid():N}.db");
        try
        {
            await using (var first = await Fixture.CreateAsync(database))
            {
                using var system = first.Context.PushSystem();
                await first.CreateSourceProviderAsync();
                var imported = await first.Service.ImportYamlAsync(Manifest("2026-09", "Official samples", includeChannel: true), default);
                _ = await first.Bindings.ConfigureAsync(
                    "agentstration", "official-samples", imported.Version.Uid, [Selection()], imported.Source.Configuration.ETag!, default);
            }
            await using (var restarted = await Fixture.CreateAsync(database))
            {
                using var system = restarted.Context.PushSystem();
                var source = await restarted.Service.GetAsync("agentstration", "official-samples", default);
                Assert.IsNotNull(source);
                Assert.AreEqual(1, source.VersionCount);
                var version = (await restarted.Service.ListVersionsAsync("agentstration", "official-samples", default)).Single();
                Assert.AreEqual("2026-09", version.Definition.Version);
                Assert.IsTrue((await restarted.Bindings.GetStatusAsync("agentstration", "official-samples", version.Uid, default)).Ready);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(database);
        }
    }

    [TestMethod]
    public void ReaderRequiresOneBoundedNativeEnvelopeAndProducesCanonicalDigest()
    {
        var reader = new SourceManifestReader();
        var parsed = reader.Read(Manifest("1", "Name", includeChannel: false));
        var reordered = reader.Read("""
            definition:
              catalogs: []
              channels: []
              bindings: []
              publisher: { name: agentstration }
              displayName: Name
              version: "1"
            metadata: { name: official-samples }
            kind: SourceVersion
            apiVersion: agentstration.io/v1
            """);
        Assert.AreEqual(parsed.Digest, reordered.Digest);
        Assert.ThrowsExactly<SourceValidationException>(() => reader.Read(Manifest("1", "Name", false) + "\n---\n{}"));
        Assert.ThrowsExactly<SourceValidationException>(() => reader.Read("apiVersion: agentstration.io/v1\nkind: SourceVersion\nmetadata: { name: x }\nspec: {}"));
        Assert.ThrowsExactly<SourceValidationException>(() => reader.Read(Manifest("1", "Name", false).Replace("catalogs: []", "catalogues: []", StringComparison.Ordinal)));
        Assert.ThrowsExactly<SourceValidationException>(() => reader.Read(new string('x', SourceManifestReader.MaximumManifestBytes + 1)));
    }

    [TestMethod]
    public async Task SourceRegistryToolDigestMatchesTheImportedVersionAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var system = fixture.Context.PushSystem();
        var manifest = Manifest("1", "Name", includeChannel: true);
        var imported = await fixture.Service.ImportYamlAsync(manifest, default);
        var directory = Path.Combine(Path.GetTempPath(), "agentstration source registry parity", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "source manifest.yaml");
            await File.WriteAllTextAsync(path, manifest, new UTF8Encoding(false));
            using var output = new StringWriter();
            using var error = new StringWriter();

            var exitCode = await SourceRegistryCli.RunAsync(["source", "digest", path], output, error);

            Assert.AreEqual(SourceRegistryCli.SuccessExitCode, exitCode);
            Assert.AreEqual(imported.Version.Definition.ManifestDigest, output.ToString().Trim());
            Assert.AreEqual(string.Empty, error.ToString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task HttpRetrieverStreamsWithinBoundAndRetainsValidatorsAsync()
    {
        var handler = new StubHttpHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Manifest("1", "Name", false)) };
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"manifest-1\"");
            response.Content.Headers.LastModified = DateTimeOffset.Parse("2026-09-06T20:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
            return response;
        });
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(1) };
        var retrieved = await new HttpSourceManifestRetriever(client).RetrieveAsync(new Uri("https://sources.example/source.yaml"), default);
        Assert.AreEqual("\"manifest-1\"", retrieved.Origin.ETag);
        Assert.AreEqual("https://sources.example/source.yaml", retrieved.Origin.Url);
        await Assert.ThrowsExactlyAsync<SourceRetrievalException>(() =>
            new HttpSourceManifestRetriever(client).RetrieveAsync(new Uri("file:///tmp/source.yaml"), default));
    }

    [TestMethod]
    public async Task VerificationIndexProviderIsOptionalAndRequiresHttpsAsync()
    {
        using var client = new HttpClient(new StubHttpHandler(_ => throw new AssertFailedException("No request was expected.")));
        var reader = new SourceVerificationIndexReader();
        var disabled = new HttpSourceVerificationIndexProvider(client, new SourceVerificationIndexOptions(), reader);
        Assert.IsNull(await disabled.GetAsync(default));

        var insecure = new HttpSourceVerificationIndexProvider(
            client,
            new SourceVerificationIndexOptions { Url = "http://registry.example/verified-sources.yaml" },
            reader);
        var error = await Assert.ThrowsExactlyAsync<SourceValidationException>(() => insecure.GetAsync(default));
        Assert.AreEqual("source_verification_index_url_invalid", error.Code);
    }

    [TestMethod]
    public async Task PlatformApiImportsListsAndUpdatesDisplayNameAsync()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Agentstration:Aep:Transport:AllowedHttpHosts:0", "source-extension.invalid");
        });
        using var client = factory.CreateClient();
        var context = await client.GetFromJsonAsync<ConsoleContextView>("/api/identity/context");
        Assert.IsNotNull(context);
        await factory.Services.GetRequiredService<IIdentityStore>().AddPlatformAdministratorAsync(
            new PlatformAdministrator(context.Context.PrincipalId, DateTimeOffset.UtcNow),
            default);
        var name = $"source-{Guid.NewGuid():N}"[..30];
        var manifest = Manifest("1", "Published name", true).Replace("official-samples", name, StringComparison.Ordinal);
        var response = await client.PostAsJsonAsync("/api/sources/imports/yaml", new ImportSourceYamlRequest(manifest));
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
        var imported = await response.Content.ReadFromJsonAsync<SourceImportResult>();
        Assert.IsNotNull(imported);
        Assert.AreEqual("Published name", imported.Source.Configuration.Definition.DisplayName);
        var workspaceScope = ResourceScopeRef.Workspace(context.Context.WorkspaceId);
        Assert.AreEqual(workspaceScope, imported.Source.Source.ScopeRef);
        Assert.AreEqual(SourceVerificationStatus.Unverified, imported.Verification.Status);
        var verification = await client.GetFromJsonAsync<SourceDefinitionVerificationView>(
            $"/api/sources/agentstration/{name}/versions/{imported.Version.Uid:D}/verification?scopeRef={Uri.EscapeDataString(workspaceScope.ToString())}");
        Assert.IsNotNull(verification);
        Assert.AreEqual("agentstration", verification.DeclaredPublisher.Name);
        Assert.IsNull(verification.VerifiedPublisher);

        var instanceResponse = await client.PostAsJsonAsync(
            "/api/sources/imports/yaml",
            new ImportSourceYamlRequest(manifest, ResourceScopeRef.Instance));
        Assert.AreEqual(HttpStatusCode.Created, instanceResponse.StatusCode);
        var instance = await instanceResponse.Content.ReadFromJsonAsync<SourceImportResult>();
        Assert.IsNotNull(instance);
        Assert.AreEqual(ResourceScopeRef.Instance, instance.Source.Source.ScopeRef);
        Assert.AreNotEqual(imported.Source.Source.Uid, instance.Source.Source.Uid);

        using var update = new HttpRequestMessage(
            HttpMethod.Put,
            $"/api/sources/agentstration/{name}/display-name?scopeRef={Uri.EscapeDataString(workspaceScope.ToString())}")
        {
            Content = JsonContent.Create(new UpdateSourceDisplayNameRequest("Local name"))
        };
        update.Headers.TryAddWithoutValidation("If-Match", imported.Source.Configuration.ETag);
        var updatedResponse = await client.SendAsync(update);
        updatedResponse.EnsureSuccessStatusCode();
        var updated = await updatedResponse.Content.ReadFromJsonAsync<SourceConfigurationResource>();
        Assert.AreEqual("Local name", updated?.Definition.DisplayName);

        var providerName = $"git-{name}";
        using (factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushSystem())
        {
            await factory.Services.GetRequiredService<ExtensionRegistrationManagementService>().CreateAsync(new ExtensionRegistrationResource
            {
                ApiVersion = ManagementApiVersions.CoreV1,
                Kind = ResourceKinds.ExtensionRegistration,
                Metadata = new ResourceMetadata { Name = providerName },
                ScopeRef = ResourceScopeRef.Instance,
                Definition = new ExtensionRegistrationProperties
                {
                    DisplayName = "Disabled test source extension",
                    Endpoint = new Uri("http://source-extension.invalid/"),
                    Enabled = false,
                    Source = ExtensionRegistrationSource.Configuration
                }
            }, default);
            await factory.Services.GetRequiredService<SourceProviderManagementService>().CreateAsync(new SourceProviderResource
            {
                ApiVersion = ManagementApiVersions.CoreV1,
                Kind = ResourceKinds.SourceProvider,
                Metadata = new ResourceMetadata { Name = providerName },
                ScopeRef = ResourceScopeRef.Instance,
                Definition = new SourceProviderProperties
                {
                    DisplayName = "Local Git",
                    Extension = new(providerName, ResourceScopeRef.Instance, ResourceNamespace.Default),
                    ContributionId = "git"
                }
            }, default);
        }

        using var configure = new HttpRequestMessage(
            HttpMethod.Put,
            $"/api/sources/agentstration/{name}/versions/{imported.Version.Uid:D}/bindings?scopeRef={Uri.EscapeDataString(workspaceScope.ToString())}")
        {
            Content = JsonContent.Create(new ConfigureSourceBindingsRequest([Selection() with
            {
                Target = new(providerName, ResourceScopeRef.Instance, ResourceNamespace.Default)
            }]))
        };
        configure.Headers.TryAddWithoutValidation("If-Match", updated?.ETag);
        var configureResponse = await client.SendAsync(configure);
        Assert.AreEqual(HttpStatusCode.OK, configureResponse.StatusCode, await configureResponse.Content.ReadAsStringAsync());
        var bindingResult = await configureResponse.Content.ReadFromJsonAsync<SourceBindingConfigurationResult>();
        Assert.IsNotNull(bindingResult);
        Assert.AreEqual(workspaceScope, bindingResult.Configuration.ScopeRef);
        Assert.AreEqual("unavailable", bindingResult.Status.Bindings.Single().Status);
    }

    [TestMethod]
    public async Task SourceBindingApiRequiresPlatformAdministratorAsync()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.UseEnvironment("Testing"));
        using var client = factory.CreateClient();
        SourceImportResult imported;
        using (factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushSystem())
        {
            imported = await factory.Services.GetRequiredService<SourceManagementService>().ImportYamlAsync(
                Manifest("1", "Published name", includeChannel: true),
                ResourceScopeRef.Instance,
                default);
        }

        var response = await client.GetAsync($"/api/sources/agentstration/official-samples/versions/{imported.Version.Uid:D}/bindings?scopeRef=%2Finstance");
        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private static string Manifest(string version, string displayName, bool includeChannel) => $$"""
        apiVersion: agentstration.io/v1
        kind: SourceVersion
        metadata:
          name: official-samples
        definition:
          version: "{{version}}"
          displayName: {{displayName}}
          publisher:
            name: agentstration
          bindings:{{(includeChannel ? "\n    - name: git-distribution\n      targetKind: sourceProvider" : " []")}}
          channels:{{(includeChannel ? "\n    - name: stable\n      compatibility:\n        agentstration:\n          minVersion: 0.2.0-alpha.1\n      provider:\n        binding: git-distribution\n      configuration:\n        optionSet: git/source-channel\n        version: \"1.0\"\n        schemaDigest: sha256:test\n        values: {}" : " []")}}
          catalogs: []
        """;

    private static string ManifestWithCatalog(
        string version,
        string kind = SourceCatalogKinds.Bootstrap,
        string path = "catalog.yaml") => Manifest(version, "Published name", includeChannel: true)
        .Replace("catalogs: []", $"catalogs:\n    - kind: {kind}\n      path: {path}", StringComparison.Ordinal);

    private static string BootstrapCatalog() => """
        apiVersion: agentstration.io/v1
        kind: BootstrapCatalog
        metadata:
          name: official-bootstrap-samples
        definition:
          displayName: Agentstration Bootstrap samples
          description: Official profiles for discovering and demonstrating Agentstration.
          entries:
            - name: solution-discovery
              defaultLocale: fr-FR
              variants:
                - locale: fr-FR
                  path: profiles/solution-discovery/fr-FR
                - locale: en-US
                  path: profiles/solution-discovery/en-US
        """;

    private static string BootstrapProfile(string targetScope) => $$"""
        apiVersion: agentstration.io/v1
        kind: BootstrapProfile
        metadata:
          name: solution-discovery
        definition:
          targetScope: {{targetScope}}
          bindings:
            - name: agent-model
              targetKind: modelProfile
              required: true
        """;

    private static string RecordingResource(string name) => $$"""
        apiVersion: agentstration.io/v1
        kind: Recording
        metadata:
          name: {{name}}
        definition: {}
        """;

    private static string BootstrapProfileWithoutBindings(string targetScope) => $$"""
        apiVersion: agentstration.io/v1
        kind: BootstrapProfile
        metadata:
          name: solution-discovery
        definition:
          targetScope: {{targetScope}}
          bindings: []
        """;

    private static string PackCatalog() => """
        apiVersion: agentstration.io/v1
        kind: PackCatalog
        metadata:
          name: official-packs
        definition:
          displayName: Official Packs
          entries:
            - name: who-am-i
              displayName: Who am I
              path: packs/who-am-i.zip
        """;

    private static byte[] CatalogArchive(params (string Path, string Content)[] entries) => CatalogArchive(entries, symbolicLink: false);

    private static byte[] CatalogArchive((string Path, string Content)[] entries, bool symbolicLink)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var item in entries)
            {
                var entry = archive.CreateEntry(item.Path);
                if (symbolicLink) entry.ExternalAttributes = unchecked((int)0xA0000000);
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write(item.Content);
            }
        }
        return stream.ToArray();
    }

    private static MaterializedSourceRevision Materialized(byte[] content) => new(
        "revision", "application/zip", content, content.Length * 10L, 100, new("sha256", Digest(content)));

    private static SourceBindingSelection Selection() => new()
    {
        Name = "git-distribution",
        TargetKind = SourceKinds.SourceProvider,
        Target = new("git-local", @namespace: ResourceNamespace.Default)
    };

    private static SourceChannelDefinition CompatibilityChannel(string minimum, string? maximum) => new()
    {
        Name = "stable",
        Compatibility = new() { Agentstration = new() { MinVersion = minimum, MaxVersionExclusive = maximum } },
        Provider = new() { Binding = "git-distribution" },
        Configuration = new()
        {
            OptionSet = "git/source-channel",
            Version = "1.0",
            SchemaDigest = "sha256:test"
        }
    };

    private static VerifiedSourceIndexManifest VerificationIndex(
        string version,
        string manifestDigest,
        IReadOnlyList<VerifiedSourceChannelDefinition>? channels = null) => new()
        {
            ApiVersion = ManagementApiVersions.CoreV1,
            Kind = SourceVerificationKinds.VerifiedSourceIndex,
            Definition = new VerifiedSourceIndexDefinition
            {
                Sources = [new VerifiedSourceDefinition
                {
                    Source = new() { Publisher = "agentstration", Name = "official-samples" },
                    Version = version,
                    ManifestDigest = manifestDigest,
                    Publisher = new() { Name = "agentstration", DisplayName = "Agentstration" },
                    ManifestLocations = [new() { Url = "https://registry.example/source.yaml", Mutable = true }],
                    Evidence = new() { Type = "official-static-index", Authority = "agentstration" },
                    Channels = channels ?? []
                }]
            }
        };

    private sealed class StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response(request));
    }

    private sealed class StubRetriever : ISourceManifestRetriever
    {
        public string? Content { get; set; }

        public Task<RetrievedSourceManifest> RetrieveAsync(Uri source, CancellationToken cancellationToken) =>
            Content is null
                ? throw new SourceRetrievalException("not_configured", "No test HTTP source is configured.")
                : Task.FromResult(new RetrievedSourceManifest(Content, new SourceManifestOrigin { Url = source.AbsoluteUri }));
    }

    private sealed class FakeSourceVerificationIndexProvider : ISourceVerificationIndexProvider
    {
        public VerifiedSourceIndexManifest? Index { get; set; }
        public Exception? Failure { get; set; }

        public Task<VerifiedSourceIndexManifest?> GetAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Failure is null ? Task.FromResult(Index) : Task.FromException<VerifiedSourceIndexManifest?>(Failure);
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly ServiceProvider services;
        private readonly bool ownsDatabase;
        private readonly string database;
        public SourceManagementService Service => services.GetRequiredService<SourceManagementService>();
        public CurrentRequestContext Context => services.GetRequiredService<CurrentRequestContext>();
        public IControlPlaneStore Store => services.GetRequiredService<IControlPlaneStore>();
        public SourceBindingManagementService Bindings => services.GetRequiredService<SourceBindingManagementService>();
        public SourceChannelSnapshotService Snapshots => services.GetRequiredService<SourceChannelSnapshotService>();
        public SourceCatalogService Catalogs => services.GetRequiredService<SourceCatalogService>();
        public ISourceSnapshotContentReader ContentReader => services.GetRequiredService<ISourceSnapshotContentReader>();
        public SourceVerificationService Verification => services.GetRequiredService<SourceVerificationService>();
        public FakeSourceVerificationIndexProvider VerificationIndex => services.GetRequiredService<FakeSourceVerificationIndexProvider>();
        public StubRetriever Retriever => services.GetRequiredService<StubRetriever>();
        public FakeSourceProviderMaterializer Materializer => services.GetRequiredService<FakeSourceProviderMaterializer>();
        public FakeAgentstrationVersionProvider Versions => services.GetRequiredService<FakeAgentstrationVersionProvider>();
        public FakeExtensionInspector Inspector => services.GetRequiredService<FakeExtensionInspector>();

        private Fixture(ServiceProvider services, string database, bool ownsDatabase)
        {
            this.services = services;
            this.database = database;
            this.ownsDatabase = ownsDatabase;
        }

        public static async Task<Fixture> CreateAsync(string? database = null)
        {
            var ownsDatabase = database is null;
            database ??= Path.Combine(Path.GetTempPath(), $"agentstration-source-{Guid.NewGuid():N}.db");
            var collection = new ServiceCollection();
            collection.AddSingleton(TimeProvider.System);
            collection.AddSingleton<CurrentRequestContext>();
            collection.AddSingleton<ICurrentRequestContext>(provider => provider.GetRequiredService<CurrentRequestContext>());
            collection.AddSingleton<IRequestContextScopeFactory>(provider => provider.GetRequiredService<CurrentRequestContext>());
            collection.AddSqliteControlPlane($"Data Source={database}");
            collection.AddSingleton(provider => new ResourceScopeOperationService(
                provider.GetRequiredService<CurrentRequestContext>(),
                provider.GetRequiredService<CurrentRequestContext>(),
                null!,
                null!,
                null!,
                provider.GetRequiredService<IResourceScopeResolver>()));
            collection.AddSingleton<IResourceReferenceResolver, ResourceReferenceResolver>();
            collection.AddSingleton<SourceManifestValidator>();
            collection.AddSingleton<ISourceManifestReader, SourceManifestReader>();
            collection.AddSingleton<StubRetriever>();
            collection.AddSingleton<ISourceManifestRetriever>(provider => provider.GetRequiredService<StubRetriever>());
            collection.AddSingleton<FakeSourceVerificationIndexProvider>();
            collection.AddSingleton<ISourceVerificationIndexProvider>(provider => provider.GetRequiredService<FakeSourceVerificationIndexProvider>());
            collection.AddSingleton<SourceVerificationService>();
            collection.AddSingleton<SourceManagementService>();
            collection.AddSingleton<ExtensionRegistrationManagementService>();
            collection.AddSingleton<SourceProviderManagementService>();
            collection.AddSingleton<FakeExtensionInspector>();
            collection.AddSingleton<IExtensionInspector>(provider => provider.GetRequiredService<FakeExtensionInspector>());
            collection.AddSingleton<SourceBindingManagementService>();
            collection.AddSingleton<FakeAgentstrationVersionProvider>();
            collection.AddSingleton<IAgentstrationVersionProvider>(provider => provider.GetRequiredService<FakeAgentstrationVersionProvider>());
            collection.AddSingleton<SourceChannelCompatibilityEvaluator>();
            collection.AddSingleton<FakeSourceProviderMaterializer>();
            collection.AddSingleton<ISourceProviderMaterializer>(provider => provider.GetRequiredService<FakeSourceProviderMaterializer>());
            collection.AddSingleton<ISourceSnapshotArtifactStore, MemorySourceSnapshotArtifactStore>();
            collection.AddSingleton<ISourceSnapshotContentReader, ZipSourceSnapshotContentReader>();
            collection.AddSingleton<ISourceCatalogManifestReader, SourceCatalogManifestReader>();
            collection.AddSingleton(new SourceMaterializationLimits());
            collection.AddSingleton<SourceChannelSnapshotService>();
            collection.AddSingleton<SourceCatalogService>();
            var services = collection.BuildServiceProvider();
            var fixture = new Fixture(services, database, ownsDatabase);
            using (fixture.Context.PushSystem()) await fixture.Store.InitializeAsync(default);
            return fixture;
        }

        public async Task CreateSourceProviderAsync()
        {
            var registrations = services.GetRequiredService<ExtensionRegistrationManagementService>();
            await registrations.CreateAsync(new ExtensionRegistrationResource
            {
                ApiVersion = ManagementApiVersions.CoreV1,
                Kind = ResourceKinds.ExtensionRegistration,
                Metadata = new ResourceMetadata { Name = "source-extension" },
                ScopeRef = ResourceScopeRef.Instance,
                Definition = new ExtensionRegistrationProperties
                {
                    DisplayName = "Source extension",
                    Endpoint = new Uri("http://source-extension/"),
                    Source = ExtensionRegistrationSource.Configuration
                }
            }, default);
            await services.GetRequiredService<SourceProviderManagementService>().CreateAsync(new SourceProviderResource
            {
                ApiVersion = ManagementApiVersions.CoreV1,
                Kind = ResourceKinds.SourceProvider,
                Metadata = new ResourceMetadata { Name = "git-local" },
                ScopeRef = ResourceScopeRef.Instance,
                Definition = new SourceProviderProperties
                {
                    DisplayName = "Local Git",
                    Extension = new("source-extension", ResourceScopeRef.Instance, ResourceNamespace.Default),
                    ContributionId = "git"
                }
            }, default);
        }

        public async ValueTask DisposeAsync()
        {
            await services.DisposeAsync();
            if (!ownsDatabase) return;
            SqliteConnection.ClearAllPools();
            File.Delete(database);
        }
    }

    private sealed class RecordingBootstrapHandler : IBootstrapResourceHandler
    {
        public string Kind => "Recording";
        public BootstrapProfileScope Scope => BootstrapProfileScope.Instance;
        public List<string> Applied { get; } = [];

        public Task<BootstrapResourcePlanResult> PlanAsync(
            BootstrapResourceDocument resource,
            BootstrapResourceOperationContext operation,
            BootstrapPlanningContext planning,
            CancellationToken cancellationToken) =>
            Task.FromResult(new BootstrapResourcePlanResult(BootstrapResourceDisposition.Create));

        public Task<BootstrapResourceApplyResult> ApplyAsync(
            BootstrapResourceDocument resource,
            BootstrapResourceOperationContext operation,
            CancellationToken cancellationToken)
        {
            Applied.Add(resource.Metadata.Name);
            return Task.FromResult(BootstrapResourceApplyResult.Created);
        }
    }

    private sealed class AllowPlatformAdministrator : IPlatformAuthorizationService
    {
        public Task<bool> IsPlatformAdministratorAsync(Guid principalId, CancellationToken cancellationToken) =>
            Task.FromResult(true);
    }

    private sealed class RecordingAuditWriter : ISecurityAuditWriter
    {
        public Task WriteAsync(SecurityAuditWrite entry, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = nameof(SourceTests);
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class FakeExtensionInspector : IExtensionInspector
    {
        private static readonly System.Text.Json.JsonElement Schema = System.Text.Json.JsonDocument.Parse("""
            { "type": "object", "additionalProperties": true }
            """).RootElement.Clone();

        public string Status { get; set; } = "available";
        public bool IncludeContribution { get; set; } = true;
        public string SchemaDigest { get; set; } = "sha256:test";
        public bool CanHandle(string providerType) => true;
        public bool CanInspectEndpoint(Uri endpoint) => true;
        public ValueTask<ExtensionInspection> InspectAsync(ModelProviderConfiguration provider, CancellationToken cancellationToken = default) =>
            InspectAsync(provider.Name, provider.Endpoint, cancellationToken);
        public ValueTask<ExtensionInspection> InspectAsync(string registrationName, Uri endpoint, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ExtensionInspection(
                registrationName,
                endpoint,
                Status,
                new("source-extension", "Source extension", "1.0.0", null),
                IncludeContribution ? [new("source-provider", "git")] : [],
                [new("git/source-channel", "source-provider", "git", ExtensionOptionScopes.SourceChannel, "1.0", [new("1.0", SchemaDigest, Schema, false)])],
                Status == "available" ? null : "The extension is unavailable."));
    }

    private sealed class FakeSourceProviderMaterializer : ISourceProviderMaterializer
    {
        public byte[] Content { get; set; } = "source archive"u8.ToArray();
        public string Revision { get; set; } = "revision-1";
        public Exception? Failure { get; set; }
        public TimeSpan Delay { get; set; }
        public int MaterializeCount { get; private set; }

        public async Task<ResolvedSourceRevision> ResolveAsync(SourceProviderInvocation invocation, CancellationToken cancellationToken)
        {
            if (Delay > TimeSpan.Zero) await Task.Delay(Delay, cancellationToken);
            if (Failure is not null) throw Failure;
            return new(Revision, new("sha256", Digest(Encoding.UTF8.GetBytes(Revision))));
        }

        public Task<MaterializedSourceRevision> MaterializeAsync(SourceProviderInvocation invocation, string revision, SourceMaterializationLimits limits, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Failure is not null) throw Failure;
            MaterializeCount++;
            return Task.FromResult(new MaterializedSourceRevision(
                revision, "application/zip", Content, Content.Length * 10L, 100, new("sha256", Digest(Content))));
        }
    }

    private sealed class FakeAgentstrationVersionProvider : IAgentstrationVersionProvider
    {
        public string? CurrentVersion { get; set; } = "0.2.0-alpha.1";
    }

    private sealed class MemorySourceSnapshotArtifactStore : ISourceSnapshotArtifactStore
    {
        private readonly Dictionary<string, byte[]> values = new(StringComparer.Ordinal);

        public Task<SourceSnapshotArtifactReference> SaveAsync(MaterializedSourceRevision content, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var digest = Digest(content.Content.Span);
            values.TryAdd(digest, content.Content.ToArray());
            return Task.FromResult(new SourceSnapshotArtifactReference(
                digest, content.MediaType, digest, content.Content.Length, content.ExpandedBytes, content.EntryCount));
        }

        public Task<Stream> OpenReadAsync(SourceSnapshotArtifactReference reference, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<Stream>(new MemoryStream(values[reference.StorageKey], writable: false));
        }
    }

    private static string Digest(ReadOnlySpan<byte> content) => Convert.ToHexStringLower(SHA256.HashData(content));
}
