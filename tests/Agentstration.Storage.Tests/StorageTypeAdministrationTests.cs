using Agentstration.Resources;
using Agentstration.Storage.Contracts;

namespace Agentstration.Storage.Tests;

[TestClass]
public sealed class StorageTypeAdministrationTests
{
    [TestMethod]
    public async Task PublicationIsExplicitAdditiveAndDoesNotRebindExistingStorage()
    {
        var fixture = new StorageFixture();
        var type = await fixture.TypeAsync();
        var storage = await fixture.Storages.CreateAsync(StorageFixture.Storage(type), default);
        var originalDigest = type.Definition.Versions.Single().ContractDigest;
        var published = await fixture.Types.PublishVersionAsync(StorageFixture.Tenant, type.Namespace, type.Name,
            new(StorageFixture.Version() with { Version = "2.0.0" }, SetPreferred: true), type.ETag, default);
        Assert.HasCount(2, published.Value.Definition.Versions);
        Assert.AreEqual(originalDigest, published.Value.Definition.Versions.Single(value => value.Version == "1.0.0").ContractDigest);
        Assert.AreEqual("2.0.0", published.Value.Definition.PreferredVersion);
        var currentStorage = await fixture.Storages.GetExactAsync(StorageFixture.Tenant, storage.Value.Namespace, storage.Value.Name, default);
        Assert.AreEqual(storage.ETag, currentStorage!.ETag);
        Assert.AreEqual("1.0.0", currentStorage.Value.Definition.Type.Version);
        var duplicate = await Assert.ThrowsAsync<StorageException>(() => fixture.Types.PublishVersionAsync(
            StorageFixture.Tenant, type.Namespace, type.Name, new(StorageFixture.Version()), published.ETag, default));
        Assert.AreEqual("storage_type_version_exists", duplicate.Code);
        var stale = await Assert.ThrowsAsync<StorageException>(() => fixture.Types.PublishVersionAsync(
            StorageFixture.Tenant, type.Namespace, type.Name, new(StorageFixture.Version() with { Version = "3.0.0" }), type.ETag, default));
        Assert.AreEqual("storage_version_conflict", stale.Code);
    }

    [TestMethod]
    public async Task PublicationCanLeavePreferredVersionUnchanged()
    {
        var fixture = new StorageFixture();
        var type = await fixture.TypeAsync();
        var published = await fixture.Types.PublishVersionAsync(StorageFixture.Tenant, type.Namespace, type.Name,
            new(StorageFixture.Version() with { Version = "2.0.0" }), type.ETag, default);
        Assert.AreEqual("1.0.0", published.Value.Definition.PreferredVersion);
    }

    [TestMethod]
    public async Task ReferencedTypeCannotBeDisabledOrDeletedAndReferencesKeepExactAddresses()
    {
        var fixture = new StorageFixture();
        var type = await fixture.TypeAsync();
        var storage = await fixture.Storages.CreateAsync(StorageFixture.Storage(type, "documents"), default);
        var usages = await fixture.Types.GetUsagesAsync(StorageFixture.Tenant, type.Namespace, type.Name, default);
        Assert.AreEqual(storage.Value.Name, usages.Single().Name);
        StringAssert.Contains(usages.Single().Url, Uri.EscapeDataString(StorageFixture.Tenant.Value));
        StringAssert.Contains(usages.Single().Url, "namespace=default");
        var disabled = await Assert.ThrowsAsync<StorageException>(() => fixture.Types.PutExactAsync(StorageFixture.Tenant,
            type.Namespace, type.Name, type.Definition with { Enabled = false }, type.ETag, default));
        Assert.AreEqual("storage_type_in_use", disabled.Code);
        var deleted = await Assert.ThrowsAsync<StorageException>(() => fixture.Types.DeleteExactAsync(StorageFixture.Tenant,
            type.Namespace, type.Name, type.ETag, default));
        Assert.AreEqual("storage_type_in_use", deleted.Code);
        Assert.IsTrue((await fixture.Types.GetExactAsync(StorageFixture.Tenant, type.Namespace, type.Name, default))!.Value.Definition.Enabled);
    }

    [TestMethod]
    public async Task UnusedTypeCanBeDisabledAndDeletedUsingCurrentEtag()
    {
        var fixture = new StorageFixture();
        var type = await fixture.TypeAsync();
        var disabled = await fixture.Types.PutExactAsync(StorageFixture.Tenant, type.Namespace, type.Name,
            type.Definition with { Enabled = false }, type.ETag, default);
        Assert.AreEqual(StorageAvailability.Disabled, (await fixture.Types.GetExactAsync(StorageFixture.Tenant, type.Namespace, type.Name, default))!.Value.ObservedVersions.Single().Availability);
        await fixture.Types.DeleteExactAsync(StorageFixture.Tenant, type.Namespace, type.Name, disabled.ETag, default);
        Assert.IsNull(await fixture.Types.GetExactAsync(StorageFixture.Tenant, type.Namespace, type.Name, default));
    }

    [TestMethod]
    public async Task HiddenReferencesAreNotProjectedButStillBlockLifecycle()
    {
        var fixture = new StorageFixture();
        var type = await fixture.TypeAsync();
        await fixture.Store.PutExactAsync(StorageFixture.Workspace,
            StorageFixture.Storage(type, "hidden-reference", StorageFixture.Workspace), null, true, default);
        fixture.Store.ReadDeniedScopes.Add(StorageFixture.Workspace);
        Assert.IsEmpty(await fixture.Types.GetUsagesAsync(StorageFixture.Tenant, type.Namespace, type.Name, default));
        var failure = await Assert.ThrowsAsync<StorageException>(() => fixture.Types.DeleteExactAsync(StorageFixture.Tenant,
            type.Namespace, type.Name, type.ETag, default));
        Assert.AreEqual("storage_type_in_use", failure.Code);
        Assert.DoesNotContain("hidden-reference", failure.Message, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task AvailabilityIsObservedWithoutChangingSavedVersionOrEtag()
    {
        var fixture = new StorageFixture();
        var type = await fixture.TypeAsync();
        fixture.Availability.Available = false;
        var missing = await fixture.Types.GetExactAsync(StorageFixture.Tenant, type.Namespace, type.Name, default);
        Assert.AreEqual(StorageAvailability.Unavailable, missing!.Value.ObservedVersions.Single().Availability);
        Assert.AreEqual(type.ETag, missing.ETag);
        Assert.AreEqual(type.Definition.Versions.Single().ContractDigest, missing.Value.Definition.Versions.Single().ContractDigest);
        fixture.Availability.Available = true;
        Assert.AreEqual(StorageAvailability.Ready, (await fixture.Types.ListAsync(StorageFixture.Tenant, 0, 50, default)).Single().Value.ObservedVersions.Single().Availability);
    }
}
