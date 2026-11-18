using Agentstration.Aep.Abstractions;
using Agentstration.Extensions;
using Agentstration.Extensions.Aep;
using Agentstration.Extensions.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.Resources;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agentstration.Management.Tests;

public sealed partial class AepEnrollmentTests
{
    [TestMethod]
    [DataRow("/instance", ResourceScopeKind.Instance)]
    [DataRow("/tenants/dev", ResourceScopeKind.Tenant)]
    [DataRow("/tenants/dev/workspaces/default", ResourceScopeKind.Workspace)]
    public async Task AutomaticSharedKeyEnrollmentUsesTheConfiguredTarget(string level, ResourceScopeKind kind)
    {
        var keyPath = Path.GetTempFileName();
        const string key = "automatic-enrollment-test-token-material-256-bits";
        await File.WriteAllTextAsync(keyPath, key);
        try
        {
            await using var factory = EnrollmentFactory().WithWebHostBuilder(builder =>
            {
                builder.UseSetting("Agentstration:Aep:Enrollment:AutoEnrollmentLevel", level);
                builder.UseSetting("Agentstration:Extensions:extension.automatic:EnrollmentMode", "SharedKeyFile");
                builder.UseSetting("Agentstration:Extensions:extension.automatic:SharedKeyFile:Path", keyPath);
                builder.ConfigureServices(services => services.RemoveAll<IAepEnrollmentResourceProvisioner>());
            });
            var context = await GetBootstrapContextAsync(factory);
            var service = factory.Services.GetRequiredService<AepEnrollmentService>();
            var automatic = factory.Services.GetRequiredService<AepAutomaticEnrollmentService>();
            var announcement = AutomaticAnnouncement();
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var proof = new AepEnrollmentProof(timestamp, AepEnrollmentProofs.Sign(announcement, timestamp, key));
            _ = await service.AnnounceAsync(announcement, proof, default);
            await automatic.ProcessPendingAsync(default);
            var request = (await service.ListAsync(context, default)).Single(value => value.Definition.InstanceId == announcement.InstanceId);
            Assert.AreEqual(AepEnrollmentState.Available, request.Definition.State);
            Assert.AreEqual(kind, request.Definition.TargetScopeRef!.Value.Kind);
            Assert.AreEqual("configuration", request.Definition.AutomaticEnrollmentPolicy);
            var generation = request.Generation;
            await Task.WhenAll(automatic.ProcessPendingAsync(default), automatic.ProcessPendingAsync(default));
            Assert.AreEqual(generation, (await service.ListAsync(context, default))
                .Single(value => value.Definition.InstanceId == announcement.InstanceId).Generation);
            using (PushOwnerScope(factory, context, request.Definition.TargetScopeRef.Value))
                await service.UnenrollAsync(context, announcement.InstanceId, default);
            await automatic.ProcessPendingAsync(default);
            Assert.AreEqual(AepEnrollmentState.Unpaired, (await service.ListAsync(context, default))
                .Single(value => value.Definition.InstanceId == announcement.InstanceId).Definition.State);
        }
        finally { File.Delete(keyPath); }
    }

    [TestMethod]
    public async Task AutomaticEnrollmentDoesNotReenableADisabledLegacyRegistration()
    {
        var keyPath = Path.GetTempFileName();
        const string key = "legacy-disabled-registration-disposable-test-key";
        await File.WriteAllTextAsync(keyPath, key);
        try
        {
            await using var factory = EnrollmentFactory().WithWebHostBuilder(builder =>
            {
                builder.UseSetting("Agentstration:Aep:Enrollment:AutoEnrollmentLevel", "/instance");
                builder.UseSetting("Agentstration:Extensions:extension.automatic:EnrollmentMode", "SharedKeyFile");
                builder.UseSetting("Agentstration:Extensions:extension.automatic:SharedKeyFile:Path", keyPath);
                builder.UseSetting("Agentstration:Extensions:extension.automatic:RegistrationName", "legacy-registration");
                builder.ConfigureServices(services => services.RemoveAll<IAepEnrollmentResourceProvisioner>());
            });
            var context = await GetBootstrapContextAsync(factory);
            var announcement = AutomaticAnnouncement();
            using var system = factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushSystem();
            var registrations = factory.Services.GetRequiredService<ExtensionRegistrationManagementService>();
            var existing = await registrations.SynchronizeAsync("legacy-registration", new()
            {
                DisplayName = "Disabled legacy connection",
                Endpoint = announcement.Endpoint,
                ExpectedExtensionId = announcement.Extension.Id,
                Enabled = false,
                Source = ExtensionRegistrationSource.Configuration
            }, default);
            var enrollment = factory.Services.GetRequiredService<AepEnrollmentService>();
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            _ = await enrollment.AnnounceAsync(announcement, new(timestamp,
                AepEnrollmentProofs.Sign(announcement, timestamp, key)), default);
            await factory.Services.GetRequiredService<AepAutomaticEnrollmentService>().ProcessPendingAsync(default);
            var request = (await enrollment.ListAsync(context, default)).Single(value => value.Definition.InstanceId == announcement.InstanceId);
            Assert.AreEqual(AepEnrollmentState.VerificationFailed, request.Definition.State);
            StringAssert.Contains(request.Definition.Outcome, "disabled");
            var unchanged = await registrations.GetExactAsync(ResourceScopeRef.Instance, ResourceNamespace.Default, "legacy-registration", default);
            Assert.AreEqual(existing.ETag, unchanged!.ETag);
            Assert.IsFalse(unchanged.Value.Definition.Enabled);
        }
        finally { File.Delete(keyPath); }
    }

    [TestMethod]
    public async Task EnrollmentPoliciesOverrideTheDefaultByInstallationAndPermitManualExceptions()
    {
        await using var factory = EnrollmentFactory().WithWebHostBuilder(builder =>
            builder.UseSetting("Agentstration:Aep:Enrollment:AutoEnrollmentLevel", "/instance"));
        var context = await GetBootstrapContextAsync(factory);
        var policies = factory.Services.GetRequiredService<ExtensionEnrollmentPolicyService>();
        var instanceId = Guid.NewGuid();
        using var system = factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushSystem();
        await policies.CreateAsync(AutomaticPolicy("manual", null), default);
        Assert.IsNull(await policies.SelectAsync("extension.automatic", Guid.NewGuid(), default));
        await policies.CreateAsync(AutomaticPolicy("exact", "/tenants/dev/workspaces/default", instanceId), default);
        var selected = await policies.SelectAsync("extension.automatic", instanceId, default);
        Assert.AreEqual(ResourceScopeRef.Workspace(context.WorkspaceId), selected!.ScopeRef);
        Assert.AreEqual(context.TenantId, selected.TenantId);
        Assert.AreEqual("exact", selected.PolicyName);
        Assert.AreEqual(ResourceScopeRef.Instance, (await policies.SelectAsync("extension.other", Guid.NewGuid(), default))!.ScopeRef);
        var conflict = await Assert.ThrowsAsync<AepEnrollmentException>(() =>
            policies.CreateAsync(AutomaticPolicy("duplicate", "/instance"), default));
        Assert.AreEqual("enrollment_policy_conflict", conflict.Code);
        var canonical = await policies.ResolveLevelAsync($"/tenants/{context.TenantId:D}/workspaces/{context.WorkspaceId:D}", "test", default);
        Assert.AreEqual(selected.ScopeRef, canonical.ScopeRef);
        var missing = await Assert.ThrowsAsync<AepEnrollmentException>(() =>
            policies.ResolveLevelAsync($"/tenants/{Guid.NewGuid():D}/workspaces/{context.WorkspaceId:D}", "test", default));
        Assert.AreEqual("enrollment_target_missing", missing.Code);
    }

    [TestMethod]
    public async Task AutomaticInstallationsWithTheSamePublicIdKeepIndependentTrustAndRegistrations()
    {
        var firstPath = Path.GetTempFileName();
        var secondPath = Path.GetTempFileName();
        var first = AutomaticAnnouncement() with { Endpoint = new("https://first-automatic.example/") };
        var second = AutomaticAnnouncement() with { Endpoint = new("https://second-automatic.example/") };
        const string firstKey = "first-installation-disposable-independent-test-key";
        const string secondKey = "second-installation-disposable-independent-test-key";
        await File.WriteAllTextAsync(firstPath, firstKey);
        await File.WriteAllTextAsync(secondPath, secondKey);
        try
        {
            await using var factory = EnrollmentFactory().WithWebHostBuilder(builder =>
            {
                builder.UseSetting("Agentstration:Aep:Enrollment:AutoEnrollmentLevel", "/instance");
                builder.UseSetting("Agentstration:Extensions:extension.automatic:EnrollmentMode", "SharedKeyFile");
                builder.UseSetting($"Agentstration:Extensions:extension.automatic:Instances:{first.InstanceId:D}:SharedKeyFile:Path", firstPath);
                builder.UseSetting($"Agentstration:Extensions:extension.automatic:Instances:{second.InstanceId:D}:SharedKeyFile:Path", secondPath);
                builder.ConfigureServices(services => services.RemoveAll<IAepEnrollmentResourceProvisioner>());
            });
            var context = await GetBootstrapContextAsync(factory);
            var service = factory.Services.GetRequiredService<AepEnrollmentService>();
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            _ = await service.AnnounceAsync(first, new(timestamp, AepEnrollmentProofs.Sign(first, timestamp, firstKey)), default);
            var failure = await Assert.ThrowsAsync<AepEnrollmentException>(() => service.AnnounceAsync(second,
                new(timestamp, AepEnrollmentProofs.Sign(second, timestamp, firstKey)), default));
            Assert.AreEqual("shared_key_proof_invalid", failure.Code);
            _ = await service.AnnounceAsync(second, new(timestamp, AepEnrollmentProofs.Sign(second, timestamp, secondKey)), default);
            await factory.Services.GetRequiredService<AepAutomaticEnrollmentService>().ProcessPendingAsync(default);
            var requests = (await service.ListAsync(context, default)).Where(value => value.Definition.ExtensionId == "extension.automatic").ToArray();
            Assert.HasCount(2, requests);
            Assert.IsTrue(requests.All(value => value.Definition.State == AepEnrollmentState.Available));
            Assert.HasCount(2, requests.Select(value => value.Definition.RegistrationName).Distinct().ToArray());
            Assert.AreEqual(first.Endpoint, requests.Single(value => value.Definition.InstanceId == first.InstanceId).Definition.Endpoint);
            Assert.AreEqual(second.Endpoint, requests.Single(value => value.Definition.InstanceId == second.InstanceId).Definition.Endpoint);
        }
        finally { File.Delete(firstPath); File.Delete(secondPath); }
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("/workspaces/default")]
    [DataRow("/tenants/dev/")]
    [DataRow("/tenants/../workspaces/default")]
    [DataRow("/instance/anything")]
    public void AutomaticEnrollmentRejectsMalformedTargets(string level) =>
        Assert.ThrowsExactly<AepEnrollmentException>(() => ExtensionEnrollmentPolicyService.ValidateLevel(level));

    private static AepEnrollmentAnnouncement AutomaticAnnouncement() => new(
        Guid.NewGuid(), new("extension.automatic", "Automatic test", "1.0.0"),
        new Uri("https://automatic.example/"), null, AepEnrollmentMethod.SharedKeyFile);

    private static ExtensionEnrollmentPolicyResource AutomaticPolicy(string name, string? level, Guid? instanceId = null) => new()
    {
        ApiVersion = ResourceApiVersions.CoreV1,
        Kind = ExtensionKinds.ExtensionEnrollmentPolicy,
        Metadata = new() { Name = name },
        ScopeRef = ResourceScopeRef.Instance,
        Definition = new() { ExtensionId = "extension.automatic", InstanceId = instanceId, AutoEnrollmentLevel = level }
    };
}
