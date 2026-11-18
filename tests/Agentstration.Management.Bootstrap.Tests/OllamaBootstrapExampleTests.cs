using Agentstration.Agents;
using Agentstration.Extensions;
using Agentstration.Identity.Contracts;
using Agentstration.Models;
using Agentstration.ResourceManagement;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.Security.AspNetCoreIdentity;
using Agentstration.Web.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Management.Tests;

public sealed partial class DeclarativeBootstrapTests
{
    [TestMethod]
    public async Task OllamaExampleSharesOnlyExplicitScopesAndPreservesExistingPolicies()
    {
        using var directory = new TemporaryDirectory();
        var catalog = Path.Combine(FindRepositoryRoot(), "deploy", "bootstrap", "profiles");
        foreach (var name in new[] { "development", "ollama-instance", "ollama-tenant", "ollama-workspace" })
        {
            var target = Directory.CreateDirectory(Path.Combine(directory.Path, name));
            foreach (var path in Directory.EnumerateFiles(Path.Combine(catalog, name), "*.yaml"))
                File.Copy(path, Path.Combine(target.FullName, Path.GetFileName(path)));
        }
        var initial = Path.Combine(directory.Path, "development");
        await File.WriteAllTextAsync(Path.Combine(initial, "35-other-topology.yaml"),
            Tenant("other", "Other Tenant") + "\n---\n" + Workspace("isolated", "Isolated", "other"));
        await using var factory = Factory(initial, InitialPassword, configureOllamaExtension: true);
        using var client = factory.CreateClient();
        var services = factory.Services;
        var account = await services.GetRequiredService<UserManager<LocalIdentityUser>>().FindByNameAsync("admin");
        Assert.IsNotNull(account);
        var principal = await services.GetRequiredService<IPrincipalResolver>().ResolveLocalAsync(account.Id, default);
        Assert.IsNotNull(principal);
        using var system = services.GetRequiredService<IRequestContextScopeFactory>().PushSystem();
        _ = await services.GetRequiredService<ExtensionSourceDiscoveryService>().DiscoverAsync(default);
        var identities = services.GetRequiredService<IIdentityStore>();
        var tenant = await identities.FindTenantByNameAsync("dev", default);
        var workspace = await identities.FindWorkspaceByNameAsync(tenant!.Id, "default", default);
        var other = await identities.FindTenantByNameAsync("other", default);
        var isolated = await identities.FindWorkspaceByNameAsync(other!.Id, "isolated", default);
        var management = services.GetRequiredService<BootstrapProfileManagementService>();
        var applications = new BootstrapProfileSelection[]
        {
            new(["ollama-instance"]),
            new(["ollama-tenant"], new(tenant.Id)),
            new(["ollama-workspace"], new(tenant.Id, workspace!.Id))
        };
        foreach (var application in applications)
        {
            var preview = await management.PreviewAsync(application, principal.Id, default);
            Assert.IsTrue(preview.CanApply, string.Join("; ", preview.Resources.Select(value => value.Message)));
            var result = await management.ApplyAsync(application, preview.Digest, principal.Id, default);
            Assert.IsNull(result.Definition.Error);
        }
        var store = services.GetRequiredService<IResourceStore>();
        var provider = await store.GetExactAsync<ModelProviderResource>(ScopedResourceAddress.Create(
            ResourceScopeRef.Instance, ResourceNamespace.Default, ModelResourceKinds.ModelProvider, "bootstrap-ollama"), default);
        var model = await store.GetExactAsync<ModelProfileResource>(ScopedResourceAddress.Create(
            ResourceScopeRef.Tenant(tenant.Id), ResourceNamespace.Default, ModelResourceKinds.ModelProfile, "ollama-chat"), default);
        var agent = await store.GetExactAsync<AgentResource>(ScopedResourceAddress.Create(
            ResourceScopeRef.Workspace(workspace.Id), ResourceNamespace.Default, AgentResourceKinds.Agent, "ollama-assistant"), default);
        Assert.IsNotNull(provider);
        Assert.IsNotNull(model);
        Assert.IsNotNull(agent);
        Assert.AreEqual("qwen3:1.7b", model.Value.Definition.Model.Name);
        Assert.AreEqual("ollama-chat", agent.Value.Definition.ModelProfile.Name);
        var policies = services.GetRequiredService<IResourceAccessPolicyService>();
        var identity = new ResourceAccessIdentity(model.Value.Uid, model.Value.ScopeRef!.Value,
            model.Value.Namespace, model.Value.Kind, model.Value.Generation);
        Assert.IsTrue((await policies.EvaluateAsync(identity, ResourceScopeRef.Workspace(workspace.Id), ResourceAccessAction.Use, default)).Allowed);
        Assert.IsFalse((await policies.EvaluateAsync(identity, ResourceScopeRef.Workspace(isolated!.Id), ResourceAccessAction.Use, default)).Allowed);
        var existing = await policies.GetAsync(provider.Value.Uid, default);
        Assert.IsNotNull(existing);
        var revoked = await policies.PutAsync(existing.Policy with { Grants = [] }, existing.ETag, default);
        var repeat = await management.PreviewAsync(applications[0], principal.Id, default);
        Assert.IsTrue(repeat.Resources.All(value => value.Disposition == BootstrapResourceDisposition.Skip));
        _ = await management.ApplyAsync(applications[0], repeat.Digest, principal.Id, default);
        Assert.AreEqual(revoked.ETag, (await policies.GetAsync(provider.Value.Uid, default))!.ETag);
        var repeatedAgent = await store.GetExactAsync<AgentResource>(ScopedResourceAddress.Create(
            ResourceScopeRef.Workspace(workspace.Id), ResourceNamespace.Default, AgentResourceKinds.Agent, "ollama-assistant"), default);
        Assert.AreEqual(agent.ETag, repeatedAgent!.ETag);

        var forbidden = Directory.CreateDirectory(Path.Combine(directory.Path, "forbidden"));
        await File.WriteAllTextAsync(Path.Combine(forbidden.FullName, "profile.yaml"), Profile("forbidden", "tenant"));
        await File.WriteAllTextAsync(Path.Combine(forbidden.FullName, "10-policy.yaml"), """
            apiVersion: agentstration.io/v1
            kind: ResourceAccessPolicy
            metadata: { name: forbidden }
            definition:
              resource: { kind: ModelProfile, name: no-share }
              grants:
                - { targetLevel: /tenants/other/workspaces/isolated, actions: [discover, use] }
            """);
        await File.WriteAllTextAsync(Path.Combine(forbidden.FullName, "00-model.yaml"),
            (await File.ReadAllTextAsync(Path.Combine(directory.Path, "ollama-tenant", "10-model-profile.yaml")))
            .Replace("name: ollama-chat", "name: no-share", StringComparison.Ordinal));
        // Restore the provider grant so this preview isolates the forbidden policy target.
        _ = await policies.PutAsync(existing.Policy, revoked.ETag, default);
        var rejected = await management.PreviewAsync(new(["forbidden"], new(tenant.Id)), principal.Id, default);
        Assert.IsFalse(rejected.CanApply);
        StringAssert.Contains(rejected.Resources.Single(value => value.Kind == "ResourceAccessPolicy").Message, "descendant");
        Assert.IsNull(await store.GetExactAsync<ModelProfileResource>(ScopedResourceAddress.Create(
            ResourceScopeRef.Tenant(tenant.Id), ResourceNamespace.Default, ModelResourceKinds.ModelProfile, "no-share"), default));

        using (services.GetRequiredService<IResourceGovernanceContext>().Push(
            ResourceGovernance.Extension("test-installation", "extension.untrusted", "1.0.0")))
        {
            var managed = await management.PreviewAsync(applications[0], principal.Id, default);
            Assert.IsFalse(managed.CanApply);
            StringAssert.Contains(managed.Resources.Single(value => value.Kind == "ResourceAccessPolicy").Message, "local administrative");
            var handler = services.GetServices<IBootstrapResourceHandler>().Single(value => value.Kind == "ResourceAccessPolicy");
            var failure = await Assert.ThrowsAsync<DeclarativeBootstrapException>(() => handler.ApplyAsync(
                new BootstrapResourceDocument(), new("aep:untrusted", directory.Path, BootstrapProfileScope.Instance), default));
            StringAssert.Contains(failure.Message, "local administrative");
        }
    }
}
