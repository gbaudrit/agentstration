using System.Text.Json;
using Agentstration.ConnectedServices.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Storage.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Agentstration.Storage.Tests;

[TestClass]
public sealed class StorageManagementTests
{
    [TestMethod]
    public async Task LocalStorageResolvesWithoutConnectedServiceOrExportInspection()
    {
        var f = new StorageFixture();
        f.Inspector.Error = "must_not_be_called";
        var type = await f.TypeAsync();
        var stored = await f.Storages.CreateAsync(StorageFixture.Storage(type), default);
        var resolved = await f.Storages.ResolveAsync(StorageFixture.Revision(stored.Value), new(StorageFixture.Tenant, stored.Value.Address));
        Assert.IsNull(resolved.ConnectedService);
        Assert.AreEqual(StorageAvailability.Ready, stored.Value.ObservedState?.Availability);
    }

    [TestMethod]
    public async Task ManualMappingsRemainExplicitAndSharedServiceDoesNotMergeLogicalAuthorities()
    {
        var f = new StorageFixture();
        var remote = await f.RemoteAsync();
        var first = await f.Storages.CreateAsync(remote, default);
        var second = await f.Storages.CreateAsync(remote with { Metadata = new() { Name = "second" } }, default);
        Assert.AreNotEqual(first.Value.Uid, second.Value.Uid);
        Assert.AreEqual(first.Value.Definition.ConnectedService, second.Value.Definition.ConnectedService);
        Assert.HasCount(1, second.Value.Definition.Bindings);
        Assert.AreNotEqual(StorageFixture.Revision(first.Value), StorageFixture.Revision(second.Value));
        var usage = await new StorageConnectedServiceUsageProvider(f.Store, f.Context).GetUsagesAsync(new(StorageFixture.Tenant,
            ResourceNamespace.Default, ConnectedServiceResourceKinds.ConnectedService, "account"));
        Assert.HasCount(2, usage);
    }

    [TestMethod]
    public async Task ProviderRequirementIdentifiersArePreservedAndBoundExplicitly()
    {
        var f = new StorageFixture();
        var remote = await f.RemoteAsync();
        var type = (await f.Types.GetExactAsync(StorageFixture.Tenant, ResourceNamespace.Default, "remote", default))!;
        var version = type.Value.Definition.Versions.Single() with
        {
            Version = "2.0.0",
            Requirements = [new("access_key_id", true, ConnectedServiceValueType.Text, ConnectedServiceValueProtection.Secured)]
        };
        var updated = await f.Types.PutExactAsync(StorageFixture.Tenant, type.Value.Namespace, type.Value.Name,
            type.Value.Definition with { Versions = [version], PreferredVersion = version.Version }, type.ETag, default);
        var stored = await f.Storages.CreateAsync(remote with { Definition = remote.Definition with
        {
            Type = StorageFixture.Reference(updated.Value),
            Bindings = [new("access_key_id", remote.Definition.Bindings.Single().Export)]
        } }, default);
        Assert.AreEqual("access_key_id", stored.Value.Definition.Bindings.Single().RequirementId);
        await f.Storages.ResolveAsync(StorageFixture.Revision(stored.Value), new(StorageFixture.Tenant, stored.Value.Address));
    }

    [TestMethod]
    public async Task CompatibleNamesNeverCreateBindingsAutomatically()
    {
        var f = new StorageFixture();
        var remote = await f.RemoteAsync();
        f.Inspector.Metadata = f.Inspector.Metadata with
        { Exports = [f.Inspector.Metadata.Exports.Single() with { Id = "connection" }] };
        await Failure("storage_mapping_incomplete", () => f.Storages.CreateAsync(remote with
        { Definition = remote.Definition with { Bindings = [] } }, default));
    }

    [TestMethod]
    [DataRow("type")]
    [DataRow("protection")]
    [DataRow("format")]
    public async Task RequirementTypeProtectionAndFormatMustMatch(string mismatch)
    {
        var f = new StorageFixture();
        var remote = await f.RemoteAsync();
        var export = f.Inspector.Metadata.Exports.Single();
        if (mismatch == "type") export = export with { Type = ConnectedServiceValueType.Logical };
        if (mismatch == "protection") export = export with { Protection = ConnectedServiceValueProtection.Standard };
        if (mismatch == "format")
        {
            var type = (await f.Types.GetExactAsync(StorageFixture.Tenant, ResourceNamespace.Default, "remote", default))!;
            var v = type.Value.Definition.Versions.Single() with
            { Requirements = [new("connection", true, ConnectedServiceValueType.Text, ConnectedServiceValueProtection.Secured, "uri")] };
            var changed = type.Value with { Definition = type.Value.Definition with { Versions = [v] } };
            await f.Store.PutExactAsync(StorageFixture.Tenant, changed, type.ETag, false, default);
            remote = remote with { Definition = remote.Definition with { Type = StorageFixture.Reference(changed) } };
        }
        f.Inspector.Metadata = f.Inspector.Metadata with { Exports = [export] };
        await Failure("storage_export_incompatible", () => f.Storages.CreateAsync(remote with
        { Definition = remote.Definition with { Bindings = [new("connection", export)] } }, default));
    }

    [TestMethod]
    [DataRow("missing")]
    [DataRow("stale")]
    public async Task MissingAndStaleExportDescriptorsAreRejected(string state)
    {
        var f = new StorageFixture();
        var remote = await f.RemoteAsync();
        f.Inspector.Metadata = f.Inspector.Metadata with
        { Exports = state == "missing" ? [] : [f.Inspector.Metadata.Exports.Single() with { Format = "changed" }] };
        await Failure("storage_export_stale", () => f.Storages.CreateAsync(remote, default));
    }

    [TestMethod]
    [DataRow("duplicate")]
    [DataRow("unknown")]
    public async Task DuplicateAndUndeclaredRequirementsCannotBeBound(string state)
    {
        var f = new StorageFixture();
        var remote = await f.RemoteAsync();
        var binding = remote.Definition.Bindings.Single();
        await Failure("storage_mapping_invalid", () => f.Storages.CreateAsync(remote with
        { Definition = remote.Definition with { Bindings = state == "duplicate" ? [binding, binding] : [binding with { RequirementId = "unknown" }] } }, default));
    }

    [TestMethod]
    public async Task ForeignScopeCannotUseAStorageTypeEvenWithAnAccessGrant()
    {
        var f = new StorageFixture();
        var type = await f.TypeAsync(scope: StorageFixture.Foreign);
        await Failure("storage_access_denied", () => f.Storages.CreateAsync(StorageFixture.Storage(type), default));
    }

    [TestMethod]
    public async Task ForeignConnectedServiceCannotBeSelectedEvenWithCompatibleExports()
    {
        var f = new StorageFixture();
        var remote = await f.RemoteAsync();
        var stored = (await f.Store.GetExactAsync<ConnectedServiceResource>(new(StorageFixture.Tenant,
            ResourceNamespace.Default, ConnectedServiceResourceKinds.ConnectedService, "account"), default))!;
        var foreign = await f.Store.PutExactAsync(StorageFixture.Foreign, stored.Value with { ScopeRef = StorageFixture.Foreign }, null, true, default);
        await Failure("storage_access_denied", () => f.Storages.CreateAsync(remote with
        {
            Definition = remote.Definition with { ConnectedService = remote.Definition.ConnectedService! with
            { Service = new(foreign.Value.Address, StorageFixture.Foreign), ServiceUid = foreign.Value.Uid } }
        }, default));
    }

    [TestMethod]
    public async Task ResolverRejectsForgedConsumerScopeUnderAuthenticatedContext()
    {
        var f = new StorageFixture();
        var resource = await f.Storages.CreateAsync(StorageFixture.Storage(await f.TypeAsync()), default);
        f.Context.AccessMode = ControlPlaneAccessMode.Tenant;
        await Failure("storage_access_denied", () => f.Storages.ResolveAsync(StorageFixture.Revision(resource.Value),
            new(StorageFixture.Foreign, resource.Value.Address)));
    }

    [TestMethod]
    public async Task ChangedRequirementContractCannotReuseOldTypeEvidence()
    {
        var f = new StorageFixture();
        var remote = await f.RemoteAsync();
        var saved = await f.Storages.CreateAsync(remote, default);
        var type = (await f.Types.GetExactAsync(StorageFixture.Tenant, ResourceNamespace.Default, "remote", default))!;
        var changed = type.Value with { Definition = type.Value.Definition with
        { Versions = [type.Value.Definition.Versions.Single() with { Requirements = [] }] } };
        await f.Store.PutExactAsync(StorageFixture.Tenant, changed, type.ETag, false, default);
        await Failure("storage_type_stale", () => f.Storages.ResolveAsync(StorageFixture.Revision(saved.Value), new(StorageFixture.Tenant, saved.Value.Address)));
    }

    [TestMethod]
    public async Task DescendantNeedsBothUsePolicyAndCurrentResourceAccessGrant()
    {
        var f = new StorageFixture();
        var type = await f.TypeAsync();
        await Failure("storage_access_denied", () => f.Storages.CreateAsync(StorageFixture.Storage(type, scope: StorageFixture.Workspace), default));
        var updated = await f.Types.PutExactAsync(StorageFixture.Tenant, type.Namespace, type.Name,
            type.Definition with { UsePolicy = new() { Grants = [new(StorageFixture.Workspace)] } }, type.ETag, default);
        f.Access.Allowed = false;
        await Failure("storage_access_denied", () => f.Storages.CreateAsync(StorageFixture.Storage(updated.Value, scope: StorageFixture.Workspace), default));
        f.Access.Allowed = true;
        var stored = await f.Storages.CreateAsync(StorageFixture.Storage(updated.Value, scope: StorageFixture.Workspace), default);
        Assert.AreEqual(StorageFixture.Workspace, stored.Value.ScopeRef);
    }

    [TestMethod]
    public async Task ResolverRechecksUsePolicyAndRevokedAuthorization()
    {
        var f = new StorageFixture();
        var resource = StorageFixture.Storage(await f.TypeAsync());
        var stored = await f.Storages.CreateAsync(resource with
        { Definition = resource.Definition with { UsePolicy = new() { Grants = [new(StorageFixture.Workspace)] } } }, default);
        await f.Storages.ResolveAsync(StorageFixture.Revision(stored.Value), new(StorageFixture.Workspace, stored.Value.Address));
        f.Access.Allowed = false;
        await Failure("storage_access_denied", () => f.Storages.ResolveAsync(StorageFixture.Revision(stored.Value), new(StorageFixture.Workspace, stored.Value.Address)));
    }

    [TestMethod]
    [DataRow("storage_service_unavailable")]
    [DataRow("storage_service_stale")]
    [DataRow("storage_service_access_denied")]
    public async Task DisabledRecreatedAndInaccessibleServicesFailWithoutRebinding(string error)
    {
        var f = new StorageFixture();
        var stored = await f.Storages.CreateAsync(await f.RemoteAsync(), default);
        f.Inspector.Error = error;
        await Failure(error, () => f.Storages.ResolveAsync(StorageFixture.Revision(stored.Value), new(StorageFixture.Tenant, stored.Value.Address)));
        var current = await f.Storages.GetExactAsync(StorageFixture.Tenant, stored.Value.Namespace, stored.Value.Name, default);
        Assert.AreEqual(stored.Value.Definition, current!.Value.Definition);
        Assert.AreNotEqual(StorageAvailability.Ready, current.Value.ObservedState?.Availability);
    }

    [TestMethod]
    public async Task TypeDriftRequiresExplicitReconciliationAndOldRevisionFails()
    {
        var f = new StorageFixture();
        var type = await f.TypeAsync();
        var stored = await f.Storages.CreateAsync(StorageFixture.Storage(type), default);
        var address = new ScopedResourceAddress(StorageFixture.Tenant, type.Namespace, type.Kind, type.Name);
        await f.Store.DeleteExactAsync(address, type.ETag, default);
        var recreated = await f.Types.CreateAsync(type, default);
        var observed = await f.Storages.ReconcileAsync(StorageFixture.Tenant, stored.Value.Namespace, stored.Value.Name, null, stored.ETag, default);
        Assert.AreEqual(StorageAvailability.Stale, observed.Value.ObservedState?.Availability);
        Assert.AreEqual(type.Uid, observed.Value.Definition.Type.TypeUid);
        var definition = stored.Value.Definition with { Type = StorageFixture.Reference(recreated.Value) };
        await Failure("storage_reconciliation_required", () => f.Storages.PutExactAsync(StorageFixture.Tenant,
            stored.Value.Namespace, stored.Value.Name, definition, observed.ETag, default));
        var reconciled = await f.Storages.ReconcileAsync(StorageFixture.Tenant, stored.Value.Namespace, stored.Value.Name,
            definition, observed.ETag, default);
        Assert.AreEqual(StorageAvailability.Ready, reconciled.Value.ObservedState?.Availability);
        await Failure("storage_revision_stale", () => f.Storages.ResolveAsync(StorageFixture.Revision(stored.Value), new(StorageFixture.Tenant, stored.Value.Address)));
    }

    [TestMethod]
    public async Task TypeVersionsAreImmutableAndProviderCanOnlyAppendVersions()
    {
        var f = new StorageFixture();
        var type = await f.TypeAsync();
        var version = type.Definition.Versions.Single();
        await Failure("storage_type_version_immutable", () => f.Types.PutExactAsync(StorageFixture.Tenant, type.Namespace,
            type.Name, type.Definition with { Versions = [version with { RequiresConnectedService = true }] }, type.ETag, default));
        var updated = await f.Types.PutExactAsync(StorageFixture.Tenant, type.Namespace, type.Name,
            type.Definition with { Versions = [version with { Version = "2.0.0" }], PreferredVersion = "2.0.0" }, type.ETag, default);
        Assert.HasCount(2, updated.Value.Definition.Versions);
    }

    [TestMethod]
    public async Task ExtensionTypeRequiresExactContributionAndManagementAuthority()
    {
        var f = new StorageFixture();
        var provider = new ScopedResourceAddress(StorageFixture.Tenant, ResourceNamespace.Default, "ExtensionRegistration", "storage-provider");
        var version = StorageFixture.Version() with
        {
            Contribution = new(provider, Guid.NewGuid(), 1, "object-storage", "1.0.0", "sha256:contribution")
        };
        var desired = new StorageTypeResource
        {
            ApiVersion = ResourceApiVersions.CoreV1, Kind = StorageResourceKinds.StorageType,
            Metadata = new() { Name = "object-storage", Namespace = ResourceNamespace.Parse("extension.storage") },
            ScopeRef = StorageFixture.Tenant,
            Definition = new() { DisplayName = "Extension type", PreferredVersion = version.Version, Versions = [version] }
        };
        await Failure("storage_contribution_invalid", () => f.Types.CreateAsync(desired, default));
        using var authority = f.Governance.Push(ResourceGovernance.Extension(
            $"{provider.ScopeRef.Value}/{provider.Kind}/{provider.Namespace}/{provider.Name}", "extension.storage", "1.0.0"));
        var created = await f.Types.SynchronizeManagedAsync(desired, default);
        Assert.AreEqual(version.Contribution, created.Value.Definition.Versions.Single().Contribution);
        var storage = await f.Storages.CreateAsync(StorageFixture.Storage(created.Value), default);
        Assert.IsNull(storage.Value.Definition.ConnectedService);
    }

    [TestMethod]
    public async Task UnavailableProviderPreservesResourceAndBindingEvidence()
    {
        var f = new StorageFixture();
        var stored = await f.Storages.CreateAsync(await f.RemoteAsync(), default);
        f.Availability.Available = false;
        var observed = await f.Storages.GetExactAsync(StorageFixture.Tenant, stored.Value.Namespace, stored.Value.Name, default);
        Assert.AreEqual(StorageAvailability.Unavailable, observed!.Value.ObservedState?.Availability);
        Assert.AreEqual(stored.Value.Uid, observed.Value.Uid);
        Assert.AreEqual(stored.Value.Definition, observed.Value.Definition);
        await Failure("storage_provider_unavailable", () => f.Storages.ResolveAsync(StorageFixture.Revision(stored.Value), new(StorageFixture.Tenant, stored.Value.Address)));
    }

    [TestMethod]
    public async Task ETagRequiredAndConcurrentWritersCannotOverwriteEachOther()
    {
        var f = new StorageFixture();
        var stored = await f.Storages.CreateAsync(StorageFixture.Storage(await f.TypeAsync()), default);
        await Failure("storage_etag_required", () => f.Storages.DeactivateAsync(StorageFixture.Tenant, stored.Value.Namespace, stored.Value.Name, null, default));
        await f.Storages.PutExactAsync(StorageFixture.Tenant, stored.Value.Namespace, stored.Value.Name,
            stored.Value.Definition with { DisplayName = "Changed" }, stored.ETag, default);
        await Failure("storage_version_conflict", () => f.Storages.PutExactAsync(StorageFixture.Tenant,
            stored.Value.Namespace, stored.Value.Name, stored.Value.Definition, stored.ETag, default));
    }

    [TestMethod]
    public async Task DependenciesBlockDeactivationAndEnvelopeRebinding()
    {
        var f = new StorageFixture();
        var stored = await f.Storages.CreateAsync(StorageFixture.Storage(await f.TypeAsync()), default);
        f.Dependencies.InUse = true;
        await Failure("storage_in_use", () => f.Storages.DeactivateAsync(StorageFixture.Tenant, stored.Value.Namespace, stored.Value.Name, stored.ETag, default));
        await Failure("storage_in_use", () => f.Storages.ReconcileAsync(StorageFixture.Tenant, stored.Value.Namespace,
            stored.Value.Name, stored.Value.Definition with { Envelope = JsonSerializer.SerializeToElement(new { prefix = "new" }) }, stored.ETag, default));
        f.Dependencies.InUse = false;
        var disabled = await f.Storages.DeactivateAsync(StorageFixture.Tenant, stored.Value.Namespace, stored.Value.Name, stored.ETag, default);
        await Failure("storage_disabled", () => f.Storages.ResolveAsync(StorageFixture.Revision(disabled.Value), new(StorageFixture.Tenant, stored.Value.Address)));
    }

    [TestMethod]
    [DataRow("{\"prefix\":42}")]
    [DataRow("{\"backendUri\":\"file:///escape\"}")]
    [DataRow("{\"prefix\":\"012345678901234567890\"}")]
    public async Task ConfigurationAndEnvelopeMustSatisfyExactSchemas(string invalid)
    {
        var f = new StorageFixture();
        var resource = StorageFixture.Storage(await f.TypeAsync());
        using var value = JsonDocument.Parse(invalid);
        await Failure("storage_configuration_invalid", () => f.Storages.CreateAsync(resource with
        { Definition = resource.Definition with { Configuration = value.RootElement } }, default));
        await Failure("storage_configuration_invalid", () => f.Storages.CreateAsync(resource with
        { Definition = resource.Definition with { Envelope = value.RootElement } }, default));
    }

    [TestMethod]
    public async Task UnsupportedSchemaKeywordsFailRatherThanBeingIgnored()
    {
        var f = new StorageFixture();
        var schema = JsonSerializer.SerializeToElement(new { type = "object", allOf = new[] { new { type = "object" } } });
        var version = StorageFixture.Version() with { ConfigurationSchema = schema, SchemaDigest = StorageDigests.Schema(schema) };
        await Failure("storage_schema_invalid", () => f.Types.CreateAsync(new()
        {
            ApiVersion = ResourceApiVersions.CoreV1, Kind = StorageResourceKinds.StorageType,
            Metadata = new() { Name = "invalid" }, ScopeRef = StorageFixture.Tenant,
            Definition = new() { DisplayName = "Invalid", PreferredVersion = version.Version, Versions = [version] }
        }, default));
    }

    [TestMethod]
    public async Task ProtectedResolutionNeverSerializesConfigurationEnvelopeOrExports()
    {
        var f = new StorageFixture();
        var resource = StorageFixture.Storage(await f.TypeAsync());
        var stored = await f.Storages.CreateAsync(resource with { Definition = resource.Definition with
        { Configuration = JsonSerializer.SerializeToElement(new { prefix = "private" }) } }, default);
        var resolved = await f.Storages.ResolveAsync(StorageFixture.Revision(stored.Value), new(StorageFixture.Tenant, stored.Value.Address));
        var json = JsonSerializer.Serialize(resolved);
        Assert.DoesNotContain("private", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Configuration", json, StringComparison.Ordinal);
        Assert.AreEqual("[REDACTED]", resolved.ToString());
    }

    private static async Task Failure<T>(string code, Func<Task<T>> action)
    {
        var exception = await Assert.ThrowsAsync<StorageException>(async () => await action());
        Assert.AreEqual(code, exception.Code);
    }
}
