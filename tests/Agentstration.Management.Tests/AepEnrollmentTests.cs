using Agentstration.Aep.Abstractions;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Management.Tests;

public sealed partial class ModelManagementApiTests
{
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
}
