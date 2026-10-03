using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.Awp.Abstractions;
using Agentstration.Identity.Contracts;
using Agentstration.Runtime.Worker.MicrosoftAgentFramework;

namespace Agentstration.Runtime.Tests;

[TestClass]
public sealed class AwpWorkerClientTests
{
    [TestMethod]
    public async Task RetryKeepsMessageIdentityAndUsesFreshSignedNonce()
    {
        var secret = System.Text.Encoding.UTF8.GetBytes("test-worker-secret-material-at-least-32-bytes");
        using var credential = new AwpClientCredential(
            Guid.Parse("10000000-0000-0000-0000-000000000001"),
            Guid.Parse("10000000-0000-0000-0000-000000000002"), "test-instance", secret);
        var handler = new RecordingHandler(secret, credential);
        using var client = new AwpClient(new Uri("https://authority.test"), credential,
            new(Guid.Parse("10000000-0000-0000-0000-000000000003")), TimeProvider.System, 1, handler);

        var response = await client.RegisterAsync(new([AwpProtocol.Version], new(client.WorkerId, client.SessionId,
            "test", 1, [new(AwpRuntimeKinds.MicrosoftAgentFramework, "1.0", ["1.0"])])), CancellationToken.None);

        Assert.AreEqual(AwpProtocol.Version, response.SelectedProtocolVersion);
        Assert.AreEqual(2, handler.Requests.Count);
        Assert.AreEqual(handler.Requests[0].MessageId, handler.Requests[1].MessageId);
        Assert.AreNotEqual(handler.Requests[0].Nonce, handler.Requests[1].Nonce);
        Assert.IsTrue(handler.Requests.All(request => request.SignatureValid));
    }

    [TestMethod]
    public void LeaseSafetyUsesAuthorityTimeAndHasNoGraceAfterMargin()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-10-03T12:00:00Z",
            System.Globalization.CultureInfo.InvariantCulture));
        using var credential = new AwpClientCredential(Guid.NewGuid(), Guid.NewGuid(), "test-instance",
            System.Text.Encoding.UTF8.GetBytes("test-worker-secret-material-at-least-32-bytes"));
        using var client = new AwpClient(new Uri("https://authority.test"), credential, new(Guid.NewGuid()), clock, 0,
            new RecordingHandler(credential.Secret, credential));
        var assignment = Assignment(clock.GetUtcNow().AddSeconds(20));
        var session = new AwpAssignmentSession(client, assignment, clock.GetUtcNow(), clock, TimeSpan.FromSeconds(8));
        Assert.IsTrue(session.CanStartMutation());

        clock.Advance(TimeSpan.FromSeconds(13));

        Assert.IsFalse(session.CanStartMutation());
        Assert.Throws<AwpLeaseUnsafeException>(session.EnsureCanStartMutation);
    }

    [TestMethod]
    public void WorkerOptionsRejectClearTextUnlessExplicitlyAllowed()
    {
        var options = new RuntimeWorkerOptions
        {
            AuthorityUrl = "http://localhost:5100",
            WorkerId = Guid.NewGuid(),
            CredentialId = Guid.NewGuid(),
            SharedKeyFile = "worker.key"
        };
        Assert.IsFalse(options.Validate());
        options.AllowInsecureHttp = true;
        Assert.IsTrue(options.Validate());
    }

    [TestMethod]
    public void WorkerUsesDedicatedTelemetrySource()
    {
        Assert.AreEqual("Agentstration.Runtime.Worker.MicrosoftAgentFramework",
            AwpRuntimeWorkerService.ActivitySource.Name);
    }

    [TestMethod]
    public async Task EventRejectedByTransportIsReplayedWithStableIdentityBeforeNextEvent()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-10-03T12:00:00Z",
            System.Globalization.CultureInfo.InvariantCulture));
        using var credential = new AwpClientCredential(Guid.NewGuid(), Guid.NewGuid(), "test-instance",
            System.Text.Encoding.UTF8.GetBytes("test-worker-secret-material-at-least-32-bytes"));
        var handler = new EventRecordingHandler();
        using var client = new AwpClient(new Uri("https://authority.test"), credential, new(Guid.NewGuid()),
            clock, 0, handler);
        var session = new AwpAssignmentSession(client, Assignment(clock.GetUtcNow().AddMinutes(1)),
            clock.GetUtcNow(), clock, TimeSpan.FromSeconds(5));
        var location = new AwpExecutionLocation("run-1");

        await Assert.ThrowsExactlyAsync<AwpClientException>(() => session.AppendEventAsync(
            AwpExecutionEventKind.AssignmentStarted, location, null, null, CancellationToken.None));
        await session.AppendEventAsync(AwpExecutionEventKind.RunStarted, location, null, null, CancellationToken.None);

        Assert.AreEqual(3, handler.Events.Count);
        Assert.AreEqual(handler.Events[0].EventId, handler.Events[1].EventId);
        Assert.AreEqual(1, handler.Events[0].AttemptEventSequence);
        Assert.AreEqual(1, handler.Events[1].AttemptEventSequence);
        Assert.AreNotEqual(handler.Events[1].EventId, handler.Events[2].EventId);
        Assert.AreEqual(2, handler.Events[2].AttemptEventSequence);
        Assert.AreEqual(2, session.LastEventSequence);
    }

    private static AwpRunAssignment Assignment(DateTimeOffset expiresAt) => new(
        new(Guid.NewGuid()), new(Guid.NewGuid()), new(Guid.NewGuid(), Guid.NewGuid().ToString("D")),
        new AwpDirectAgentRunTarget("run-1"), new(AwpRuntimeKinds.MicrosoftAgentFramework, "1.0", "1.0"),
        new("material-1", "1.0", "digest"), new("token", 1, expiresAt, 5));

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan duration) => now += duration;
    }

    private sealed class RecordingHandler(byte[] secret, AwpClientCredential credential) : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            var envelope = JsonSerializer.Deserialize<AwpEnvelope<JsonElement>>(body, AwpProtocol.JsonOptions)!;
            var nonce = Header(request, AwpWorkerAuthentication.NonceHeader);
            var timestamp = long.Parse(Header(request, AwpWorkerAuthentication.TimestampHeader),
                System.Globalization.CultureInfo.InvariantCulture);
            var hash = Header(request, AwpWorkerAuthentication.ContentHashHeader);
            var canonical = AwpWorkerAuthentication.Canonicalize(request.Method.Method,
                request.RequestUri!.PathAndQuery, credential.InstanceId, credential.WorkerId,
                Guid.Parse(Header(request, AwpWorkerAuthentication.SessionHeader)), timestamp, nonce, hash);
            Requests.Add(new(envelope.MessageId, nonce,
                AwpWorkerAuthentication.Verify(secret, canonical, Header(request, AwpWorkerAuthentication.SignatureHeader))));
            if (Requests.Count == 1) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            var response = new AwpEnvelope<AwpWorkerRegistrationResponse>(AwpProtocol.Version, envelope.MessageId,
                DateTimeOffset.UtcNow, new(AwpProtocol.Version, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(response, options: AwpProtocol.JsonOptions)
            };
        }

        private static string Header(HttpRequestMessage request, string name) =>
            request.Headers.GetValues(name).Single();
    }

    private sealed class EventRecordingHandler : HttpMessageHandler
    {
        public List<AwpExecutionEvent> Events { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var envelope = await request.Content!.ReadFromJsonAsync<AwpEnvelope<AwpAppendEventsRequest>>(
                AwpProtocol.JsonOptions, cancellationToken);
            var executionEvent = envelope!.Payload.Events.Single();
            Events.Add(executionEvent);
            if (Events.Count == 1)
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);

            var response = new AwpEnvelope<AwpAppendEventsResponse>(AwpProtocol.Version, envelope.MessageId,
                DateTimeOffset.UtcNow, new(DateTimeOffset.UtcNow, executionEvent.AttemptEventSequence, []));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(response, options: AwpProtocol.JsonOptions)
            };
        }
    }

    private sealed record RecordedRequest(Guid MessageId, string Nonce, bool SignatureValid);
}
