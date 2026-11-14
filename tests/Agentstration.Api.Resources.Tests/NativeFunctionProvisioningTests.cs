using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.Api.Contracts;
using Agentstration.Functions;
using Agentstration.Functions.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.Infrastructure;
using Agentstration.Infrastructure.Functions;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class NativeFunctionProvisioningTests : ResourceManagementApiTestBase
{
    [TestMethod]
    public async Task StartupReconcilesDescriptionsWithoutChangingFunctionIdentityOrVersionContracts()
    {
        await using var factory = Factory();
        _ = await GetBootstrapContextAsync(factory);
        using var system = factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushSystem();
        using var authority = factory.Services.GetRequiredService<IResourceGovernanceContext>().Push(ResourceGovernance.Core(NativeFunctionPlatformResourceProvisioner.Component));
        var functions = factory.Services.GetRequiredService<TransformationFunctionService>();
        var original = (await functions.GetAsync(ResourceScopeRef.Instance, ResourceNamespace.Default, "json-select-builtin", default))!;
        var versionDigest = FunctionDigests.Compute(original.Value.Definition.Versions);
        _ = await factory.Services.GetRequiredService<IResourceStore>().PutExactAsync(ResourceScopeRef.Instance,
            original.Value with { Definition = original.Value.Definition with { Description = null } }, original.ETag, false, default);
        var provisioner = factory.Services.GetRequiredService<NativeFunctionPlatformResourceProvisioner>();
        await provisioner.EnsureAsync(default);
        var reconciled = (await functions.GetAsync(ResourceScopeRef.Instance, ResourceNamespace.Default, original.Value.Name, default))!;
        Assert.IsFalse(string.IsNullOrWhiteSpace(reconciled.Value.Definition.Description));
        Assert.AreEqual(original.Value.Uid, reconciled.Value.Uid);
        Assert.AreEqual(versionDigest, FunctionDigests.Compute(reconciled.Value.Definition.Versions));
        await provisioner.EnsureAsync(default);
        Assert.AreEqual(reconciled.ETag, (await functions.GetAsync(ResourceScopeRef.Instance, ResourceNamespace.Default, original.Value.Name, default))!.ETag);
    }

    [TestMethod]
    public async Task StartupWithoutFileBootstrapProvisionsOneGovernedInstanceCatalogueAndGrantsInheritedUse()
    {
        await using var factory = Factory().WithWebHostBuilder(builder => builder
            .UseSetting("Agentstration:Bootstrap:InitialBootstrapEnabled", "false")
            .UseSetting("Agentstration:Bootstrap:Path", Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))));
        using var client = factory.CreateClient();
        var context = await GetBootstrapContextAsync(factory);
        var scopes = factory.Services.GetRequiredService<IRequestContextScopeFactory>();
        var functions = factory.Services.GetRequiredService<TransformationFunctionService>();
        var resources = factory.Services.GetRequiredService<IResourceStore>();
        IReadOnlyList<StoredResource<TransformationFunctionResource>> builtins;
        using (scopes.PushSystem())
        {
            builtins = await functions.ListAsync(ResourceScopeRef.Instance, 0, 100, default);
            Assert.HasCount(5, builtins);
            Assert.IsTrue(builtins.All(value => !string.IsNullOrWhiteSpace(value.Value.Definition.Description)));
            CollectionAssert.AreEquivalent(new[] { "json-select-builtin", "json-project-builtin", "collection-filter-builtin", "collection-distinct-builtin", "artifact-explode-builtin" }, builtins.Select(value => value.Value.Name).ToArray());
            var kind = (await functions.GetKindAsync(ResourceScopeRef.Instance, ResourceNamespace.Default, "native", default))!;
            foreach (var resource in builtins.Select(value => (Resource)value.Value).Append(kind.Value))
            {
                Assert.AreEqual(ResourceScopeRef.Instance, resource.ScopeRef);
                Assert.AreEqual(ResourceOriginKind.BuiltIn, resource.Governance.Origin.Kind);
                Assert.AreEqual(NativeFunctionPlatformResourceProvisioner.Component, resource.Governance.ManagedBy!.Reference);
                Assert.AreEqual("true", resource.Metadata.Annotations[ResourceProvenanceAnnotations.BuiltIn]);
            }
            await factory.Services.GetRequiredService<NativeFunctionPlatformResourceProvisioner>().EnsureAsync(default);
            var replay = await functions.ListAsync(ResourceScopeRef.Instance, 0, 100, default);
            foreach (var previous in builtins)
            {
                var current = replay.Single(value => value.Value.Name == previous.Value.Name);
                Assert.AreEqual(previous.Value.Uid, current.Value.Uid);
                Assert.AreEqual(previous.ETag, current.ETag);
            }
        }
        using (scopes.Push(context))
        {
            var workspace = ResourceScopeRef.Workspace(context.WorkspaceId);
            Assert.HasCount(5, await functions.ListAsync(workspace, 0, 100, default));
            Assert.IsEmpty(await resources.ListExactAsync<TransformationFunctionResource>(workspace, FunctionResourceKinds.TransformationFunction, 0, 100, default));
            Assert.IsEmpty(await resources.ListExactAsync<FunctionKindResource>(workspace, FunctionResourceKinds.FunctionKind, 0, 100, default));
            var function = await functions.ResolveAsync(new("json-select-builtin", "1.0.0", ResourceScopeRef.Instance), workspace, default);
            var result = await functions.InvokeAsync(function, workspace,
                JsonSerializer.Deserialize<JsonElement>("""{"value":{"id":42},"path":"/id"}"""), default);
            Assert.AreEqual(42, result.GetInt32());
        }
        using (scopes.PushTenant(context.PrincipalId, context.TenantId))
            Assert.AreEqual(ResourceScopeRef.Instance, (await functions.ResolveAsync(new("json-select-builtin", "1.0.0", ResourceScopeRef.Instance), ResourceScopeRef.Tenant(context.TenantId), default)).Reference.ScopeRef);

        SelectConsoleContext(client, ResourceScopeRef.Instance);
        var listed = await client.GetFromJsonAsync<ValueResponse<TransformationFunctionResource>>("/api/transformation-functions?scopeRef=%2Finstance");
        Assert.HasCount(5, listed!.Value);
        var original = builtins.Single(value => value.Value.Name == "json-select-builtin");
        using var update = new HttpRequestMessage(HttpMethod.Put, "/api/transformation-functions/json-select-builtin?scopeRef=%2Finstance")
        { Content = JsonContent.Create(original.Value with { Definition = original.Value.Definition with { Enabled = false } }) };
        update.Headers.TryAddWithoutValidation("If-Match", original.ETag);
        using var denied = await client.SendAsync(update);
        Assert.AreEqual(HttpStatusCode.PreconditionFailed, denied.StatusCode);
        Assert.Contains("resource_managed", await denied.Content.ReadAsStringAsync());
    }

    [TestMethod]
    public async Task ProvisioningRefusesToAdoptAUserResourceAtAReservedBuiltinIdentity()
    {
        await using var factory = Factory();
        _ = factory.Services;
        using var system = factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushSystem();
        var store = factory.Services.GetRequiredService<IResourceStore>();
        var service = factory.Services.GetRequiredService<TransformationFunctionService>();
        var address = new ScopedResourceAddress(ResourceScopeRef.Instance, ResourceNamespace.Default, FunctionResourceKinds.TransformationFunction, "json-select-builtin");
        var original = (await store.GetExactAsync<TransformationFunctionResource>(address, default))!;
        using (factory.Services.GetRequiredService<IResourceGovernanceContext>().Push(ResourceGovernance.Core(NativeFunctionPlatformResourceProvisioner.Component)))
            await store.DeleteExactAsync(address, original.ETag, default);
        var user = await service.PutAsync(original.Value with { Uid = Guid.NewGuid(), Governance = ResourceGovernance.User }, null, true, default);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => factory.Services.GetRequiredService<NativeFunctionPlatformResourceProvisioner>().EnsureAsync(default));
        var unchanged = (await store.GetExactAsync<TransformationFunctionResource>(address, default))!;
        Assert.AreEqual(user.Value.Uid, unchanged.Value.Uid);
        Assert.AreEqual(user.ETag, unchanged.ETag);
        Assert.AreEqual(ResourceOriginKind.User, unchanged.Value.Governance.Origin.Kind);
    }
}
