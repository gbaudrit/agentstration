using Agentstration.Aep.Abstractions;
using Agentstration.Extensions.Aep;
using Agentstration.Extensions.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.Infrastructure.Tools;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Tools;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agentstration.Management.Tests;

public sealed partial class AepEnrollmentTests
{
    [TestMethod]
    [DataRow(ResourceScopeKind.Instance)]
    [DataRow(ResourceScopeKind.Tenant)]
    [DataRow(ResourceScopeKind.Workspace)]
    public async Task EnrollmentProjectsToolSetDraftsAtTheRegistrationScopeWithoutEnablingTools(ResourceScopeKind kind)
    {
        var handler = new EnrollmentLifecycleHandler("extension.toolsets", advertiseTool: true, advertiseToolSet: true);
        await using var factory = EnrollmentFactory().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IHttpClientFactory>(); services.AddSingleton<IHttpClientFactory>(new EnrollmentHttpClientFactory(handler));
            services.RemoveAll<IToolProviderDiscovery>(); services.AddSingleton<IToolProviderDiscovery, StaticToolDiscovery>();
        }));
        var context = await GetBootstrapContextAsync(factory);
        var target = kind switch { ResourceScopeKind.Instance => ResourceScopeRef.Instance, ResourceScopeKind.Tenant => ResourceScopeRef.Tenant(context.TenantId), _ => ResourceScopeRef.Workspace(context.WorkspaceId) };
        var enrollment = factory.Services.GetRequiredService<AepEnrollmentService>(); var instance = Guid.NewGuid();
        _ = await enrollment.AnnounceAsync(new(instance, handler.Manifest().Extension, new("https://toolsets.example/"), new("https://toolsets.example/aep/enrollment/pair")), default);
        using var owner = PushOwnerScope(factory, context, target);
        await enrollment.AssignAsync(context, instance, target, default);
        var code = await enrollment.RotateAsync(context, instance, default);
        var credential = await enrollment.ClaimAsync(new(instance, instance, code.Code), default);
        _ = await enrollment.ReadyAsync(new(instance, instance, credential.CompletionToken), default);
        using var system = factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushSystem();
        var store = factory.Services.GetRequiredService<IResourceStore>();
        var name = $"paired-{instance:N}";
        var group = (await store.GetExactAsync<ToolSetResource>(new(target, default, ToolResourceKinds.ToolSet, name + ".tools"), default))!;
        Assert.IsNotNull(group); Assert.IsFalse(group.Value.Definition.Publish);
        Assert.AreEqual(target, group.Value.ScopeRef); Assert.HasCount(1, group.Value.Definition.Members);
        var member = group.Value.Definition.Members.Single(); Assert.AreEqual(target, member.Tool.ScopeRef);
        var tool = (await store.GetExactAsync<ToolResource>(new(target, default, ToolResourceKinds.Tool, member.Tool.Name), default))!;
        Assert.IsFalse(tool.Value.Definition.Enabled); Assert.AreEqual(group.Value.Governance, tool.Value.Governance);
        var sets = factory.Services.GetRequiredService<ToolSetService>();
        Assert.IsNull(await sets.GetVersionExactAsync(target, default, group.Value.Name, "1.0.0", default));
        using (factory.Services.GetRequiredService<IResourceGovernanceContext>().Push(group.Value.Governance))
        {
            var denied = await Assert.ThrowsExactlyAsync<ToolSetValidationException>(() => sets.PublishExactAsync(target, default, group.Value.Name, "1.0.0", default));
            Assert.AreEqual("tool_set_member_unavailable", denied.Code);
        }
        var other = target.Kind == ResourceScopeKind.Instance ? ResourceScopeRef.Workspace(context.WorkspaceId) : ResourceScopeRef.Instance;
        await store.PutExactAsync(other, group.Value with { Uid = Guid.NewGuid(), ScopeRef = other, Governance = ResourceGovernance.User }, null, true, default);
        var registration = (await store.GetExactAsync<ExtensionRegistrationResource>(new(target, default, ExtensionKinds.ExtensionRegistration, name), default))!;
        var provisioner = factory.Services.GetServices<IAepEnrollmentResourceProvisioner>().OfType<AepEnrollmentToolProvisioner>().Single();
        await provisioner.ProvisionAsync(registration.Value, handler.Manifest(), default);
        var repeated = (await sets.GetExactAsync(target, default, group.Value.Name, default))!;
        Assert.AreEqual(group.Value.Uid, repeated.Value.Uid); Assert.AreEqual(group.ETag, repeated.ETag);
        var independent = (await sets.GetExactAsync(other, default, group.Value.Name, default))!;
        var manifest = handler.Manifest();
        var updatedGroup = manifest.Contributions.ToolSets!.Single() with { DisplayName = "Updated extension tools" };
        await provisioner.ProvisionAsync(registration.Value, manifest with { Contributions = manifest.Contributions with { ToolSets = [updatedGroup] } }, default);
        Assert.AreEqual("Updated extension tools", (await sets.GetExactAsync(target, default, group.Value.Name, default))!.Value.Definition.DisplayName);
        Assert.AreEqual(independent.ETag, (await sets.GetExactAsync(other, default, group.Value.Name, default))!.ETag);
        Assert.IsFalse((await store.GetExactAsync<ToolResource>(new(target, default, ToolResourceKinds.Tool, tool.Value.Name), default))!.Value.Definition.Enabled);
        var operatorName = name + ".operator";
        var operatorGroup = await store.PutExactAsync(target, group.Value with
        {
            Uid = Guid.NewGuid(), Metadata = group.Value.Metadata with { Name = operatorName }, Governance = ResourceGovernance.User
        }, null, true, default);
        var conflicting = updatedGroup with { Id = "operator" };
        await Assert.ThrowsExactlyAsync<ToolResourceValidationException>(() => provisioner.ProvisionAsync(registration.Value,
            manifest with { Contributions = manifest.Contributions with { ToolSets = [updatedGroup, conflicting] } }, default));
        Assert.AreEqual(operatorGroup.ETag, (await sets.GetExactAsync(target, default, operatorName, default))!.ETag);
        using (factory.Services.GetRequiredService<IResourceGovernanceContext>().Push(group.Value.Governance))
        {
            var currentTool = (await store.GetExactAsync<ToolResource>(new(target, default, ToolResourceKinds.Tool, tool.Value.Name), default))!;
            await store.PutExactAsync(target, currentTool.Value with { Generation = checked(currentTool.Value.Generation + 1), Definition = currentTool.Value.Definition with { Enabled = true } }, currentTool.ETag, false, default);
        }
        await provisioner.ProvisionAsync(registration.Value, manifest with { Contributions = manifest.Contributions with { ToolSets = [updatedGroup] } }, default);
        Assert.IsTrue((await store.GetExactAsync<ToolResource>(new(target, default, ToolResourceKinds.Tool, tool.Value.Name), default))!.Value.Definition.Enabled);
        Assert.IsNull(await sets.GetVersionExactAsync(target, default, group.Value.Name, "1.0.0", default));

    }
}
