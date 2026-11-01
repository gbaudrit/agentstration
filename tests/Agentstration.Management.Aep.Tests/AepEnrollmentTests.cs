using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.Aep.Abstractions;
using Agentstration.Extensions;
using Agentstration.Extensions.Aep;
using Agentstration.Extensions.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.ModelProviders;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Security.Contracts;
using Agentstration.Tools;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class AepEnrollmentTests
{
    private static WebApplicationFactory<Program> EnrollmentFactory() => new AepEnrollmentTestFactory();

    private static Task<RequestContext> GetBootstrapContextAsync(WebApplicationFactory<Program> factory) =>
        factory.Services
            .GetRequiredService<ILocalEnvironmentBootstrapper>()
            .EnsureInitializedAsync(default);

    private sealed class AepEnrollmentTestFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Agentstration:Testing:ApiOnly", "true");
            builder.UseSetting("Logging:LogLevel:Default", "Warning");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IPlatformAuthorizationService>();
                services.RemoveAll<IExtensionInspector>();
                services.AddSingleton<IPlatformAuthorizationService, AllowEnrollmentPlatformAdministrator>();
                services.AddSingleton<IExtensionInspector, UnavailableExtensionInspector>();
            });
        }
    }

    private sealed class UnavailableExtensionInspector : IExtensionInspector
    {
        public bool CanHandle(string providerType) => true;
        public bool CanInspectEndpoint(Uri endpoint) => true;

        public ValueTask<ExtensionInspection> InspectAsync(
            ModelProviderConfiguration provider,
            CancellationToken cancellationToken = default) =>
            InspectAsync(provider.Name, provider.Endpoint, cancellationToken);

        public ValueTask<ExtensionInspection> InspectAsync(
            string registrationName,
            Uri endpoint,
            CancellationToken cancellationToken = default) =>
            endpoint.Host == "instance-inventory.extension.example"
                ? ValueTask.FromResult(new ExtensionInspection(
                    registrationName,
                    endpoint,
                    "available",
                    new ExtensionIdentity("extension.instance-inventory", "Instance inventory", "1.0.0", null),
                    [new ExtensionContribution(AepContributionKinds.Tool, "lookup")],
                    []))
                : ValueTask.FromResult(new ExtensionInspection(
                    registrationName,
                    endpoint,
                    "unavailable",
                    null,
                    [],
                    [],
                    "The test extension is intentionally unavailable."));
    }

    [TestMethod]
    public async Task PairingCodeCanBeDisabledAndConfigurationCanLockTheMode()
    {
        await using (var factory = EnrollmentFactory())
        {
            var context = await GetBootstrapContextAsync(factory);
            var settings = factory.Services.GetRequiredService<AepEnrollmentSettingsService>();
            var updated = await settings.UpdateAsync(context, pairingCodeEnabled: false, sharedKeyFileEnabled: true, ifMatch: null, default);
            Assert.IsFalse(updated.PairingCodeEnabled);
            Assert.IsTrue(updated.PairingCodeConfigurable);

            var service = factory.Services.GetRequiredService<AepEnrollmentService>();
            var rejected = await Assert.ThrowsAsync<AepEnrollmentException>(() => service.AnnounceAsync(new(
                Guid.NewGuid(),
                new AepExtensionIdentity("extension.disabled-pairing", "Disabled pairing", "1.0.0"),
                new Uri("https://extension.example/"),
                new Uri("https://extension.example/aep/enrollment/pair")), default));
            Assert.AreEqual("enrollment_mode_disabled", rejected.Code);
        }

        await using var lockedFactory = EnrollmentFactory().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<AepEnrollmentPolicyOptions>();
            services.AddSingleton(new AepEnrollmentPolicyOptions { PairingCodeAllowed = false });
        }));
        var lockedContext = await GetBootstrapContextAsync(lockedFactory);
        var lockedSettings = lockedFactory.Services.GetRequiredService<AepEnrollmentSettingsService>();
        var locked = await lockedSettings.UpdateAsync(lockedContext, pairingCodeEnabled: true, sharedKeyFileEnabled: true, ifMatch: null, default);
        Assert.IsFalse(locked.PairingCodeEnabled);
        Assert.IsFalse(locked.PairingCodeConfigurable);
    }

    [TestMethod]
    public async Task PairingAnnouncementsAreIdempotentAndConcurrentClaimsHaveOneWinner()
    {
        await using var factory = EnrollmentFactory();
        var context = await GetBootstrapContextAsync(factory);
        var service = factory.Services.GetRequiredService<AepEnrollmentService>();
        var instanceId = Guid.NewGuid();
        var announcement = new AepEnrollmentAnnouncement(
            instanceId,
            new AepExtensionIdentity("extension.pairing-test", "Pairing test", "1.0.0"),
            new Uri("https://extension.example/"),
            new Uri("https://extension.example/aep/enrollment/pair"));

        _ = await service.AnnounceAsync(announcement, default);
        _ = await service.AnnounceAsync(announcement, default);
        Assert.HasCount(1, await service.ListAsync(context, default));
        await service.AssignAsync(context, instanceId, ResourceScopeRef.Workspace(context.WorkspaceId), default);

        var first = await service.RotateAsync(context, instanceId, default);
        var second = await service.RotateAsync(context, instanceId, default);
        var stale = await Assert.ThrowsAsync<AepEnrollmentException>(() => service.ClaimAsync(
            new(instanceId, instanceId, first.Code), default));
        Assert.AreEqual("code_invalid", stale.Code);

        async Task<bool> ClaimAsync()
        {
            try
            {
                _ = await service.ClaimAsync(new(instanceId, instanceId, second.Code), default);
                return true;
            }
            catch (AepEnrollmentException exception) when (exception.Code == "code_consumed")
            {
                return false;
            }
        }

        var results = await Task.WhenAll(ClaimAsync(), ClaimAsync());
        Assert.AreEqual(1, results.Count(value => value));
        var request = (await service.ListAsync(context, default)).Single();
        Assert.AreEqual(AepEnrollmentState.CredentialIssued, request.Definition.State);
        Assert.IsNull(request.Definition.CodeDigest);
        Assert.IsNull(request.Definition.CompletionDigest);
        Assert.IsNotNull(request.Definition.CredentialSecretName);
        Assert.IsNotNull(request.Definition.RegistrationName);

        var audit = (await factory.Services.GetRequiredService<ISecurityAuditStore>().ListLatestAsync(100, default))
            .Where(value => value.TargetAccountId == instanceId)
            .ToArray();
        Assert.IsTrue(audit.Any(value => value.Action == SecurityAuditActions.AepEnrollmentAnnounced));
        Assert.IsTrue(audit.Any(value => value.Action == SecurityAuditActions.AepPairingCodeIssued));
        Assert.IsTrue(audit.Any(value => value.Action == SecurityAuditActions.AepCredentialIssued));
        var auditJson = JsonSerializer.Serialize(audit);
        Assert.DoesNotContain(second.Code, auditJson, StringComparison.Ordinal);
        Assert.DoesNotContain("CodeDigest", auditJson, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task PairingAnnouncementEndpointAcceptsTheAepProtocolJsonRepresentation()
    {
        await using var factory = EnrollmentFactory();
        using var client = factory.CreateClient();
        var instanceId = Guid.NewGuid();
        using var response = await client.PostAsJsonAsync(
            AepEnrollmentProtocol.AnnouncementPath,
            new AepEnrollmentAnnouncement(
                instanceId,
                new AepExtensionIdentity("extension.http-pairing", "HTTP pairing", "1.0.0"),
                new Uri("http://localhost:5260/"),
                new Uri("http://localhost:5260/aep/enrollment/pair")),
            AepProtocol.JsonOptions);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        var result = await response.Content.ReadFromJsonAsync<AepEnrollmentAnnouncementResponse>(AepProtocol.JsonOptions);
        Assert.IsNotNull(result);
        Assert.AreEqual(instanceId, result.RequestId);
        Assert.AreEqual("pending", result.State);
    }

    [TestMethod]
    public async Task PairingAnnouncementRejectsCrossOriginForm()
    {
        await using var factory = EnrollmentFactory();
        var context = await GetBootstrapContextAsync(factory);
        var service = factory.Services.GetRequiredService<AepEnrollmentService>();
        var instanceId = Guid.NewGuid();

        var exception = await Assert.ThrowsAsync<AepEnrollmentException>(() => service.AnnounceAsync(new(
            instanceId,
            new AepExtensionIdentity("extension.pairing-test", "Pairing test", "1.0.0"),
            new Uri("https://extension.example/"),
            new Uri("https://attacker.example/aep/enrollment/pair")), default));

        Assert.AreEqual("pairing_origin_mismatch", exception.Code);
    }

    [TestMethod]
    public async Task UnassignedAnnouncementsAreVisibleOnlyToPlatformAdministrators()
    {
        await using var factory = EnrollmentFactory().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IPlatformAuthorizationService>();
            services.AddSingleton<IPlatformAuthorizationService, DenyEnrollmentPlatformAdministrator>();
        }));
        var context = await GetBootstrapContextAsync(factory);
        var enrollment = factory.Services.GetRequiredService<AepEnrollmentService>();
        var endpoint = new Uri($"https://{Guid.NewGuid():N}.extension.example/");
        _ = await enrollment.AnnounceAsync(new(
            Guid.NewGuid(),
            new AepExtensionIdentity("extension.unassigned", "Unassigned", "1.0.0"),
            endpoint,
            new Uri(endpoint, AepEnrollmentProtocol.PairingPath)), default);

        Assert.IsEmpty(await enrollment.ListAsync(context, default));
        var inventory = await factory.Services.GetRequiredService<ExtensionInventoryService>().ListAsync(context, default);
        Assert.IsFalse(inventory.Any(value => value.Endpoint == endpoint));
    }

    [TestMethod]
    public async Task PairingAssignmentSupportsInstanceTenantAndWorkspaceScopes()
    {
        var handler = new EnrollmentLifecycleHandler("extension.scope-test");
        await using var factory = EnrollmentFactory().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IHttpClientFactory>();
            services.AddSingleton<IHttpClientFactory>(new EnrollmentHttpClientFactory(handler));
        }));
        var context = await GetBootstrapContextAsync(factory);
        var service = factory.Services.GetRequiredService<AepEnrollmentService>();
        var registrations = factory.Services.GetRequiredService<ExtensionRegistrationManagementService>();
        var targets = new[]
        {
            ResourceScopeRef.Instance,
            ResourceScopeRef.Tenant(context.TenantId),
            ResourceScopeRef.Workspace(context.WorkspaceId)
        };

        foreach (var target in targets)
        {
            var instanceId = Guid.NewGuid();
            _ = await service.AnnounceAsync(new(
                instanceId,
                new AepExtensionIdentity("extension.scope-test", "Scope test", "1.0.0"),
                new Uri($"https://{target.Kind.ToString().ToLowerInvariant()}.extension.example/"),
                new Uri($"https://{target.Kind.ToString().ToLowerInvariant()}.extension.example/aep/enrollment/pair")), default);

            var unassigned = await Assert.ThrowsAsync<AepEnrollmentException>(() => service.RotateAsync(context, instanceId, default));
            Assert.AreEqual("scope_required", unassigned.Code);
            if (target.Kind != ResourceScopeKind.Workspace)
            {
                using var workspaceScope = factory.Services
                    .GetRequiredService<IRequestContextScopeFactory>()
                    .Push(context);
                await Assert.ThrowsExactlyAsync<ResourceScopeAccessDeniedException>(() =>
                    service.AssignAsync(context, instanceId, target, default));
            }
            using var ownerScope = PushOwnerScope(factory, context, target);
            await service.AssignAsync(context, instanceId, target, default);
            var code = await service.RotateAsync(context, instanceId, default);
            var credential = await service.ClaimAsync(new(instanceId, instanceId, code.Code), default);
            _ = await service.ReadyAsync(new(instanceId, instanceId, credential.CompletionToken), default);

            using var systemScope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushSystem();
            var registration = await registrations.GetExactAsync(
                target,
                ResourceNamespace.Default,
                $"paired-{instanceId:N}",
                default);
            Assert.IsNotNull(registration, $"Expected a registration in {target.Value}.");
            var provider = await factory.Services.GetRequiredService<ToolManagementService>().GetProviderExactAsync(
                target,
                $"paired-{instanceId:N}",
                ResourceNamespace.Default,
                default);
            Assert.IsNull(provider, "An extension that advertises no Tools must not create a ToolProvider.");
        }
    }

    [TestMethod]
    public async Task PairingReadyProvisionsResourcesFromTheAuthenticatedManifestAndExactRegistration()
    {
        var recorder = new RecordingResourceProvisioner();
        var handler = new EnrollmentLifecycleHandler("extension.tools-test", advertiseTool: true);
        await using var factory = EnrollmentFactory().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IHttpClientFactory>();
            services.AddSingleton<IHttpClientFactory>(new EnrollmentHttpClientFactory(handler));
            services.RemoveAll<IAepEnrollmentResourceProvisioner>();
            services.AddSingleton<IAepEnrollmentResourceProvisioner>(recorder);
        }));
        var context = await GetBootstrapContextAsync(factory);
        var service = factory.Services.GetRequiredService<AepEnrollmentService>();
        var instanceId = Guid.NewGuid();
        var target = ResourceScopeRef.Tenant(context.TenantId);
        _ = await service.AnnounceAsync(new(
            instanceId,
            new AepExtensionIdentity("extension.tools-test", "Tools test", "1.0.0"),
            new Uri("https://tools.example/"),
            new Uri("https://tools.example/aep/enrollment/pair")), default);
        using var ownerScope = PushOwnerScope(factory, context, target);
        await service.AssignAsync(context, instanceId, target, default);
        var code = await service.RotateAsync(context, instanceId, default);
        var credential = await service.ClaimAsync(new(instanceId, instanceId, code.Code), default);

        var result = await service.ReadyAsync(new(instanceId, instanceId, credential.CompletionToken), default);

        Assert.AreEqual("available", result.State);
        Assert.IsNotNull(recorder.Registration);
        Assert.AreEqual(target, recorder.Registration.ScopeRef);
        Assert.AreEqual($"paired-{instanceId:N}", recorder.Registration.Name);
        Assert.HasCount(1, recorder.Manifest!.Contributions.Tools!);
        Assert.AreEqual("lookup", recorder.Manifest.Contributions.Tools![0].Id);
    }

    [TestMethod]
    public async Task InstanceInventoryPreservesTheSelectedScopeWhenLoadingContributions()
    {
        var handler = new EnrollmentLifecycleHandler("extension.instance-inventory", advertiseTool: true);
        await using var factory = EnrollmentFactory().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IHttpClientFactory>();
            services.AddSingleton<IHttpClientFactory>(new EnrollmentHttpClientFactory(handler));
            services.RemoveAll<IToolProviderDiscovery>();
            services.AddSingleton<IToolProviderDiscovery, StaticToolDiscovery>();
        }));
        var context = await GetBootstrapContextAsync(factory);
        var enrollment = factory.Services.GetRequiredService<AepEnrollmentService>();
        var instanceId = Guid.NewGuid();
        var endpoint = new Uri("https://instance-inventory.extension.example/");
        _ = await enrollment.AnnounceAsync(new(
            instanceId,
            new AepExtensionIdentity("extension.instance-inventory", "Instance inventory", "1.0.0"),
            endpoint,
            new Uri(endpoint, AepEnrollmentProtocol.PairingPath)), default);
        using var ownerScope = PushOwnerScope(factory, context, ResourceScopeRef.Instance);
        await enrollment.AssignAsync(context, instanceId, ResourceScopeRef.Instance, default);
        var code = await enrollment.RotateAsync(context, instanceId, default);
        var credential = await enrollment.ClaimAsync(new(instanceId, instanceId, code.Code), default);
        _ = await enrollment.ReadyAsync(new(instanceId, instanceId, credential.CompletionToken), default);

        var item = (await factory.Services.GetRequiredService<ExtensionInventoryService>().ListAsync(context, default))
            .Single(value => value.EnrollmentInstanceId == instanceId);

        Assert.AreEqual(ResourceScopeRef.Instance, item.RegistrationScopeRef);
        Assert.IsNotNull(item.Extension);
        Assert.IsTrue(
            item.Extension.Contributions.Any(value => value.Kind == AepContributionKinds.Tool && value.Id == "lookup"),
            $"Expected lookup Tool contribution; status={item.AvailabilityStatus}, details={item.Extension.Details}, contributions={string.Join(',', item.Extension.Contributions.Select(value => $"{value.Kind}:{value.Id}"))}.");
    }

    [TestMethod]
    public async Task PairingReadyFailsEnrollmentWhenResourceProvisioningFails()
    {
        var recorder = new RecordingResourceProvisioner { Failure = new InvalidOperationException("collision") };
        var handler = new EnrollmentLifecycleHandler("extension.tools-failure", advertiseTool: true);
        await using var factory = EnrollmentFactory().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IHttpClientFactory>();
            services.AddSingleton<IHttpClientFactory>(new EnrollmentHttpClientFactory(handler));
            services.RemoveAll<IAepEnrollmentResourceProvisioner>();
            services.AddSingleton<IAepEnrollmentResourceProvisioner>(recorder);
        }));
        var context = await GetBootstrapContextAsync(factory);
        var service = factory.Services.GetRequiredService<AepEnrollmentService>();
        var instanceId = Guid.NewGuid();
        _ = await service.AnnounceAsync(new(
            instanceId,
            new AepExtensionIdentity("extension.tools-failure", "Tools failure", "1.0.0"),
            new Uri("https://tools-failure.example/"),
            new Uri("https://tools-failure.example/aep/enrollment/pair")), default);
        using var ownerScope = PushOwnerScope(factory, context, ResourceScopeRef.Instance);
        await service.AssignAsync(context, instanceId, ResourceScopeRef.Instance, default);
        var code = await service.RotateAsync(context, instanceId, default);
        var credential = await service.ClaimAsync(new(instanceId, instanceId, code.Code), default);

        var exception = await Assert.ThrowsAsync<AepEnrollmentException>(() =>
            service.ReadyAsync(new(instanceId, instanceId, credential.CompletionToken), default));

        Assert.AreEqual("resource_provisioning_failed", exception.Code);
        var enrollment = (await service.ListAsync(context, default)).Single(value => value.Definition.InstanceId == instanceId);
        Assert.AreEqual(AepEnrollmentState.VerificationFailed, enrollment.Definition.State);
        Assert.AreEqual("resource_provisioning_failed", enrollment.Definition.Outcome);
    }

    [TestMethod]
    public async Task PairingAutomaticallyMaterializesExactScopedToolsAndRetryPreservesIdentity()
    {
        var handler = new EnrollmentLifecycleHandler("extension.materialization-test", advertiseTool: true);
        await using var factory = EnrollmentFactory().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IHttpClientFactory>();
            services.AddSingleton<IHttpClientFactory>(new EnrollmentHttpClientFactory(handler));
            services.RemoveAll<IToolProviderDiscovery>();
            services.AddSingleton<IToolProviderDiscovery, StaticToolDiscovery>();
        }));
        var context = await GetBootstrapContextAsync(factory);
        var enrollment = factory.Services.GetRequiredService<AepEnrollmentService>();
        var tools = factory.Services.GetRequiredService<ToolManagementService>();
        var instanceId = Guid.NewGuid();
        var target = ResourceScopeRef.Workspace(context.WorkspaceId);
        var registrationName = $"paired-{instanceId:N}";
        _ = await enrollment.AnnounceAsync(new(
            instanceId,
            new AepExtensionIdentity("extension.materialization-test", "Materialization test", "1.0.0"),
            new Uri("https://materialization.example/"),
            new Uri("https://materialization.example/aep/enrollment/pair")), default);
        await enrollment.AssignAsync(context, instanceId, target, default);
        var code = await enrollment.RotateAsync(context, instanceId, default);
        var credential = await enrollment.ClaimAsync(new(instanceId, instanceId, code.Code), default);

        _ = await enrollment.ReadyAsync(new(instanceId, instanceId, credential.CompletionToken), default);

        using var systemScope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushSystem();
        var firstProvider = await tools.GetProviderExactAsync(target, registrationName, ResourceNamespace.Default, default);
        var firstTool = (await tools.ListToolsAsync(default)).Single(value => value.Value.ScopeRef == target
            && value.Value.Definition.Provider?.Name == registrationName);
        Assert.IsNotNull(firstProvider);
        Assert.AreEqual(target, firstProvider.Value.Definition.Aep!.Extension.ScopeRef);
        Assert.AreEqual(registrationName, firstProvider.Value.Definition.Aep.Extension.Name);
        Assert.AreEqual("lookup", firstTool.Value.Definition.ExternalId);
        Assert.IsFalse(firstTool.Value.Definition.Enabled);
        Assert.AreEqual(ResourceOriginKind.Extension, firstProvider.Value.Governance.Origin.Kind);
        Assert.AreEqual("extension.materialization-test", firstProvider.Value.Governance.Origin.Reference);
        Assert.AreEqual("1.0.0", firstProvider.Value.Governance.Origin.Version);
        Assert.AreEqual(ResourceManagementAuthorityKind.ExtensionInstallation, firstProvider.Value.Governance.ManagedBy?.Kind);
        Assert.AreEqual(firstProvider.Value.Governance, firstTool.Value.Governance);

        var registration = await factory.Services.GetRequiredService<ExtensionRegistrationManagementService>()
            .GetExactAsync(target, ResourceNamespace.Default, registrationName, default);
        var manifest = handler.Manifest();
        await factory.Services.GetServices<IAepEnrollmentResourceProvisioner>().Single()
            .ProvisionAsync(registration!.Value, manifest, default);
        var secondProvider = await tools.GetProviderExactAsync(target, registrationName, ResourceNamespace.Default, default);
        var secondTool = (await tools.ListToolsAsync(default)).Single(value => value.Value.ScopeRef == target
            && value.Value.Definition.Provider?.Name == registrationName);

        Assert.AreEqual(firstProvider.Value.Uid, secondProvider!.Value.Uid);
        Assert.AreEqual(firstProvider.Value.Generation, secondProvider.Value.Generation);
        Assert.AreEqual(firstTool.Value.Uid, secondTool.Value.Uid);
        Assert.AreEqual(firstTool.Value.Generation, secondTool.Value.Generation);
    }

    [TestMethod]
    public async Task CredentialRotationPreservesWorkspaceBoundClientIdentity()
    {
        var handler = new EnrollmentLifecycleHandler("extension.pairing-test");
        await using var factory = EnrollmentFactory().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IHttpClientFactory>();
            services.AddSingleton<IHttpClientFactory>(new EnrollmentHttpClientFactory(handler));
        }));
        var context = await GetBootstrapContextAsync(factory);
        var service = factory.Services.GetRequiredService<AepEnrollmentService>();
        var instanceId = Guid.NewGuid();
        _ = await service.AnnounceAsync(new(
            instanceId,
            new AepExtensionIdentity("extension.pairing-test", "Pairing test", "1.0.0"),
            new Uri("https://extension.example/"),
            new Uri("https://extension.example/aep/enrollment/pair")), default);
        await service.AssignAsync(context, instanceId, ResourceScopeRef.Workspace(context.WorkspaceId), default);
        var code = await service.RotateAsync(context, instanceId, default);
        var credential = await service.ClaimAsync(new(instanceId, instanceId, code.Code), default);
        _ = await service.ReadyAsync(new(instanceId, instanceId, credential.CompletionToken), default);

        _ = await service.RotateCredentialAsync(context, instanceId, default);

        Assert.IsNotNull(handler.Rotation);
        Assert.AreEqual(instanceId, handler.Rotation.InstanceId);
        Assert.AreEqual($"agentstration:{ResourceScopeRef.Workspace(context.WorkspaceId).Value}:{instanceId:D}", handler.Rotation.ClientId);
        Assert.AreEqual(1, handler.PreviousRevocationCount);
    }

    [TestMethod]
    public async Task InstancePairingEnrollmentCanBeUnenrolledAndEnrolledAgainWithoutADuplicateRegistration()
    {
        var handler = new EnrollmentLifecycleHandler("extension.reenrollment-test");
        await using var factory = EnrollmentFactory().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IHttpClientFactory>();
            services.AddSingleton<IHttpClientFactory>(new EnrollmentHttpClientFactory(handler));
        }));
        var context = await GetBootstrapContextAsync(factory);
        var service = factory.Services.GetRequiredService<AepEnrollmentService>();
        var registrations = factory.Services.GetRequiredService<ExtensionRegistrationManagementService>();
        var initialTarget = ResourceScopeRef.Instance;
        var reenrollmentTarget = ResourceScopeRef.Workspace(context.WorkspaceId);
        var instanceId = Guid.NewGuid();
        _ = await service.AnnounceAsync(new(
            instanceId,
            new AepExtensionIdentity("extension.reenrollment-test", "Re-enrollment test", "1.0.0"),
            new Uri("https://reenrollment.example/"),
            new Uri("https://reenrollment.example/aep/enrollment/pair")), default);
        AepEnrollmentCredential firstCredential;
        using (PushOwnerScope(factory, context, initialTarget))
        {
            await service.AssignAsync(context, instanceId, initialTarget, default);
            var firstCode = await service.RotateAsync(context, instanceId, default);
            firstCredential = await service.ClaimAsync(new(instanceId, instanceId, firstCode.Code), default);
            _ = await service.ReadyAsync(new(instanceId, instanceId, firstCredential.CompletionToken), default);

            await service.UnenrollAsync(context, instanceId, default);
            await service.UnenrollAsync(context, instanceId, default);
        }

        var unenrolled = (await service.ListAsync(context, default)).Single(value => value.Definition.InstanceId == instanceId);
        Assert.AreEqual(AepEnrollmentState.Unpaired, unenrolled.Definition.State);
        Assert.AreEqual("unenrolled", unenrolled.Definition.Outcome);
        Assert.AreEqual(1, handler.UnenrollmentCount);
        using (factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushSystem())
        {
            var disabled = await registrations.GetExactAsync(initialTarget, ResourceNamespace.Default, $"paired-{instanceId:N}", default);
            Assert.IsNotNull(disabled);
            Assert.IsFalse(disabled.Value.Definition.Enabled);
        }

        AepEnrollmentCredential secondCredential;
        using (PushOwnerScope(factory, context, reenrollmentTarget))
        {
            await service.AssignAsync(context, instanceId, reenrollmentTarget, default);
            var secondCode = await service.RotateAsync(context, instanceId, default);
            secondCredential = await service.ClaimAsync(new(instanceId, instanceId, secondCode.Code), default);
            _ = await service.ReadyAsync(new(instanceId, instanceId, secondCredential.CompletionToken), default);
        }

        Assert.AreNotEqual(firstCredential.AccessToken, secondCredential.AccessToken);
        using (factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushSystem())
        {
            var enabled = await registrations.GetExactAsync(reenrollmentTarget, ResourceNamespace.Default, $"paired-{instanceId:N}", default);
            Assert.IsNotNull(enabled);
            Assert.IsTrue(enabled.Value.Definition.Enabled);
        }
        var inventory = await factory.Services.GetRequiredService<ExtensionInventoryService>().ListAsync(context, default);
        Assert.HasCount(1, inventory.Where(value => value.EnrollmentInstanceId == instanceId).ToArray());
        var audit = await factory.Services.GetRequiredService<ISecurityAuditStore>().ListLatestAsync(100, default);
        Assert.IsTrue(audit.Any(value => value.Action == SecurityAuditActions.AepEnrollmentUnenrolled && value.TargetAccountId == instanceId));
    }

    [TestMethod]
    public async Task SharedKeyAnnouncementProvesPossessionAndCreatesTheRegistration()
    {
        var path = Path.GetTempFileName();
        var sharedKey = new string('s', 43);
        await File.WriteAllTextAsync(path, sharedKey + "\n");
        try
        {
            await using var factory = EnrollmentFactory().WithWebHostBuilder(builder =>
            {
                builder.UseSetting("Agentstration:Extensions:extension.shared-key:EnrollmentMode", "SharedKeyFile");
                builder.UseSetting("Agentstration:Extensions:extension.shared-key:SharedKeyFile:Path", path);
                builder.ConfigureServices(services => services.RemoveAll<IAepEnrollmentResourceProvisioner>());
            });
            var context = await GetBootstrapContextAsync(factory);
            var service = factory.Services.GetRequiredService<AepEnrollmentService>();
            var announcement = new AepEnrollmentAnnouncement(
                Guid.NewGuid(),
                new AepExtensionIdentity("extension.shared-key", "Shared key extension", "1.0.0"),
                new Uri("https://shared-key.example/"),
                null,
                AepEnrollmentMethod.SharedKeyFile);
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var proof = new AepEnrollmentProof(timestamp, AepEnrollmentProofs.Sign(announcement, timestamp, sharedKey));

            var response = await service.AnnounceAsync(announcement, proof, default);

            Assert.AreEqual("pending", response.State);
            var targetScope = ResourceScopeRef.Workspace(context.WorkspaceId);
            await service.AssignAsync(context, announcement.InstanceId, targetScope, default);
            var enrollment = (await service.ListAsync(context, default)).Single(value => value.Definition.InstanceId == announcement.InstanceId);
            Assert.AreEqual(AepEnrollmentState.Available, enrollment.Definition.State);
            Assert.AreEqual(AepEnrollmentMode.SharedKeyFile, enrollment.Definition.EnrollmentMode);
            Assert.AreEqual("extension-shared-key", enrollment.Definition.RegistrationName);
            using var systemScope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushSystem();
            var registration = await factory.Services.GetRequiredService<ExtensionRegistrationManagementService>().GetExactAsync(
                targetScope,
                ResourceNamespace.Default,
                "extension-shared-key",
                default);
            Assert.IsNotNull(registration);
            Assert.AreEqual(announcement.Endpoint, registration.Value.Definition.Endpoint);
            Assert.AreEqual(AepEnrollmentMode.SharedKeyFile, registration.Value.Definition.EnrollmentMode);

            await service.UnenrollAsync(context, announcement.InstanceId, default);
            await service.UnenrollAsync(context, announcement.InstanceId, default);
            var unenrolled = (await service.ListAsync(context, default)).Single(value => value.Definition.InstanceId == announcement.InstanceId);
            Assert.AreEqual(AepEnrollmentState.Unpaired, unenrolled.Definition.State);
            var disabled = await factory.Services.GetRequiredService<ExtensionRegistrationManagementService>().GetExactAsync(
                targetScope,
                ResourceNamespace.Default,
                "extension-shared-key",
                default);
            Assert.IsNotNull(disabled);
            Assert.IsFalse(disabled.Value.Definition.Enabled);

            await service.AssignAsync(context, announcement.InstanceId, targetScope, default);
            var reenrolled = (await service.ListAsync(context, default)).Single(value => value.Definition.InstanceId == announcement.InstanceId);
            Assert.AreEqual(AepEnrollmentState.Available, reenrolled.Definition.State);
            var enabled = await factory.Services.GetRequiredService<ExtensionRegistrationManagementService>().GetExactAsync(
                targetScope,
                ResourceNamespace.Default,
                "extension-shared-key",
                default);
            Assert.IsNotNull(enabled);
            Assert.IsTrue(enabled.Value.Definition.Enabled);
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public async Task SharedKeyAnnouncementRejectsAnInvalidProofBeforeRegistration()
    {
        var path = Path.GetTempFileName();
        await File.WriteAllTextAsync(path, new string('s', 43) + "\n");
        try
        {
            await using var factory = EnrollmentFactory().WithWebHostBuilder(builder =>
            {
                builder.UseSetting("Agentstration:Extensions:extension.invalid-proof:EnrollmentMode", "SharedKeyFile");
                builder.UseSetting("Agentstration:Extensions:extension.invalid-proof:SharedKeyFile:Path", path);
            });
            var context = await GetBootstrapContextAsync(factory);
            var announcement = new AepEnrollmentAnnouncement(
                Guid.NewGuid(),
                new AepExtensionIdentity("extension.invalid-proof", "Invalid proof", "1.0.0"),
                new Uri("https://invalid-proof.example/"),
                null,
                AepEnrollmentMethod.SharedKeyFile);

            var exception = await Assert.ThrowsAsync<AepEnrollmentException>(() => factory.Services
                .GetRequiredService<AepEnrollmentService>()
                .AnnounceAsync(announcement, new AepEnrollmentProof(DateTimeOffset.UtcNow.ToUnixTimeSeconds(), "invalid"), default));

            Assert.AreEqual("shared_key_proof_invalid", exception.Code);
            using var systemScope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushSystem();
            var registration = await factory.Services.GetRequiredService<ExtensionRegistrationManagementService>().GetExactAsync(
                ResourceScopeRef.Instance,
                ResourceNamespace.Default,
                "extension-invalid-proof",
                default);
            Assert.IsNull(registration);
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public async Task ExtensionInventoryReplacesAnnouncementWithThePairedRegistration()
    {
        var handler = new EnrollmentLifecycleHandler("extension.inventory-test");
        await using var factory = EnrollmentFactory().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Agentstration:Extensions:extension.inventory-test:Endpoint", "https://extension.example/");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHttpClientFactory>();
                services.AddSingleton<IHttpClientFactory>(new EnrollmentHttpClientFactory(handler));
            });
        });
        var context = await GetBootstrapContextAsync(factory);
        var enrollment = factory.Services.GetRequiredService<AepEnrollmentService>();
        var instanceId = Guid.NewGuid();
        _ = await enrollment.AnnounceAsync(new(
            instanceId,
            new AepExtensionIdentity("extension.inventory-test", "Inventory test", "1.0.0"),
            new Uri("https://extension.example/"),
            new Uri("https://extension.example/aep/enrollment/pair")), default);

        var before = await factory.Services.GetRequiredService<ExtensionInventoryService>().ListAsync(context, default);
        var announced = before.Where(value => value.Endpoint == new Uri("https://extension.example/")).ToArray();
        Assert.HasCount(1, announced);
        Assert.AreEqual(instanceId, announced[0].EnrollmentInstanceId);
        Assert.IsNotNull(announced[0].RegistrationName);

        await enrollment.AssignAsync(context, instanceId, ResourceScopeRef.Workspace(context.WorkspaceId), default);
        var code = await enrollment.RotateAsync(context, instanceId, default);
        var credential = await enrollment.ClaimAsync(new(instanceId, instanceId, code.Code), default);
        _ = await enrollment.ReadyAsync(new(instanceId, instanceId, credential.CompletionToken), default);

        var after = await factory.Services.GetRequiredService<ExtensionInventoryService>().ListAsync(context, default);
        var paired = after.Where(value => value.Endpoint == new Uri("https://extension.example/")).ToArray();
        Assert.HasCount(1, paired);
        Assert.IsNotNull(paired[0].RegistrationName);
        Assert.AreEqual(AepEnrollmentState.Available, paired[0].EnrollmentStatus);
        Assert.AreEqual("unavailable", paired[0].AvailabilityStatus);
        Assert.HasCount(1, paired[0].Connections);
        Assert.StartsWith("paired-", paired[0].Connections[0].RegistrationName, StringComparison.Ordinal);
    }

    private sealed class EnrollmentHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static IDisposable PushOwnerScope(
        WebApplicationFactory<Program> factory,
        RequestContext context,
        ResourceScopeRef target)
    {
        var scopes = factory.Services.GetRequiredService<IRequestContextScopeFactory>();
        return target.Kind switch
        {
            ResourceScopeKind.Instance => scopes.PushInstance(context),
            ResourceScopeKind.Tenant => scopes.PushTenant(context.PrincipalId, context.TenantId),
            ResourceScopeKind.Workspace => scopes.Push(context),
            _ => throw new InvalidOperationException($"Unsupported resource scope '{target}'.")
        };
    }

    private sealed class AllowEnrollmentPlatformAdministrator : IPlatformAuthorizationService
    {
        public Task<bool> IsPlatformAdministratorAsync(Guid principalId, CancellationToken cancellationToken) =>
            Task.FromResult(true);
    }

    private sealed class DenyEnrollmentPlatformAdministrator : IPlatformAuthorizationService
    {
        public Task<bool> IsPlatformAdministratorAsync(Guid principalId, CancellationToken cancellationToken) =>
            Task.FromResult(false);
    }

    private sealed class RecordingResourceProvisioner : IAepEnrollmentResourceProvisioner
    {
        public ExtensionRegistrationResource? Registration { get; private set; }
        public AepManifest? Manifest { get; private set; }
        public Exception? Failure { get; init; }

        public Task ProvisionAsync(
            ExtensionRegistrationResource registration,
            AepManifest? authenticatedManifest,
            CancellationToken cancellationToken)
        {
            Registration = registration;
            Manifest = authenticatedManifest;
            return Failure is null ? Task.CompletedTask : Task.FromException(Failure);
        }
    }

    private sealed class StaticToolDiscovery : IToolProviderDiscovery
    {
        public bool Supports(ToolProviderType providerType) => providerType == ToolProviderType.Aep;

        public Task<ToolProviderDiscoveryResult> DiscoverAsync(
            ToolProviderResource provider,
            CancellationToken cancellationToken) => Task.FromResult(new ToolProviderDiscoveryResult(
                [new DiscoveredToolDescriptor(
                    "lookup",
                    "Lookup",
                    "Looks up a value.",
                    JsonSerializer.SerializeToElement(new { type = "object" }),
                    null,
                    new Dictionary<string, JsonElement>())],
                new Dictionary<string, bool> { ["tools"] = true },
                new Dictionary<string, string>()));
    }

    private sealed class EnrollmentLifecycleHandler(string extensionId, bool advertiseTool = false) : HttpMessageHandler
    {
        public AepCredentialRotation? Rotation { get; private set; }
        public int PreviousRevocationCount { get; private set; }
        public int UnenrollmentCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath == AepEnrollmentProtocol.CredentialRotationPath)
                Rotation = await request.Content!.ReadFromJsonAsync<AepCredentialRotation>(AepProtocol.JsonOptions, cancellationToken);
            else if (request.RequestUri?.AbsolutePath == AepEnrollmentProtocol.PreviousCredentialRevocationPath)
                PreviousRevocationCount++;
            else if (request.RequestUri?.AbsolutePath == AepEnrollmentProtocol.UnenrollmentPath)
                UnenrollmentCount++;

            if (request.RequestUri?.AbsolutePath != AepProtocol.DiscoveryPath)
                return new(HttpStatusCode.OK);
            var manifest = Manifest();
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(manifest, options: AepProtocol.JsonOptions) };
        }

        public AepManifest Manifest()
        {
            var tools = advertiseTool
                ? new[] { new AepToolContribution("lookup", "Lookup", new AepMcpToolMapping("main", "lookup")) }
                : [];
            return new AepManifest(
                AepProtocol.Version,
                new(extensionId, "Pairing test", "1.0.0"),
                new Dictionary<string, AepCapabilityDescriptor>(),
                new AepContributions([], tools),
                advertiseTool ? new AepMcpDescriptor([new AepMcpServerDescriptor("main", "/mcp")]) : null);
        }
    }
}
