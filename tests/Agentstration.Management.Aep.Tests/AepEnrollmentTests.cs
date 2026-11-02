extern alias crawl4ai;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.Aep.Abstractions;
using Agentstration.DataSources.Contracts;
using Agentstration.Extensions;
using Agentstration.Extensions.Aep;
using Agentstration.Extensions.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.Flows;
using Agentstration.Flows.Application;
using Agentstration.Infrastructure.Bootstrap;
using Agentstration.Infrastructure.Declarative;
using Agentstration.Infrastructure.Tools;
using Agentstration.ModelProviders;
using Agentstration.ResourceManagement;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.Security.Contracts;
using Agentstration.Tools;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Crawl4AiDataSourceProfileBundle = crawl4ai::Agentstration.Extensions.Crawl4AI.Crawl4AiDataSourceProfileBundle;

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
        await factory.Services.GetServices<IAepEnrollmentResourceProvisioner>().OfType<AepEnrollmentToolProvisioner>().Single()
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
    [DataRow(ResourceScopeKind.Instance, false, false, "none")]
    [DataRow(ResourceScopeKind.Tenant, false, false, "none")]
    [DataRow(ResourceScopeKind.Workspace, false, false, "none")]
    [DataRow(ResourceScopeKind.Workspace, true, false, "none")]
    [DataRow(ResourceScopeKind.Workspace, true, true, "none")]
    [DataRow(ResourceScopeKind.Workspace, false, false, "definition")]
    [DataRow(ResourceScopeKind.Workspace, false, false, "generation")]
    [DataRow(ResourceScopeKind.Workspace, false, false, "version")]
    [DataRow(ResourceScopeKind.Workspace, false, false, "missing")]
    [DataRow(ResourceScopeKind.Workspace, false, false, "owner")]
    [DataRow(ResourceScopeKind.Workspace, false, false, "published")]
    [DataRow(ResourceScopeKind.Workspace, false, false, "manifest")]
    [DataRow(ResourceScopeKind.Workspace, false, false, "flow")]
    [DataRow(ResourceScopeKind.Workspace, false, false, "unrecorded")]
    public async Task PairingInstallsAdvertisedBundleAndPersistsGovernedInventory(
        ResourceScopeKind scopeKind, bool conflictOnFirstApply, bool interruptPublication, string drift)
    {
        var handler = new EnrollmentLifecycleHandler("Agentstration.Extensions.Crawl4AI", advertiseBundle: true);
        var conflict = new BundleApplyConflict { Pending = conflictOnFirstApply, InterruptPublication = interruptPublication };
        await using var factory = EnrollmentFactory().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IHttpClientFactory>();
            services.AddSingleton<IHttpClientFactory>(new EnrollmentHttpClientFactory(handler));
            services.RemoveAll<IHttpMessageHandlerFactory>();
            services.AddSingleton<IHttpMessageHandlerFactory>(new EnrollmentHttpClientFactory(handler));
            services.RemoveAll<IToolProviderDiscovery>();
            services.AddSingleton<IToolProviderDiscovery, CrawlToolDiscovery>();
            var flowHandler = services.Single(value => value.ServiceType == typeof(IBootstrapResourceHandler)
                && value.ImplementationType == typeof(FlowBootstrapResourceHandler));
            services.Remove(flowHandler);
            services.AddScoped<IBootstrapResourceHandler>(provider => new ConflictingFlowHandler(
                ActivatorUtilities.CreateInstance<FlowBootstrapResourceHandler>(provider),
                provider.GetRequiredService<FlowService>(), conflict));
        }));
        var context = await GetBootstrapContextAsync(factory);
        var enrollment = factory.Services.GetRequiredService<AepEnrollmentService>();
        var store = factory.Services.GetRequiredService<IResourceStore>();
        var instanceId = Guid.NewGuid();
        var target = scopeKind switch
        {
            ResourceScopeKind.Instance => ResourceScopeRef.Instance,
            ResourceScopeKind.Tenant => ResourceScopeRef.Tenant(context.TenantId),
            _ => ResourceScopeRef.Workspace(context.WorkspaceId)
        };
        _ = await enrollment.AnnounceAsync(new(
            instanceId,
            new AepExtensionIdentity("Agentstration.Extensions.Crawl4AI", "Crawl4AI", "1.0.0"),
            new Uri("https://crawl4ai.example/"),
            new Uri("https://crawl4ai.example/aep/enrollment/pair")), default);
        using var ownerScope = PushOwnerScope(factory, context, target);
        await enrollment.AssignAsync(context, instanceId, target, default);
        var code = await enrollment.RotateAsync(context, instanceId, default);
        var credential = await enrollment.ClaimAsync(new(instanceId, instanceId, code.Code), default);

        StoredResource<ExtensionBundleInstallationResource>? failedInstallation = null;
        StoredResource<ToolSetResource>? partialToolSet = null;
        if (conflictOnFirstApply)
        {
            var failure = await Assert.ThrowsExactlyAsync<AepEnrollmentException>(() =>
                enrollment.ReadyAsync(new(instanceId, instanceId, credential.CompletionToken), default));
            Assert.AreEqual("resource_provisioning_failed", failure.Code);
            Assert.AreEqual(AepEnrollmentState.VerificationFailed,
                (await enrollment.ListAsync(context, default)).Single().Definition.State);
            using (factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushSystem())
            {
                failedInstallation = (await store.ListExactAsync<ExtensionBundleInstallationResource>(target,
                    ExtensionKinds.ExtensionBundleInstallation, 0, 10, default)).Single();
                Assert.AreEqual(ExtensionBundleInstallationState.Failed, failedInstallation.Value.Definition.State);
                StringAssert.Contains(failedInstallation.Value.Definition.Diagnostic!, "Conflict");
                Assert.IsTrue(failedInstallation.Value.Definition.ManagedResources.Any(resource => resource.Kind == ToolResourceKinds.ToolSet));
                Assert.IsTrue(failedInstallation.Value.Definition.ManagedResources.Any(resource => resource.Published));
                if (interruptPublication)
                {
                    Assert.IsTrue(failedInstallation.Value.Definition.ManagedResources.Any(resource => resource.Kind == FlowResourceKinds.Flow));
                    Assert.IsFalse(failedInstallation.Value.Definition.ManagedResources.Any(resource => resource.Kind == "FlowVersion"));
                }
                partialToolSet = (await store.ListExactAsync<ToolSetResource>(target,
                    ToolResourceKinds.ToolSet, 0, 10, default)).Single();
            }
            code = await enrollment.RotateAsync(context, instanceId, default);
            credential = await enrollment.ClaimAsync(new(instanceId, instanceId, code.Code), default);
        }

        try
        {
            _ = await enrollment.ReadyAsync(new(instanceId, instanceId, credential.CompletionToken), default);
        }
        catch (AepEnrollmentException)
        {
            using var systemScope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushSystem();
            var failed = await store.ListExactAsync<ExtensionBundleInstallationResource>(target,
                ExtensionKinds.ExtensionBundleInstallation, 0, 10, default);
            Assert.Fail(failed.SingleOrDefault()?.Value.Definition.Diagnostic ?? "Bundle installation did not create a diagnostic record.");
        }

        using var readScope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushSystem();
        var installations = await store.ListExactAsync<ExtensionBundleInstallationResource>(target,
            ExtensionKinds.ExtensionBundleInstallation, 0, 10, default);
        var installation = installations.Single().Value;
        Assert.AreEqual(ExtensionBundleInstallationState.Installed, installation.Definition.State);
        Assert.AreEqual("crawl4ai-web", installation.Definition.BundleId);
        Assert.HasCount(3, installation.Definition.ToolBindings);
        Assert.IsTrue(installation.Definition.ManagedResources.Any(value =>
            value.Kind == DataSourceResourceKinds.DataSourceProfile && value.Name == "crawl4ai-web"));
        Assert.IsTrue(installation.Definition.ManagedResources.All(value => value.ScopeRef == target));
        Assert.IsTrue(installation.Definition.ManagedResources.All(value => value.DefinitionHash is not null));
        var registrationName = $"paired-{instanceId:N}";
        var extensionView = (await factory.Services.GetRequiredService<ExtensionManagementService>()
            .ListAsync(default)).Single(value => value.RegistrationName == registrationName);
        Assert.HasCount(1, extensionView.BundleInstallations);
        Assert.AreEqual("unavailable", extensionView.Status);
        Assert.AreEqual(ExtensionBundleInstallationState.Installed,
            extensionView.BundleInstallations.Single().Definition.State);

        var toolSetEvidence = installation.Definition.ManagedResources.Single(value =>
            value.Kind == ToolResourceKinds.ToolSet && !value.Published);
        var toolSetAddress = ScopedResourceAddress.Create(target, toolSetEvidence.Namespace,
            toolSetEvidence.Kind, toolSetEvidence.Name);
        var toolSet = await store.GetExactAsync<ToolSetResource>(toolSetAddress, default);
        Assert.IsNotNull(toolSet);
        if (failedInstallation is not null)
        {
            Assert.AreEqual(failedInstallation.Value.Uid, installation.Uid);
            Assert.AreEqual(partialToolSet!.Value.Uid, toolSet.Value.Uid);
            Assert.AreEqual(partialToolSet.Value.Generation, toolSet.Value.Generation);
        }
        var registration = await store.GetExactAsync<ExtensionRegistrationResource>(ScopedResourceAddress.Create(
            target, ResourceNamespace.Default, ExtensionKinds.ExtensionRegistration, registrationName), default);
        Assert.IsNotNull(registration);
        await factory.Services.GetServices<IAepEnrollmentResourceProvisioner>()
            .OfType<AepEnrollmentBundleProvisioner>().Single()
            .ProvisionAsync(registration.Value, handler.Manifest(), default);
        var repeated = (await store.ListExactAsync<ExtensionBundleInstallationResource>(target,
            ExtensionKinds.ExtensionBundleInstallation, 0, 10, default)).Single();
        Assert.AreEqual(installations.Single().ETag, repeated.ETag);
        Assert.AreEqual(toolSet.ETag, (await store.GetExactAsync<ToolSetResource>(toolSetAddress, default))!.ETag);
        var mutation = await Assert.ThrowsExactlyAsync<ResourceConcurrencyException>(() =>
            store.PutExactAsync(target, toolSet.Value with
            {
                Definition = toolSet.Value.Definition with { DisplayName = "Forged" }
            }, toolSet.ETag, false, default));
        StringAssert.StartsWith(mutation.Message, "resource_managed:");
        var publication = await Assert.ThrowsExactlyAsync<ToolSetValidationException>(() =>
            factory.Services.GetRequiredService<ToolSetService>().PublishExactAsync(target, toolSet.Value.Namespace,
                toolSet.Value.Name, toolSet.Value.Definition.Version, default));
        Assert.AreEqual("resource_managed", publication.Code);
        var profileService = factory.Services.GetRequiredService<Agentstration.DataSources.DataSourceProfileService>();
        var protectedProfile = await Assert.ThrowsExactlyAsync<Agentstration.DataSources.DataSourceValidationException>(() =>
            profileService.PublishAsync(default, "crawl4ai-web", target, new("1.0.0", true), default));
        Assert.AreEqual("resource_managed", protectedProfile.Code);
        var flowService = factory.Services.GetRequiredService<FlowService>();
        var protectedFlow = await Assert.ThrowsExactlyAsync<ResourceGovernanceViolationException>(() =>
            flowService.PublishVersionExactAsync(target, new("crawl4ai-web-acquisition"), "1.0.0", true, default));
        Assert.AreEqual("resource_managed", protectedFlow.Code);

        if (drift != "none")
            await AssertBundleDriftRejectedAsync(factory, target, registration.Value, handler.Manifest(),
                installation, toolSet, drift);
        else if (scopeKind == ResourceScopeKind.Workspace && !conflictOnFirstApply)
        {
            await enrollment.UnenrollAsync(context, instanceId, default);
            var offline = (await factory.Services.GetRequiredService<ExtensionManagementService>().ListAsync(default))
                .Single(value => value.RegistrationName == registrationName);
            Assert.HasCount(1, offline.BundleInstallations);
            var afterUnenrollment = await Assert.ThrowsExactlyAsync<ToolSetValidationException>(() =>
                factory.Services.GetRequiredService<ToolSetService>().PublishExactAsync(target,
                    toolSet.Value.Namespace, toolSet.Value.Name, toolSet.Value.Definition.Version, default));
            Assert.AreEqual("resource_managed", afterUnenrollment.Code);
        }
    }

    private static async Task AssertBundleDriftRejectedAsync(WebApplicationFactory<Program> factory,
        ResourceScopeRef scope, ExtensionRegistrationResource registration, AepManifest manifest,
        ExtensionBundleInstallationResource installation, StoredResource<ToolSetResource> original, string drift)
    {
        var store = factory.Services.GetRequiredService<IResourceStore>();
        var address = ScopedResourceAddress.Create(scope, original.Value.Namespace, original.Value.Kind, original.Value.Name);
        var authority = factory.Services.GetRequiredService<IResourceGovernanceContext>();
        using (authority.Push(installation.Governance))
        {
            if (drift == "unrecorded")
            {
                var record = (await store.ListExactAsync<ExtensionBundleInstallationResource>(scope,
                    ExtensionKinds.ExtensionBundleInstallation, 0, 10, default)).Single();
                installation = record.Value with { Definition = record.Value.Definition with
                { State = ExtensionBundleInstallationState.Installing, ManagedResources = [] } };
                _ = await store.PutExactAsync(scope, installation, record.ETag, false, default);
            }
            if (drift is "missing" or "owner") await store.DeleteExactAsync(address, original.ETag, default);
            else if (drift == "published")
            {
                var published = (await store.ListExactAsync<ToolSetVersionResource>(scope,
                    ToolResourceKinds.ToolSetVersion, 0, 10, default)).Single();
                await store.DeleteExactAsync(ScopedResourceAddress.Create(scope, published.Value.Namespace,
                    published.Value.Kind, published.Value.Name), published.ETag, default);
                _ = await store.CreateImmutableAsync(published.Value with { DefinitionHash = "changed" }, default);
            }
            else if (drift == "flow")
            {
                var flows = factory.Services.GetRequiredService<FlowService>();
                var flow = (await flows.GetExactAsync(scope, new("crawl4ai-web-acquisition"), default))!;
                _ = await flows.UpdateExactAsync(scope, flow.Value.Id, new(flow.Value.Description, flow.Value.Version,
                    !flow.Value.Enabled, flow.Value.Definition, flow.Value.Metadata, flow.Value.Graph, flow.Value.DisplayName),
                    flow.ETag, default);
            }
            else if (drift != "manifest")
            {
                var changed = drift switch
                {
                    "definition" or "unrecorded" => original.Value with { Definition = original.Value.Definition with { DisplayName = "Changed" } },
                    "version" => original.Value with { Definition = original.Value.Definition with { Version = "2.0.0" } },
                    _ => original.Value with { Generation = original.Value.Generation + 1 }
                };
                _ = await store.PutExactAsync(scope, changed, original.ETag, false, default);
            }
        }
        if (drift == "owner")
        {
            using var other = authority.Push(ResourceGovernance.Extension("other-installation", "other.extension", "1.0.0"));
            _ = await store.PutExactAsync(scope, original.Value with { Governance = ResourceGovernance.User }, null, true, default);
        }
        if (drift == "manifest")
        {
            var bundle = manifest.Contributions.DataSourceProfileBundles!.Single();
            manifest = manifest with { Contributions = manifest.Contributions with
            {
                DataSourceProfileBundles = [bundle with { ResourceManifests = bundle.ResourceManifests
                    .Select(value => value.Replace("Crawl4AI Web acquisition", "Changed acquisition", StringComparison.Ordinal)).ToArray() }]
            } };
        }
        var conflict = await Assert.ThrowsExactlyAsync<AepBundleConflictException>(() =>
            factory.Services.GetServices<IAepEnrollmentResourceProvisioner>().OfType<AepEnrollmentBundleProvisioner>()
                .Single().ProvisionAsync(registration, manifest, default));
        Assert.AreEqual(drift switch
        {
            "owner" => "extension_bundle_owner_conflict",
            "manifest" => "extension_bundle_installation_conflict",
            _ => "extension_bundle_resource_drift"
        }, conflict.Code);
        var retained = (await store.ListExactAsync<ExtensionBundleInstallationResource>(scope,
            ExtensionKinds.ExtensionBundleInstallation, 0, 10, default)).Single().Value;
        Assert.AreEqual(ExtensionBundleInstallationState.Degraded, retained.Definition.State);
        CollectionAssert.AreEqual(installation.Definition.ManagedResources.ToArray(), retained.Definition.ManagedResources.ToArray());
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
    public async Task ExtensionInventoryPreservesDistinctScopesAndLinksThePairedRegistration()
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
        Assert.HasCount(2, announced);
        Assert.HasCount(1, announced.Where(value => value.RegistrationName is not null));
        Assert.IsNull(announced.Single(value => value.EnrollmentInstanceId == instanceId).RegistrationName);

        await enrollment.AssignAsync(context, instanceId, ResourceScopeRef.Workspace(context.WorkspaceId), default);
        var code = await enrollment.RotateAsync(context, instanceId, default);
        var credential = await enrollment.ClaimAsync(new(instanceId, instanceId, code.Code), default);
        _ = await enrollment.ReadyAsync(new(instanceId, instanceId, credential.CompletionToken), default);

        var after = await factory.Services.GetRequiredService<ExtensionInventoryService>().ListAsync(context, default);
        var paired = after.Where(value => value.Endpoint == new Uri("https://extension.example/")).ToArray();
        Assert.HasCount(2, paired);
        var enrolled = paired.Single(value => value.EnrollmentInstanceId == instanceId);
        Assert.AreEqual(ResourceScopeRef.Workspace(context.WorkspaceId), enrolled.RegistrationScopeRef);
        Assert.IsNotNull(enrolled.RegistrationName);
        Assert.AreEqual(AepEnrollmentState.Available, enrolled.EnrollmentStatus);
        Assert.AreEqual("unavailable", enrolled.AvailabilityStatus);
        Assert.HasCount(1, enrolled.Connections);
        Assert.StartsWith("paired-", enrolled.Connections[0].RegistrationName, StringComparison.Ordinal);
        Assert.AreNotEqual(enrolled.RegistrationScopeRef, paired.Single(value => value.EnrollmentInstanceId is null).RegistrationScopeRef);
    }

    private sealed class EnrollmentHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory, IHttpMessageHandlerFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
        public HttpMessageHandler CreateHandler(string name) => handler;
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

    private sealed class CrawlToolDiscovery : IToolProviderDiscovery
    {
        public bool Supports(ToolProviderType providerType) => providerType == ToolProviderType.Aep;

        public Task<ToolProviderDiscoveryResult> DiscoverAsync(
            ToolProviderResource provider,
            CancellationToken cancellationToken) => Task.FromResult(new ToolProviderDiscoveryResult(
                new[] { "web.fetch", "web.crawl", "content.extract", "content.read", "content.delete" }
                    .Select(id => new DiscoveredToolDescriptor(
                        id,
                        id,
                        id,
                        JsonSerializer.SerializeToElement(new { type = "object" }),
                        null,
                        new Dictionary<string, JsonElement>()))
                    .ToArray(),
                new Dictionary<string, bool> { ["tools"] = true },
                new Dictionary<string, string>()));
    }

    private sealed class BundleApplyConflict
    {
        public bool Pending { get; set; }
        public bool InterruptPublication { get; init; }
    }

    private sealed class ConflictingFlowHandler(
        FlowBootstrapResourceHandler inner,
        FlowService flows,
        BundleApplyConflict conflict) : IBootstrapResourceHandler
    {
        public string Kind => inner.Kind;
        public BootstrapProfileScope Scope => inner.Scope;
        public bool SupportsProfileScope(BootstrapProfileScope profileScope) => inner.SupportsProfileScope(profileScope);

        public Task<BootstrapResourcePlanResult> PlanAsync(
            BootstrapResourceDocument resource,
            BootstrapResourceOperationContext operation,
            BootstrapPlanningContext planning,
            CancellationToken cancellationToken) => inner.PlanAsync(resource, operation, planning, cancellationToken);

        public async Task<BootstrapResourceApplyResult> ApplyAsync(
            BootstrapResourceDocument resource,
            BootstrapResourceOperationContext operation,
            CancellationToken cancellationToken)
        {
            if (!conflict.Pending) return await inner.ApplyAsync(resource, operation, cancellationToken);
            conflict.Pending = false;
            if (conflict.InterruptPublication)
            {
                var value = ResourceManifestSerializer.FromJson<DeclarativeResourceEnvelope<DeclarativeFlowDefinition>>(
                    JsonSerializer.Serialize(resource, JsonSerializerOptions.Web));
                var definition = value.Definition;
                _ = await flows.CreateExactAsync(ResourceScopeRef.Workspace(operation.Target!.WorkspaceId!.Value),
                    new(value.Metadata.Name, definition.Description, definition.Version, definition.Enabled,
                        definition.Spec, definition.Metadata, definition.Graph, definition.DisplayName),
                    value.Metadata.Namespace, cancellationToken);
            }
            return BootstrapResourceApplyResult.Conflict;
        }
    }

    private sealed class EnrollmentLifecycleHandler(
        string extensionId,
        bool advertiseTool = false,
        bool advertiseBundle = false) : HttpMessageHandler
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
            var tools = advertiseBundle
                ? new[]
                {
                    new AepToolContribution("web.fetch", "Fetch", new AepMcpToolMapping("main", "web_fetch")),
                    new AepToolContribution("web.crawl", "Crawl", new AepMcpToolMapping("main", "web_crawl")),
                    new AepToolContribution("content.extract", "Extract", new AepMcpToolMapping("main", "content_extract")),
                    new AepToolContribution("content.read", "Read", new AepMcpToolMapping("main", "content_read")),
                    new AepToolContribution("content.delete", "Delete", new AepMcpToolMapping("main", "content_delete"))
                }
                : advertiseTool
                ? new[] { new AepToolContribution("lookup", "Lookup", new AepMcpToolMapping("main", "lookup")) }
                : [];
            return new AepManifest(
                AepProtocol.Version,
                new(extensionId, "Pairing test", "1.0.0"),
                new Dictionary<string, AepCapabilityDescriptor>(),
                new AepContributions([], tools, DataSourceProfileBundles: advertiseBundle ? [Crawl4AiDataSourceProfileBundle.Create()] : []),
                advertiseTool || advertiseBundle ? new AepMcpDescriptor([new AepMcpServerDescriptor("main", "/mcp")]) : null);
        }
    }
}
