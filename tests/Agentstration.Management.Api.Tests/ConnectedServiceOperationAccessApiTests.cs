using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.ConnectedServices;
using Agentstration.ConnectedServices.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.Infrastructure;
using Agentstration.ResourceManagement;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Management.Tests;

public sealed partial class ConnectedServiceApiTests
{
    [TestMethod]
    [DataRow("instance")]
    [DataRow("tenant")]
    public async Task OperationsInheritLiveServiceAccessOnlyWhenExplicitlyExposed(string ownerKind)
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        var context = await GetBootstrapContextAsync(factory);
        var consumer = ResourceScopeRef.Workspace(context.WorkspaceId);
        var owner = ownerKind == "instance" ? ResourceScopeRef.Instance : ResourceScopeRef.Tenant(context.TenantId);
        var ns = new ResourceNamespace("operation-sharing");
        var scopes = factory.Services.GetRequiredService<IRequestContextScopeFactory>();
        var store = factory.Services.GetRequiredService<IResourceStore>();
        var policies = factory.Services.GetRequiredService<IResourceAccessPolicyService>();
        var management = factory.Services.GetRequiredService<ConnectedServiceManagementService>();
        var operations = factory.Services.GetRequiredService<ConnectedServiceOperationService>();
        StoredResource<ConnectedServiceResource> service;
        ConnectedServiceOperationResource operation;
        ResourceAccessPolicyResponse grant;
        using (scopes.PushSystem())
        {
            var type = (await store.GetExactAsync<ConnectedServiceTypeResource>(new(ResourceScopeRef.Instance,
                ResourceNamespace.Default, ConnectedServiceResourceKinds.ConnectedServiceType, "http-endpoint"), default))!;
            var operationType = (await store.GetExactAsync<ConnectedServiceOperationTypeResource>(new(ResourceScopeRef.Instance,
                ResourceNamespace.Default, ConnectedServiceResourceKinds.ConnectedServiceOperationType, "http-get"), default))!;
            using (factory.Services.GetRequiredService<IResourceGovernanceContext>().Push(ResourceGovernance.Core("connected-services.http", "1.0.0")))
            {
                type = await store.PutExactAsync(ResourceScopeRef.Instance, type.Value with
                {
                    Definition = type.Value.Definition with { UsePolicy = new() { Grants = new[] { new DescendantUseGrant(owner), new DescendantUseGrant(consumer) }.Distinct().Where(g => g.ScopeRef != ResourceScopeRef.Instance).ToArray() } }
                }, type.ETag, false, default);
                operationType = await store.PutExactAsync(ResourceScopeRef.Instance, operationType.Value with
                {
                    Definition = operationType.Value.Definition with { UsePolicy = new() { Grants = new[] { new DescendantUseGrant(owner), new DescendantUseGrant(consumer) }.Distinct().Where(g => g.ScopeRef != ResourceScopeRef.Instance).ToArray() } }
                }, operationType.ETag, false, default);
            }
            foreach (var resource in new Resource[] { type.Value, operationType.Value })
                _ = await policies.PutAsync(new(new(resource.Uid, ResourceScopeRef.Instance, resource.Namespace, resource.Kind),
                    new[] { owner, consumer }.Distinct().Where(s => s != ResourceScopeRef.Instance)
                        .Select(s => new ResourceAccessGrant(s, [ResourceAccessAction.Discover, ResourceAccessAction.Use])).ToArray()), null, default);
            var version = type.Value.Definition.Versions.Single(v => v.Version == type.Value.Definition.PreferredVersion);
            service = await management.CreateAsync(new()
            {
                ApiVersion = ResourceApiVersions.CoreV1, Kind = ConnectedServiceResourceKinds.ConnectedService,
                Metadata = new() { Name = "shared-http", Namespace = ns }, ScopeRef = owner,
                Definition = new()
                {
                    DisplayName = "Shared HTTP",
                    Type = new() { Type = new(type.Value.Name, type.Value.ScopeRef, type.Value.Namespace), TypeUid = type.Value.Uid, Version = version.Version, SchemaDigest = version.SchemaDigest },
                    Options = JsonSerializer.SerializeToElement(new { }),
                    Bindings = [new() { InputId = "baseUrl", Kind = ConnectedServiceBindingKind.Literal, Literal = JsonSerializer.SerializeToElement("https://example.test/news") }],
                    UsePolicy = new() { Grants = [new(consumer)] }
                }
            }, default);
            Assert.IsFalse(service.Value.Definition.ExposeOperationsToDescendants);
            var opVersion = operationType.Value.Definition.Versions.Single(v => v.Version == operationType.Value.Definition.PreferredVersion);
            operation = (await store.PutExactAsync(owner, new ConnectedServiceOperationResource
            {
                ApiVersion = ResourceApiVersions.CoreV1, Kind = ConnectedServiceResourceKinds.ConnectedServiceOperation,
                Metadata = new() { Name = "shared-get", Namespace = ns }, ScopeRef = owner, Generation = 1, Enabled = true,
                Definition = new()
                {
                    DisplayName = "Shared GET", ExternalId = "GET /news", Definition = JsonSerializer.SerializeToElement(new { }), DefinitionDigest = "sha256:operation",
                    Service = new() { Service = new(service.Value.Address, owner), ServiceUid = service.Value.Uid, Generation = service.Value.Generation },
                    Type = new() { Type = new(operationType.Value.Name, operationType.Value.ScopeRef, operationType.Value.Namespace), TypeUid = operationType.Value.Uid, Version = opVersion.Version, SchemaDigest = opVersion.SchemaDigest },
                    Provenance = new() { Discovery = new(owner, ns, ConnectedServiceResourceKinds.ConnectedServiceOperationDiscovery, "fixture"), DiscoveryUid = Guid.NewGuid(), ManifestDigest = "sha256:manifest", DiscoveredAt = DateTimeOffset.UnixEpoch }
                },
                ObservedState = new() { Availability = ConnectedServiceOperationAvailability.Available, ObservedAt = DateTimeOffset.UnixEpoch }
            }, null, true, default)).Value;
            grant = await policies.PutAsync(new(new(service.Value.Uid, owner, ns, service.Value.Kind),
                [new(ownerKind == "instance" ? ResourceScopeRef.Tenant(context.TenantId) : consumer,
                    [ResourceAccessAction.Discover, ResourceAccessAction.Use], IncludeDescendants: ownerKind == "instance")]), null, default);
        }
        SelectConsoleContext(client, consumer);
        var address = new ScopedResourceAddress(owner, ns, operation.Kind, operation.Name);
        var reference = new ConnectedServiceOperationRevisionReference(address, operation.Uid, operation.Generation, operation.Definition.DefinitionDigest);
        var detailUrl = $"/api/connected-service-operations/{operation.Name}?scopeRef={Uri.EscapeDataString(owner.Value)}&namespace={ns.Value}";
        var serviceListUrl = $"/api/connected-services/{service.Value.Name}/operations?scopeRef={Uri.EscapeDataString(owner.Value)}&namespace={ns.Value}";
        using (var hidden = await client.GetAsync(detailUrl)) Assert.AreEqual(HttpStatusCode.NotFound, hidden.StatusCode);
        Assert.IsEmpty((await client.GetFromJsonAsync<ConnectedServiceOperationResource[]>(serviceListUrl))!);

        using (scopes.PushSystem())
        {
            var before = service;
            service = await management.PutExactAsync(owner, ns, service.Value.Name,
                service.Value.Definition with { ExposeOperationsToDescendants = true }, service.ETag, default);
            Assert.AreEqual(before.Value.Generation, service.Value.Generation, "An access-only choice must preserve the pinned connection revision.");
            Assert.AreNotEqual(before.ETag, service.ETag);
            await Assert.ThrowsExactlyAsync<ResourceConcurrencyException>(() => management.PutExactAsync(owner, ns, service.Value.Name,
                service.Value.Definition with { ExposeOperationsToDescendants = false }, before.ETag, default));
        }
        Assert.AreEqual(operation.Uid, (await client.GetFromJsonAsync<ConnectedServiceOperationResource>(detailUrl))!.Uid);
        Assert.HasCount(1, (await client.GetFromJsonAsync<ConnectedServiceOperationResource[]>(serviceListUrl))!);
        Assert.IsTrue((await client.GetFromJsonAsync<ConnectedServiceOperationResource[]>("/api/connected-service-operations"))!.Any(o => o.Uid == operation.Uid));
        using (scopes.Push(context))
        {
            Assert.IsTrue((await policies.EvaluateAsync(new(operation.Uid, owner, ns, operation.Kind, operation.Generation), consumer, ResourceAccessAction.Use, default)).Allowed);
            _ = await operations.ResolveAsync(reference, consumer, true, default);
        }
        using (scopes.PushSystem())
        {
            var later = await store.PutExactAsync(owner, operation with
                { Uid = Guid.Empty, Metadata = operation.Metadata with { Name = "later-discovery" } }, null, true, default);
            Assert.IsNotNull(ConnectedServiceOperationAccessPolicy.GetSource(later.Value, service.Value));
            Assert.IsNull(ConnectedServiceOperationAccessPolicy.GetSource(operation with { ScopeRef = consumer }, service.Value));
            Assert.IsNull(ConnectedServiceOperationAccessPolicy.GetSource(operation with
                { Metadata = operation.Metadata with { Namespace = new ResourceNamespace("other") } }, service.Value));
            Assert.IsNull(ConnectedServiceOperationAccessPolicy.GetSource(operation with
                { Definition = operation.Definition with { Service = operation.Definition.Service with { ServiceUid = Guid.NewGuid() } } }, service.Value));
            Assert.IsNull(ConnectedServiceOperationAccessPolicy.GetSource(operation with
                { Definition = operation.Definition with { Service = operation.Definition.Service with
                    { Service = new(ResourceAddress.Create(ns, service.Value.Kind, "other-service"), owner) } } }, service.Value));
            var siblingId = Guid.NewGuid();
            var foreignTenantId = Guid.NewGuid();
            var identityStore = factory.Services.GetRequiredService<IIdentityStore>();
            await identityStore.AddTenantAsync(new(foreignTenantId, "operation-foreign", "Foreign", TenantStatus.Active, DateTimeOffset.UnixEpoch), default);
            await identityStore.AddWorkspaceAsync(new(siblingId, foreignTenantId, "operation-sibling", "Sibling", WorkspaceStatus.Active, DateTimeOffset.UnixEpoch), default);
            Assert.IsFalse((await policies.EvaluateAsync(new(operation.Uid, owner, ns, operation.Kind), ResourceScopeRef.Workspace(siblingId), ResourceAccessAction.Use, default)).Allowed);
            Assert.IsFalse((await policies.EvaluateAsync(new(operation.Uid, owner, new ResourceNamespace("other"), operation.Kind), consumer, ResourceAccessAction.Use, default)).Allowed);
            var current = (await store.GetExactAsync<ConnectedServiceOperationResource>(address, default))!;
            var disabled = await operations.SetEnabledAsync(address, false, current.ETag, default);
            var disabledReference = reference with { Generation = disabled.Value.Generation };
            var unavailable = await Assert.ThrowsExactlyAsync<ConnectedServiceOperationDiscoveryException>(() => operations.ResolveAsync(disabledReference, consumer, true, default));
            Assert.AreEqual("operation_unavailable", unavailable.Code);
            var enabled = await operations.SetEnabledAsync(address, true, disabled.ETag, default);
            reference = reference with { Generation = enabled.Value.Generation };
            var stale = await store.PutExactAsync(owner, enabled.Value with
                { ObservedState = enabled.Value.ObservedState with { Availability = ConnectedServiceOperationAvailability.Unavailable } }, enabled.ETag, false, default);
            unavailable = await Assert.ThrowsExactlyAsync<ConnectedServiceOperationDiscoveryException>(() => operations.ResolveAsync(reference, consumer, true, default));
            Assert.AreEqual("operation_unavailable", unavailable.Code);
            _ = await store.PutExactAsync(owner, stale.Value with { ObservedState = operation.ObservedState }, stale.ETag, false, default);
        }
        using (scopes.PushSystem())
            grant = await policies.PutAsync(grant.Policy with { Grants = [new(consumer, [ResourceAccessAction.Discover])] }, grant.ETag, default);
        using (scopes.Push(context))
        {
            var denied = await Assert.ThrowsExactlyAsync<ConnectedServiceOperationDiscoveryException>(() => operations.ResolveAsync(reference, consumer, true, default));
            Assert.AreEqual("operation_access_denied", denied.Code);
        }
        Assert.HasCount(2, (await client.GetFromJsonAsync<ConnectedServiceOperationResource[]>(serviceListUrl))!);
        using (scopes.PushSystem())
            await policies.DeleteAsync(service.Value.Uid, grant.ETag, default);
        using (var revoked = await client.GetAsync(detailUrl)) Assert.AreEqual(HttpStatusCode.NotFound, revoked.StatusCode);
        using (var revoked = await client.GetAsync(serviceListUrl)) Assert.AreEqual(HttpStatusCode.NotFound, revoked.StatusCode);
        Assert.IsFalse((await client.GetFromJsonAsync<ConnectedServiceOperationResource[]>("/api/connected-service-operations"))!.Any(o => o.Uid == operation.Uid));
        using (scopes.PushSystem())
        {
            _ = await policies.PutAsync(grant.Policy, null, default);
            service = await management.PutExactAsync(owner, ns, service.Value.Name,
                service.Value.Definition with { ExposeOperationsToDescendants = false }, service.ETag, default);
        }
        using (var disabled = await client.GetAsync(detailUrl)) Assert.AreEqual(HttpStatusCode.NotFound, disabled.StatusCode);
    }
}
