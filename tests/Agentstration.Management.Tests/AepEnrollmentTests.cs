using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.Aep.Abstractions;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agentstration.Management.Tests;

public sealed partial class ModelManagementApiTests
{
    [TestMethod]
    public async Task PairingCodeCanBeDisabledAndConfigurationCanLockTheMode()
    {
        await using (var factory = Factory())
        {
            var context = await GetBootstrapContextAsync(factory);
            var settings = factory.Services.GetRequiredService<AepEnrollmentSettingsService>();
            var updated = await settings.UpdateAsync(context, pairingCodeEnabled: false, sharedKeyFileEnabled: true, ifMatch: null, default);
            Assert.IsFalse(updated.PairingCodeEnabled);
            Assert.IsTrue(updated.PairingCodeConfigurable);

            var service = factory.Services.GetRequiredService<AepEnrollmentService>();
            var rejected = await Assert.ThrowsAsync<AepEnrollmentException>(() => service.AnnounceAsync(new(
                Guid.NewGuid(),
                context.TenantId,
                context.WorkspaceId,
                new AepExtensionIdentity("extension.disabled-pairing", "Disabled pairing", "1.0.0"),
                new Uri("https://extension.example/"),
                new Uri("https://extension.example/aep/enrollment/pair")), default));
            Assert.AreEqual("enrollment_mode_disabled", rejected.Code);
        }

        await using var lockedFactory = Factory().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
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
        await using var factory = Factory();
        var context = await GetBootstrapContextAsync(factory);
        var service = factory.Services.GetRequiredService<AepEnrollmentService>();
        var instanceId = Guid.NewGuid();
        var announcement = new AepEnrollmentAnnouncement(
            instanceId,
            context.TenantId,
            context.WorkspaceId,
            new AepExtensionIdentity("extension.pairing-test", "Pairing test", "1.0.0"),
            new Uri("https://extension.example/"),
            new Uri("https://extension.example/aep/enrollment/pair"));

        _ = await service.AnnounceAsync(announcement, default);
        _ = await service.AnnounceAsync(announcement, default);
        Assert.HasCount(1, await service.ListAsync(context, default));

        var first = await service.RotateAsync(context, instanceId, default);
        var second = await service.RotateAsync(context, instanceId, default);
        var stale = await Assert.ThrowsAsync<AepEnrollmentException>(() => service.ClaimAsync(
            new(instanceId, instanceId, context.WorkspaceId, first.Code), default));
        Assert.AreEqual("code_invalid", stale.Code);

        async Task<bool> ClaimAsync()
        {
            try
            {
                _ = await service.ClaimAsync(new(instanceId, instanceId, context.WorkspaceId, second.Code), default);
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
    public async Task PairingAnnouncementRejectsCrossOriginForm()
    {
        await using var factory = Factory();
        var context = await GetBootstrapContextAsync(factory);
        var service = factory.Services.GetRequiredService<AepEnrollmentService>();
        var instanceId = Guid.NewGuid();

        var exception = await Assert.ThrowsAsync<AepEnrollmentException>(() => service.AnnounceAsync(new(
            instanceId,
            context.TenantId,
            context.WorkspaceId,
            new AepExtensionIdentity("extension.pairing-test", "Pairing test", "1.0.0"),
            new Uri("https://extension.example/"),
            new Uri("https://attacker.example/aep/enrollment/pair")), default));

        Assert.AreEqual("pairing_origin_mismatch", exception.Code);
    }

    [TestMethod]
    public async Task CredentialRotationPreservesWorkspaceBoundClientIdentity()
    {
        var handler = new EnrollmentLifecycleHandler("extension.pairing-test");
        await using var factory = Factory().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IHttpClientFactory>();
            services.AddSingleton<IHttpClientFactory>(new EnrollmentHttpClientFactory(handler));
        }));
        var context = await GetBootstrapContextAsync(factory);
        var service = factory.Services.GetRequiredService<AepEnrollmentService>();
        var instanceId = Guid.NewGuid();
        _ = await service.AnnounceAsync(new(
            instanceId,
            context.TenantId,
            context.WorkspaceId,
            new AepExtensionIdentity("extension.pairing-test", "Pairing test", "1.0.0"),
            new Uri("https://extension.example/"),
            new Uri("https://extension.example/aep/enrollment/pair")), default);
        var code = await service.RotateAsync(context, instanceId, default);
        var credential = await service.ClaimAsync(new(instanceId, instanceId, context.WorkspaceId, code.Code), default);
        _ = await service.ReadyAsync(new(instanceId, instanceId, context.WorkspaceId, credential.CompletionToken), default);

        _ = await service.RotateCredentialAsync(context, instanceId, default);

        Assert.IsNotNull(handler.Rotation);
        Assert.AreEqual(instanceId, handler.Rotation.InstanceId);
        Assert.AreEqual($"agentstration:{context.WorkspaceId:D}:{instanceId:D}", handler.Rotation.ClientId);
        Assert.AreEqual(1, handler.PreviousRevocationCount);
    }

    private sealed class EnrollmentHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class EnrollmentLifecycleHandler(string extensionId) : HttpMessageHandler
    {
        public AepCredentialRotation? Rotation { get; private set; }
        public int PreviousRevocationCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath == AepEnrollmentProtocol.CredentialRotationPath)
                Rotation = await request.Content!.ReadFromJsonAsync<AepCredentialRotation>(AepProtocol.JsonOptions, cancellationToken);
            else if (request.RequestUri?.AbsolutePath == AepEnrollmentProtocol.PreviousCredentialRevocationPath)
                PreviousRevocationCount++;

            if (request.RequestUri?.AbsolutePath != AepProtocol.DiscoveryPath)
                return new(HttpStatusCode.OK);
            var manifest = new AepManifest(
                AepProtocol.Version,
                new(extensionId, "Pairing test", "1.0.0"),
                new Dictionary<string, AepCapabilityDescriptor>(),
                new AepContributions([]));
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(manifest, options: AepProtocol.JsonOptions) };
        }
    }
}
