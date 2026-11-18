using Agentstration.Extensions.Aep;
using Agentstration.Extensions.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.Resources;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Management.Tests;

public sealed partial class DeclarativeBootstrapTests
{
    [TestMethod]
    public async Task BootstrapCreatesEnrollmentPolicyAfterItsTargetTopology()
    {
        using var directory = new TemporaryDirectory();
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "00-admin.yaml"), PlatformAdministrator());
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "10-tenant.yaml"), Tenant("bootstrap", "Bootstrap"));
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "20-workspace.yaml"), Workspace("target", "Target", "bootstrap"));
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "30-enrollment.yaml"), EnrollmentPolicy("target-policy", "/tenants/bootstrap/workspaces/target"));
        await using var factory = Factory(directory.Path, InitialPassword);
        using var client = factory.CreateClient();
        using var health = await client.GetAsync("/health");
        health.EnsureSuccessStatusCode();
        using var system = factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushSystem();
        var policies = factory.Services.GetRequiredService<ExtensionEnrollmentPolicyService>();
        var stored = await policies.GetAsync("target-policy", default);
        Assert.IsNotNull(stored);
        Assert.AreEqual(ResourceScopeRef.Instance, stored.Value.ScopeRef);
        var target = await policies.SelectAsync("extension.bootstrap", Guid.NewGuid(), default);
        var identities = factory.Services.GetRequiredService<IIdentityStore>();
        var tenant = await identities.FindTenantByNameAsync("bootstrap", default);
        var workspace = await identities.FindWorkspaceByNameAsync(tenant!.Id, "target", default);
        Assert.AreEqual(ResourceScopeRef.Workspace(workspace!.Id), target!.ScopeRef);
    }

    [TestMethod]
    public async Task BootstrapRejectsConflictingEnrollmentSelectorsBeforeMutation()
    {
        using var directory = new TemporaryDirectory();
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "10-first.yaml"), EnrollmentPolicy("first", "/instance"));
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "20-second.yaml"), EnrollmentPolicy("second", "/tenants/dev"));
        await using var factory = Factory(directory.Path, InitialPassword);
        var failure = Assert.ThrowsExactly<Agentstration.Web.Hosting.DeclarativeBootstrapException>(() => factory.CreateClient());
        StringAssert.Contains(failure.Message, "Several policies");
    }

    private static string EnrollmentPolicy(string name, string level) => $$"""
        apiVersion: agentstration.io/v1
        kind: ExtensionEnrollmentPolicy
        metadata:
          name: {{name}}
        definition:
          extensionId: extension.bootstrap
          autoEnrollmentLevel: {{level}}
        """;
}
