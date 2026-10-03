using System.Net;
using System.Net.Http.Json;
using System.Text;
using Agentstration.Identity.Contracts;
using Agentstration.Security.Contracts;
using Agentstration.Secrets.Abstractions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agentstration.Management.Tests;

public sealed partial class SecurityApiTests
{
    [TestMethod]
    public async Task AwpWorkerPairingAuthenticationRotationAndRevocationAreIsolated()
    {
        await using var factory = AwpFactory();
        using var administrator = UnredirectedClient(factory);
        await BootstrapAsync(administrator, "awp-admin");
        var workerId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();

        using (var announce = await administrator.PostAsJsonAsync("/api/awp/enrollment/announce",
                   new AnnounceAwpWorkerRequest(workerId, "MAF Worker 1", AwpWorkerAuthentication.ProtocolVersion)))
            Assert.AreEqual(HttpStatusCode.OK, announce.StatusCode);
        var pairing = await administrator.PostAsync($"/api/identity/awp-workers/{workerId:D}/pairing-code", null);
        Assert.AreEqual(HttpStatusCode.OK, pairing.StatusCode);
        var code = await pairing.Content.ReadFromJsonAsync<AwpWorkerPairingCode>()
            ?? throw new InvalidOperationException("The pairing response was empty.");
        using (var invalidClaim = await administrator.PostAsJsonAsync("/api/awp/enrollment/claim",
                   new ClaimAwpWorkerRequest(workerId, "000000000")))
            Assert.AreEqual(HttpStatusCode.UnprocessableEntity, invalidClaim.StatusCode);
        var claim = await administrator.PostAsJsonAsync("/api/awp/enrollment/claim", new ClaimAwpWorkerRequest(workerId, code.Code));
        Assert.AreEqual(HttpStatusCode.OK, claim.StatusCode);
        var credential = await claim.Content.ReadFromJsonAsync<AwpWorkerCredential>()
            ?? throw new InvalidOperationException("The credential response was empty.");

        using (var startSession = await administrator.SendAsync(SignedRequest(
                   credential, sessionId, path: "/api/awp/v1/session", method: HttpMethod.Post)))
            Assert.AreEqual(HttpStatusCode.NoContent, startSession.StatusCode);

        using var authenticated = SignedRequest(credential, sessionId);
        using var authenticatedResponse = await administrator.SendAsync(authenticated);
        Assert.AreEqual(HttpStatusCode.OK, authenticatedResponse.StatusCode);
        var identity = await authenticatedResponse.Content.ReadFromJsonAsync<AwpIdentityResponse>();
        Assert.AreEqual(workerId.ToString("D"), identity?.WorkerId);
        Assert.AreEqual(sessionId.ToString("D"), identity?.WorkerSessionId);

        var replayNonce = AwpWorkerAuthentication.CreateNonce();
        using (var first = await administrator.SendAsync(SignedRequest(credential, sessionId, replayNonce)))
            Assert.AreEqual(HttpStatusCode.OK, first.StatusCode);
        using (var replay = await administrator.SendAsync(SignedRequest(credential, sessionId, replayNonce)))
            Assert.AreEqual(HttpStatusCode.Unauthorized, replay.StatusCode);

        using var bffHeaders = new HttpRequestMessage(HttpMethod.Get, "/api/awp/v1/identity");
        bffHeaders.Headers.Add(BffWorkloadAuthentication.WorkloadHeader, "console-bff");
        using (var denied = await administrator.SendAsync(bffHeaders))
            Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode);

        var rotation = await administrator.PostAsync($"/api/identity/awp-workers/{workerId:D}/credentials/rotate", null);
        Assert.AreEqual(HttpStatusCode.OK, rotation.StatusCode);
        var rotated = await rotation.Content.ReadFromJsonAsync<AwpWorkerCredential>()
            ?? throw new InvalidOperationException("The rotation response was empty.");
        using (var overlappingCredential = await administrator.SendAsync(SignedRequest(credential, sessionId)))
            Assert.AreEqual(HttpStatusCode.OK, overlappingCredential.StatusCode);
        using (var newCredential = await administrator.SendAsync(SignedRequest(rotated, sessionId)))
            Assert.AreEqual(HttpStatusCode.OK, newCredential.StatusCode);

        var replacementSessionId = Guid.NewGuid();
        using (var replaceSession = await administrator.SendAsync(SignedRequest(
                   rotated, replacementSessionId, path: "/api/awp/v1/session", method: HttpMethod.Post)))
            Assert.AreEqual(HttpStatusCode.NoContent, replaceSession.StatusCode);
        using (var supersededSession = await administrator.SendAsync(SignedRequest(rotated, sessionId)))
            Assert.AreEqual(HttpStatusCode.Unauthorized, supersededSession.StatusCode);
        using (var replacementSession = await administrator.SendAsync(SignedRequest(rotated, replacementSessionId)))
            Assert.AreEqual(HttpStatusCode.OK, replacementSession.StatusCode);

        using var revokePrevious = await administrator.PostAsync(
            $"/api/identity/awp-workers/{workerId:D}/credentials/{credential.CredentialId:D}/revoke", null);
        Assert.AreEqual(HttpStatusCode.NoContent, revokePrevious.StatusCode);
        using (var oldCredential = await administrator.SendAsync(SignedRequest(credential, replacementSessionId)))
            Assert.AreEqual(HttpStatusCode.Unauthorized, oldCredential.StatusCode);

        using var revoke = await administrator.PostAsync(
            $"/api/identity/awp-workers/{workerId:D}/credentials/{rotated.CredentialId:D}/revoke", null);
        Assert.AreEqual(HttpStatusCode.NoContent, revoke.StatusCode);
        using (var revoked = await administrator.SendAsync(SignedRequest(rotated, replacementSessionId)))
            Assert.AreEqual(HttpStatusCode.Unauthorized, revoked.StatusCode);

        var audit = await factory.Services.GetRequiredService<ISecurityAuditStore>().ListLatestAsync(100, default);
        Assert.IsTrue(audit.Any(value => value.Action == SecurityAuditActions.AwpWorkerEnrollmentAnnounced));
        Assert.IsTrue(audit.Any(value => value.Action == SecurityAuditActions.AwpWorkerPairingCodeIssued));
        Assert.IsTrue(audit.Any(value => value.Action == SecurityAuditActions.AwpWorkerCredentialIssued));
        Assert.IsTrue(audit.Any(value => value.Action == SecurityAuditActions.AwpWorkerCredentialRotated));
        Assert.IsTrue(audit.Any(value => value.Action == SecurityAuditActions.AwpWorkerCredentialRevoked));
        Assert.IsTrue(audit.Any(value => value.Action == SecurityAuditActions.AwpWorkerSessionStarted));
        Assert.IsTrue(audit.Any(value => value.Action == SecurityAuditActions.AwpWorkerEnrollmentFailed));
        Assert.IsTrue(audit.Any(value => value.Action == SecurityAuditActions.AwpWorkerAuthenticationFailed));
    }

    [TestMethod]
    public async Task AwpPairingCodeIsSingleUseUnderConcurrentClaims()
    {
        await using var factory = AwpFactory();
        using var administrator = UnredirectedClient(factory);
        await BootstrapAsync(administrator, "awp-concurrency-admin");
        var workerId = Guid.NewGuid();
        using var announce = await administrator.PostAsJsonAsync("/api/awp/enrollment/announce",
            new AnnounceAwpWorkerRequest(workerId, "Concurrent Worker", AwpWorkerAuthentication.ProtocolVersion));
        Assert.AreEqual(HttpStatusCode.OK, announce.StatusCode);
        using var pairingResponse = await administrator.PostAsync($"/api/identity/awp-workers/{workerId:D}/pairing-code", null);
        var pairing = await pairingResponse.Content.ReadFromJsonAsync<AwpWorkerPairingCode>()
            ?? throw new InvalidOperationException("The pairing response was empty.");

        var results = await Task.WhenAll(
            administrator.PostAsJsonAsync("/api/awp/enrollment/claim", new ClaimAwpWorkerRequest(workerId, pairing.Code)),
            administrator.PostAsJsonAsync("/api/awp/enrollment/claim", new ClaimAwpWorkerRequest(workerId, pairing.Code)));
        try
        {
            Assert.AreEqual(1, results.Count(value => value.StatusCode == HttpStatusCode.OK));
            Assert.AreEqual(1, results.Count(value => value.StatusCode == HttpStatusCode.Conflict));
        }
        finally
        {
            foreach (var response in results) response.Dispose();
        }
    }

    [TestMethod]
    public async Task FileProvisionedAwpCredentialCanActivateItsIndependentSession()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"agentstration-awp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var keyPath = Path.Combine(directory, "worker.key");
        const string secret = "a-file-provisioned-awp-worker-secret-with-sufficient-length";
        await File.WriteAllTextAsync(keyPath, secret);
        try
        {
            var workerId = Guid.NewGuid();
            var credentialId = Guid.NewGuid();
            await using var factory = AwpFactory(workerId, credentialId, keyPath);
            using var client = UnredirectedClient(factory);
            var credential = new AwpWorkerCredential(workerId, credentialId, "test-instance", secret);
            var sessionId = Guid.NewGuid();

            using (var activate = await client.SendAsync(SignedRequest(
                       credential, sessionId, path: "/api/awp/v1/session", method: HttpMethod.Post)))
                Assert.AreEqual(HttpStatusCode.NoContent, activate.StatusCode);
            using (var identity = await client.SendAsync(SignedRequest(credential, sessionId)))
                Assert.AreEqual(HttpStatusCode.OK, identity.StatusCode);
        }
        finally { Directory.Delete(directory, true); }
    }

    private static WebApplicationFactory<Program> AwpFactory() => Factory("Local").WithWebHostBuilder(builder =>
    {
        builder.UseSetting("Agentstration:AwpWorkerTrust:Enabled", "true");
        builder.UseSetting("Agentstration:AwpWorkerTrust:InstanceId", "test-instance");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IMasterKeyProvider>();
            services.AddSingleton<IMasterKeyProvider, AwpTestMasterKeyProvider>();
        });
    });

    private static WebApplicationFactory<Program> AwpFactory(Guid workerId, Guid credentialId, string keyPath) =>
        AwpFactory().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Agentstration:AwpWorkerTrust:Credentials:0:WorkerId", workerId.ToString("D"));
            builder.UseSetting("Agentstration:AwpWorkerTrust:Credentials:0:CredentialId", credentialId.ToString("D"));
            builder.UseSetting("Agentstration:AwpWorkerTrust:Credentials:0:SharedKeyFile", keyPath);
        });

    private static HttpRequestMessage SignedRequest(
        AwpWorkerCredential credential,
        Guid sessionId,
        string? nonce = null,
        string path = "/api/awp/v1/identity",
        HttpMethod? method = null)
    {
        method ??= HttpMethod.Get;
        nonce ??= AwpWorkerAuthentication.CreateNonce();
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var hash = AwpWorkerAuthentication.HashContent([]);
        var canonical = AwpWorkerAuthentication.Canonicalize(
            method.Method, path, credential.InstanceId, credential.WorkerId, sessionId, timestamp, nonce, hash);
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add(AwpWorkerAuthentication.WorkerHeader, credential.WorkerId.ToString("D"));
        request.Headers.Add(AwpWorkerAuthentication.SessionHeader, sessionId.ToString("D"));
        request.Headers.Add(AwpWorkerAuthentication.CredentialHeader, credential.CredentialId.ToString("D"));
        request.Headers.Add(AwpWorkerAuthentication.InstanceHeader, credential.InstanceId);
        request.Headers.Add(AwpWorkerAuthentication.TimestampHeader, timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.Add(AwpWorkerAuthentication.NonceHeader, nonce);
        request.Headers.Add(AwpWorkerAuthentication.ContentHashHeader, hash);
        request.Headers.Add(AwpWorkerAuthentication.SignatureHeader,
            AwpWorkerAuthentication.Sign(Encoding.UTF8.GetBytes(credential.Secret), canonical));
        return request;
    }

    private sealed record AwpIdentityResponse(string WorkerId, string WorkerSessionId, string ProtocolVersion);

    private sealed class AwpTestMasterKeyProvider : IMasterKeyProvider
    {
        private static readonly byte[] Key = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();
        public ValueTask<byte[]> GetKeyAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Key.ToArray());
    }
}
