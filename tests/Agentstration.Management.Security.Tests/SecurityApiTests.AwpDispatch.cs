using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.Awp.Abstractions;
using Agentstration.Identity.Contracts;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Agentstration.Runtime.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Management.Tests;

public sealed partial class SecurityApiTests
{
    [TestMethod]
    public async Task AuthenticatedAwpWorkerRegistersClaimsAndRenewsItsLease()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"agentstration-awp-dispatch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var keyPath = Path.Combine(directory, "worker.key");
        const string secret = "an-awp-dispatch-worker-secret-with-sufficient-length";
        await File.WriteAllTextAsync(keyPath, secret);
        try
        {
            var workerId = Guid.NewGuid();
            var sessionId = Guid.NewGuid();
            var credential = new AwpWorkerCredential(workerId, Guid.NewGuid(), "test-instance", secret);
            await using var factory = AwpFactory(workerId, credential.CredentialId, keyPath);
            using var client = UnredirectedClient(factory);
            using (var activate = await client.SendAsync(SignedRequest(
                       credential, sessionId, path: "/api/awp/v1/session", method: HttpMethod.Post)))
                Assert.AreEqual(HttpStatusCode.NoContent, activate.StatusCode);

            var unsupportedRegistration = Envelope(new AwpWorkerRegistrationRequest(
                ["2.0"],
                new(new(workerId), new(sessionId), "worker-1.2.3", 1,
                    [new(AwpRuntimeKinds.MicrosoftAgentFramework, "1.0", ["1.0"], "maf-1.2.3")])));
            using (var unsupported = await client.SendAsync(SignedJsonRequest(
                       credential, sessionId, AwpProtocol.RegistrationPath, unsupportedRegistration)))
                Assert.AreEqual(HttpStatusCode.BadRequest, unsupported.StatusCode);

            var workspaceId = new WorkspaceId(Guid.NewGuid());
            var tenantId = Guid.NewGuid();
            var runId = $"run-{Guid.NewGuid():N}";
            var runs = factory.Services.GetRequiredService<IRuntimeRunStore>();
            var assignments = factory.Services.GetRequiredService<RuntimeWorkerAssignmentService>();
            await runs.CreateAsync(new RuntimeRun
            {
                WorkspaceId = workspaceId,
                Scope = new RuntimeRunScope(tenantId, workspaceId, Guid.NewGuid()),
                Id = runId,
                Name = runId,
                Properties = new RuntimeRunProperties
                {
                    Agent = new RuntimeAgentReference("agent", 1),
                    Input = new RuntimeRunInput { Messages = [new(RuntimeMessageRole.User, "test")] },
                    Execution = new RuntimeExecutionOptions()
                },
                Status = new RuntimeRunStatus { State = RuntimeRunState.Pending, CreatedAt = DateTimeOffset.UtcNow }
            }, default);
            _ = await assignments.CreateAsync(workspaceId, runId, AwpRuntimeKinds.MicrosoftAgentFramework,
                "1.0", "1.0", $"material-{runId}", "sha256:test-material", default);

            var registration = Envelope(new AwpWorkerRegistrationRequest(
                [AwpProtocol.Version],
                new(new(workerId), new(sessionId), "worker-1.2.3", 1,
                    [new(AwpRuntimeKinds.MicrosoftAgentFramework, "1.0", ["1.0"], "maf-1.2.3")])));
            using (var register = await client.SendAsync(SignedJsonRequest(
                       credential, sessionId, AwpProtocol.RegistrationPath, registration)))
            {
                Assert.AreEqual(HttpStatusCode.OK, register.StatusCode);
                var response = await register.Content.ReadFromJsonAsync<AwpEnvelope<AwpWorkerRegistrationResponse>>(AwpProtocol.JsonOptions);
                Assert.AreEqual(AwpProtocol.Version, response?.Payload.SelectedProtocolVersion);
            }

            var claimRequest = Envelope(new AwpClaimRequest(new(workerId), new(sessionId), 1, 0));
            AwpRunAssignment claimed;
            using (var claim = await client.SendAsync(SignedJsonRequest(
                       credential, sessionId, AwpProtocol.ClaimPath, claimRequest)))
            {
                Assert.AreEqual(HttpStatusCode.OK, claim.StatusCode);
                var response = await claim.Content.ReadFromJsonAsync<AwpEnvelope<AwpClaimResponse>>(AwpProtocol.JsonOptions);
                claimed = response?.Payload.Assignment ?? throw new AssertFailedException("Expected an AWP assignment.");
            }
            Assert.AreEqual(runId, ((AwpDirectAgentRunTarget)claimed.Target).RuntimeRunId);
            Assert.AreEqual(workspaceId.Value.ToString("D"), claimed.Scope.WorkspaceId);
            Assert.AreEqual("sha256:test-material", claimed.ExecutionMaterial.Digest);

            var context = new AwpAssignmentCommandContext(
                new(workerId), new(sessionId), claimed.Scope, claimed.AssignmentId, claimed.AttemptId,
                claimed.Ownership.Token, claimed.Ownership.FencingGeneration);
            var heartbeatRequest = Envelope(new AwpHeartbeatRequest(context, 0));
            using var heartbeat = await client.SendAsync(SignedJsonRequest(
                credential, sessionId, AwpProtocol.HeartbeatPath, heartbeatRequest));
            Assert.AreEqual(HttpStatusCode.OK, heartbeat.StatusCode);
            var heartbeatResponse = await heartbeat.Content.ReadFromJsonAsync<AwpEnvelope<AwpHeartbeatResponse>>(AwpProtocol.JsonOptions);
            Assert.IsTrue(heartbeatResponse?.Payload.LeaseExpiresAt > claimed.Ownership.LeaseExpiresAt);
            Assert.IsFalse(heartbeatResponse?.Payload.Cancellation.Requested);

            var forgedContext = context with
            {
                Scope = context.Scope with { WorkspaceId = Guid.NewGuid().ToString("D") }
            };
            using var forgedHeartbeat = await client.SendAsync(SignedJsonRequest(
                credential, sessionId, AwpProtocol.HeartbeatPath,
                Envelope(new AwpHeartbeatRequest(forgedContext, 0))));
            Assert.AreEqual(HttpStatusCode.Conflict, forgedHeartbeat.StatusCode);

            var replacementSessionId = Guid.NewGuid();
            using (var activateReplacement = await client.SendAsync(SignedRequest(
                       credential, replacementSessionId, path: "/api/awp/v1/session", method: HttpMethod.Post)))
                Assert.AreEqual(HttpStatusCode.NoContent, activateReplacement.StatusCode);
            var replacementRegistration = Envelope(registration.Payload with
            {
                Worker = registration.Payload.Worker with { SessionId = new(replacementSessionId) }
            });
            using (var replace = await client.SendAsync(SignedJsonRequest(
                       credential, replacementSessionId, AwpProtocol.RegistrationPath, replacementRegistration)))
                Assert.AreEqual(HttpStatusCode.OK, replace.StatusCode);
            using (var formerSessionHeartbeat = await client.SendAsync(SignedJsonRequest(
                       credential, sessionId, AwpProtocol.HeartbeatPath, heartbeatRequest)))
                Assert.AreEqual(HttpStatusCode.Unauthorized, formerSessionHeartbeat.StatusCode);
            var assignmentStore = factory.Services.GetRequiredService<IRuntimeWorkerAssignmentStore>();
            var interrupted = await assignmentStore.GetAsync(
                workspaceId, new RuntimeAssignmentId(claimed.AssignmentId.Value), default);
            Assert.AreEqual(RuntimeAssignmentState.Failed, interrupted?.Value.State);
            Assert.AreEqual("worker_lost", interrupted?.Value.CurrentAttempt?.ErrorCode);
        }
        finally { Directory.Delete(directory, true); }
    }

    private static AwpEnvelope<T> Envelope<T>(T payload) =>
        new(AwpProtocol.Version, Guid.NewGuid(), DateTimeOffset.UtcNow, payload);

    private static HttpRequestMessage SignedJsonRequest<T>(
        AwpWorkerCredential credential,
        Guid sessionId,
        string path,
        T body)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(body, AwpProtocol.JsonOptions);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var nonce = AwpWorkerAuthentication.CreateNonce();
        var hash = AwpWorkerAuthentication.HashContent(bytes);
        var canonical = AwpWorkerAuthentication.Canonicalize(
            HttpMethod.Post.Method, path, credential.InstanceId, credential.WorkerId, sessionId, timestamp, nonce, hash);
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = content };
        request.Headers.Add(AwpWorkerAuthentication.WorkerHeader, credential.WorkerId.ToString("D"));
        request.Headers.Add(AwpWorkerAuthentication.SessionHeader, sessionId.ToString("D"));
        request.Headers.Add(AwpWorkerAuthentication.CredentialHeader, credential.CredentialId.ToString("D"));
        request.Headers.Add(AwpWorkerAuthentication.InstanceHeader, credential.InstanceId);
        request.Headers.Add(AwpWorkerAuthentication.TimestampHeader,
            timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.Add(AwpWorkerAuthentication.NonceHeader, nonce);
        request.Headers.Add(AwpWorkerAuthentication.ContentHashHeader, hash);
        request.Headers.Add(AwpWorkerAuthentication.SignatureHeader,
            AwpWorkerAuthentication.Sign(System.Text.Encoding.UTF8.GetBytes(credential.Secret), canonical));
        return request;
    }
}
