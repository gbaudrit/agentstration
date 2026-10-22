using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Agentstration.Awp.Abstractions;
using Agentstration.Identity.Contracts;

namespace Agentstration.Runtime.Worker.MicrosoftAgentFramework;

internal sealed class AwpClient : IDisposable
{
    private readonly HttpClient http;
    private readonly AwpClientCredential credential;
    private readonly AwpWorkerSessionId sessionId;
    private readonly TimeProvider timeProvider;
    private readonly int retryCount;

    public AwpClient(Uri authority, AwpClientCredential credential, AwpWorkerSessionId sessionId,
        TimeProvider timeProvider, int retryCount, HttpMessageHandler? handler = null)
    {
        this.credential = credential;
        this.sessionId = sessionId;
        this.timeProvider = timeProvider;
        this.retryCount = retryCount;
        http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: true);
        http.BaseAddress = authority;
        http.Timeout = Timeout.InfiniteTimeSpan;
    }

    public AwpWorkerId WorkerId => new(credential.WorkerId);
    public AwpWorkerSessionId SessionId => sessionId;

    public async Task ActivateSessionAsync(CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Post, "/api/awp/v1/session", [], cancellationToken);
    }

    public Task<AwpWorkerRegistrationResponse> RegisterAsync(AwpWorkerRegistrationRequest request,
        CancellationToken cancellationToken) => PostAsync<AwpWorkerRegistrationRequest, AwpWorkerRegistrationResponse>(
            AwpProtocol.RegistrationPath, request, Guid.NewGuid(), cancellationToken);

    public Task<AwpClaimResponse> ClaimAsync(AwpClaimRequest request, CancellationToken cancellationToken) =>
        PostAsync<AwpClaimRequest, AwpClaimResponse>(AwpProtocol.ClaimPath, request, Guid.NewGuid(), cancellationToken);

    public Task<AwpHeartbeatResponse> HeartbeatAsync(AwpHeartbeatRequest request, CancellationToken cancellationToken) =>
        PostAsync<AwpHeartbeatRequest, AwpHeartbeatResponse>(AwpProtocol.HeartbeatPath, request, Guid.NewGuid(), cancellationToken);

    public Task<AwpGetExecutionMaterialResponse> GetMaterialAsync(AwpAssignmentCommandContext context,
        CancellationToken cancellationToken) => PostAsync<AwpGetExecutionMaterialRequest, AwpGetExecutionMaterialResponse>(
            AwpProtocol.ExecutionMaterialPath, new(context), Guid.NewGuid(), cancellationToken);

    public Task<AwpOpenStepExecutionResponse> OpenStepAsync(AwpOpenStepExecutionRequest request, Guid commandId,
        CancellationToken cancellationToken) => PostAsync<AwpOpenStepExecutionRequest, AwpOpenStepExecutionResponse>(
            AwpProtocol.OpenStepExecutionPath, request, commandId, cancellationToken);

    public Task<AwpOpenTurnResponse> OpenTurnAsync(AwpOpenTurnRequest request, Guid commandId,
        CancellationToken cancellationToken) => PostAsync<AwpOpenTurnRequest, AwpOpenTurnResponse>(
            AwpProtocol.OpenTurnPath, request, commandId, cancellationToken);

    public Task<AwpAppendEventsResponse> AppendEventsAsync(AwpAppendEventsRequest request,
        CancellationToken cancellationToken) => PostAsync<AwpAppendEventsRequest, AwpAppendEventsResponse>(
            AwpProtocol.AppendEventsPath, request, Guid.NewGuid(), cancellationToken);

    public Task<AwpCheckpointResponse> StoreCheckpointAsync(AwpStoreCheckpointRequest request,
        CancellationToken cancellationToken) => PostAsync<AwpStoreCheckpointRequest, AwpCheckpointResponse>(
            AwpProtocol.StoreCheckpointPath, request, Guid.NewGuid(), cancellationToken);

    public Task<AwpCheckpointResponse> GetCheckpointAsync(AwpGetCheckpointRequest request,
        CancellationToken cancellationToken) => PostAsync<AwpGetCheckpointRequest, AwpCheckpointResponse>(
            AwpProtocol.GetCheckpointPath, request, Guid.NewGuid(), cancellationToken);

    public Task<AwpInvokeModelResponse> InvokeModelAsync(AwpInvokeModelRequest request,
        CancellationToken cancellationToken) => PostAsync<AwpInvokeModelRequest, AwpInvokeModelResponse>(
            AwpProtocol.InvokeModelPath, request, Guid.NewGuid(), cancellationToken);

    public async IAsyncEnumerable<AwpModelStreamEvent> StreamModelAsync(
        AwpInvokeModelRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        const int maxEvents = 4096;
        const int maxLineBytes = 64 * 1024;
        const int maxTotalBytes = 8 * 1024 * 1024;
        var messageId = Guid.NewGuid();
        var envelope = new AwpEnvelope<AwpInvokeModelRequest>(
            AwpProtocol.Version, messageId, timeProvider.GetUtcNow(), request);
        var body = JsonSerializer.SerializeToUtf8Bytes(envelope, AwpProtocol.JsonOptions);
        using var response = await SendAsync(HttpMethod.Post, AwpProtocol.StreamModelPath, body,
            cancellationToken, "text/event-stream");
        if (!string.Equals(response.Content.Headers.ContentType?.MediaType, "text/event-stream",
            StringComparison.OrdinalIgnoreCase))
            throw new AwpModelStreamException("invalid_response",
                "The AWP model stream response is not an event stream.");

        await using var content = await response.Content.ReadAsStreamAsync(cancellationToken);
        var reader = new BoundedLineReader(content, maxLineBytes, maxTotalBytes);
        var data = new StringBuilder();
        Guid? streamId = null;
        long expectedSequence = 0;
        var eventCount = 0;
        var started = false;
        var terminal = false;

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Length != 0)
            {
                if (line[0] == ':') continue;
                if (!line.StartsWith("data:", StringComparison.Ordinal))
                    throw new AwpModelStreamException("invalid_response",
                        "The AWP model stream contains an unsupported SSE field.");
                var value = line.AsSpan(5);
                if (!value.IsEmpty && value[0] == ' ') value = value[1..];
                if (data.Length > 0) data.Append('\n');
                if (data.Length + value.Length > maxLineBytes)
                    throw new AwpModelStreamException("response_limit",
                        "An AWP model stream event exceeds its size limit.");
                data.Append(value);
                continue;
            }

            if (data.Length == 0) continue;
            if (terminal)
                throw new AwpModelStreamException("invalid_response",
                    "The AWP model stream contains content after its terminal event.");
            if (++eventCount > maxEvents)
                throw new AwpModelStreamException("response_limit",
                    "The AWP model stream exceeds its event limit.");

            AwpEnvelope<AwpModelStreamEvent>? frame;
            try
            {
                frame = JsonSerializer.Deserialize<AwpEnvelope<AwpModelStreamEvent>>(
                    data.ToString(), AwpProtocol.JsonOptions);
            }
            catch (JsonException exception)
            {
                throw new AwpModelStreamException("invalid_response",
                    "The AWP model stream contains invalid JSON.", exception);
            }
            finally
            {
                data.Clear();
            }

            if (frame?.Payload is null
                || !string.Equals(frame.ProtocolVersion, AwpProtocol.Version, StringComparison.Ordinal)
                || frame.MessageId != messageId)
                throw new AwpModelStreamException("invalid_response",
                    "The AWP model stream envelope is invalid.");
            var streamEvent = frame.Payload;
            if (streamId is null) streamId = streamEvent.StreamId;
            if (streamEvent.StreamId == Guid.Empty || streamEvent.StreamId != streamId
                || streamEvent.SequenceNumber != expectedSequence++)
                throw new AwpModelStreamException("invalid_response",
                    "The AWP model stream identity or sequence is invalid.");
            if (!started && streamEvent is not AwpModelStreamStarted)
                throw new AwpModelStreamException("invalid_response",
                    "The AWP model stream does not begin with a start event.");
            if (started && streamEvent is AwpModelStreamStarted)
                throw new AwpModelStreamException("invalid_response",
                    "The AWP model stream contains more than one start event.");
            started = true;
            terminal = streamEvent is AwpModelStreamCompleted or AwpModelStreamFailed;
            yield return streamEvent;
        }

        if (data.Length != 0 || !started || !terminal)
            throw new AwpModelStreamException("incomplete_response",
                "The AWP model stream ended without a valid terminal event.");
    }

    public Task<AwpInvokeToolResponse> InvokeToolAsync(AwpInvokeToolRequest request,
        CancellationToken cancellationToken) => PostAsync<AwpInvokeToolRequest, AwpInvokeToolResponse>(
            AwpProtocol.InvokeToolPath, request, Guid.NewGuid(), cancellationToken);

    public Task<AwpInvokeFlowToolResponse> InvokeFlowToolAsync(AwpInvokeFlowToolRequest request,
        CancellationToken cancellationToken) => PostAsync<AwpInvokeFlowToolRequest, AwpInvokeFlowToolResponse>(
            AwpProtocol.InvokeFlowToolPath, request, Guid.NewGuid(), cancellationToken);

    public Task<AwpCaptureFlowArtifactResponse> CaptureFlowArtifactAsync(AwpCaptureFlowArtifactRequest request,
        CancellationToken cancellationToken) => PostAsync<AwpCaptureFlowArtifactRequest, AwpCaptureFlowArtifactResponse>(
            AwpProtocol.CaptureFlowArtifactPath, request, Guid.NewGuid(), cancellationToken);

    public async Task CleanupFlowArtifactAsync(AwpCleanupFlowArtifactRequest request,
        CancellationToken cancellationToken) => _ = await PostAsync<AwpCleanupFlowArtifactRequest, AwpCleanupFlowArtifactResponse>(
            AwpProtocol.CleanupFlowArtifactPath, request, Guid.NewGuid(), cancellationToken);

    public Task<AwpArtifactResponse> StoreArtifactAsync(AwpStoreArtifactRequest request,
        CancellationToken cancellationToken) => PostAsync<AwpStoreArtifactRequest, AwpArtifactResponse>(
            AwpProtocol.StoreArtifactPath, request, Guid.NewGuid(), cancellationToken);

    public Task<AwpArtifactResponse> GetArtifactAsync(AwpGetArtifactRequest request,
        CancellationToken cancellationToken) => PostAsync<AwpGetArtifactRequest, AwpArtifactResponse>(
            AwpProtocol.GetArtifactPath, request, Guid.NewGuid(), cancellationToken);

    public Task<AwpChildFlowResponse> CreateChildFlowAsync(AwpCreateChildFlowRequest request,
        CancellationToken cancellationToken) => PostAsync<AwpCreateChildFlowRequest, AwpChildFlowResponse>(
            AwpProtocol.CreateChildFlowPath, request, Guid.NewGuid(), cancellationToken);

    public Task<AwpTerminalResponse> CompleteAsync(AwpCompleteAssignmentRequest request,
        CancellationToken cancellationToken) => PostAsync<AwpCompleteAssignmentRequest, AwpTerminalResponse>(
            AwpProtocol.CompleteAssignmentPath, request, request.CommandId.Value, cancellationToken);

    public Task<AwpTerminalResponse> FailAsync(AwpFailAssignmentRequest request,
        CancellationToken cancellationToken) => PostAsync<AwpFailAssignmentRequest, AwpTerminalResponse>(
            AwpProtocol.FailAssignmentPath, request, request.CommandId.Value, cancellationToken);

    private async Task<TResponse> PostAsync<TRequest, TResponse>(string path, TRequest payload, Guid messageId,
        CancellationToken cancellationToken)
    {
        var envelope = new AwpEnvelope<TRequest>(AwpProtocol.Version, messageId, timeProvider.GetUtcNow(), payload);
        var body = JsonSerializer.SerializeToUtf8Bytes(envelope, AwpProtocol.JsonOptions);
        using var response = await SendAsync(HttpMethod.Post, path, body, cancellationToken);
        var result = await response.Content.ReadFromJsonAsync<AwpEnvelope<TResponse>>(AwpProtocol.JsonOptions, cancellationToken)
            ?? throw new AwpClientException("invalid_response", "The AWP response envelope is empty.", response.StatusCode);
        if (!string.Equals(result.ProtocolVersion, AwpProtocol.Version, StringComparison.Ordinal)
            || result.MessageId != messageId || result.Payload is null)
            throw new AwpClientException("invalid_response", "The AWP response envelope is invalid.", response.StatusCode);
        return result.Payload;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, byte[] body,
        CancellationToken cancellationToken, string? accept = null)
    {
        for (var attempt = 0; ; attempt++)
        {
            HttpResponseMessage? response = null;
            try
            {
                using var request = CreateRequest(method, path, body, accept);
                response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (response.IsSuccessStatusCode) return response;
                if (!IsTransient(response.StatusCode) || attempt >= retryCount)
                {
                    var exception = await CreateExceptionAsync(response, cancellationToken);
                    response.Dispose();
                    throw exception;
                }
                response.Dispose();
            }
            catch (HttpRequestException) when (attempt < retryCount)
            {
                response?.Dispose();
            }
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(2000, 200 * (1 << attempt))), timeProvider, cancellationToken);
        }
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path, byte[] body, string? accept = null)
    {
        var request = new HttpRequestMessage(method, path)
        {
            Content = new ByteArrayContent(body)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        if (accept is not null) request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
        var timestamp = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        var nonce = AwpWorkerAuthentication.CreateNonce();
        var contentHash = AwpWorkerAuthentication.HashContent(body);
        var canonical = AwpWorkerAuthentication.Canonicalize(method.Method, path, credential.InstanceId,
            credential.WorkerId, sessionId.Value, timestamp, nonce, contentHash);
        request.Headers.Add(AwpWorkerAuthentication.WorkerHeader, credential.WorkerId.ToString("D"));
        request.Headers.Add(AwpWorkerAuthentication.SessionHeader, sessionId.Value.ToString("D"));
        request.Headers.Add(AwpWorkerAuthentication.CredentialHeader, credential.CredentialId.ToString("D"));
        request.Headers.Add(AwpWorkerAuthentication.InstanceHeader, credential.InstanceId);
        request.Headers.Add(AwpWorkerAuthentication.TimestampHeader, timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.Add(AwpWorkerAuthentication.NonceHeader, nonce);
        request.Headers.Add(AwpWorkerAuthentication.ContentHashHeader, contentHash);
        request.Headers.Add(AwpWorkerAuthentication.SignatureHeader,
            AwpWorkerAuthentication.Sign(credential.Secret, canonical));
        return request;
    }

    private static bool IsTransient(HttpStatusCode statusCode) => statusCode is HttpStatusCode.RequestTimeout
        or HttpStatusCode.TooManyRequests or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable
        or HttpStatusCode.GatewayTimeout || (int)statusCode >= 500;

    private static async Task<AwpClientException> CreateExceptionAsync(HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        string? code = null;
        string? message = null;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("code", out var codeValue)) code = codeValue.GetString();
            if (document.RootElement.TryGetProperty("title", out var title)) message = title.GetString();
        }
        catch (JsonException) { }
        return new AwpClientException(code ?? "http_error",
            message ?? $"The AWP authority returned HTTP {(int)response.StatusCode}.", response.StatusCode);
    }

    public void Dispose() => http.Dispose();

    private sealed class BoundedLineReader(Stream stream, int maxLineBytes, int maxTotalBytes)
    {
        private static readonly UTF8Encoding Utf8 = new(false, true);
        private readonly byte[] buffer = new byte[4096];
        private int position;
        private int length;
        private int totalBytes;

        public async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            var line = new ArrayBufferWriter<byte>();
            while (true)
            {
                if (position == length)
                {
                    length = await stream.ReadAsync(buffer, cancellationToken);
                    position = 0;
                    if (length == 0)
                        return line.WrittenCount == 0 ? null : Decode(line.WrittenSpan);
                    totalBytes += length;
                    if (totalBytes > maxTotalBytes)
                        throw new AwpModelStreamException("response_limit",
                            "The AWP model stream exceeds its total size limit.");
                }

                var newline = Array.IndexOf(buffer, (byte)'\n', position, length - position);
                var end = newline < 0 ? length : newline;
                Append(line, buffer.AsSpan(position, end - position));
                if (line.WrittenCount > maxLineBytes)
                    throw new AwpModelStreamException("response_limit",
                        "An AWP model stream line exceeds its size limit.");
                position = newline < 0 ? length : newline + 1;
                if (newline >= 0) return Decode(line.WrittenSpan);
            }
        }

        private static void Append(ArrayBufferWriter<byte> writer, ReadOnlySpan<byte> value)
        {
            value.CopyTo(writer.GetSpan(value.Length));
            writer.Advance(value.Length);
        }

        private static string Decode(ReadOnlySpan<byte> value)
        {
            if (!value.IsEmpty && value[^1] == '\r') value = value[..^1];
            try { return Utf8.GetString(value); }
            catch (DecoderFallbackException exception)
            {
                throw new AwpModelStreamException("invalid_response",
                    "The AWP model stream is not valid UTF-8.", exception);
            }
        }
    }
}

internal sealed class AwpClientException(string code, string message, HttpStatusCode statusCode) : Exception(message)
{
    public string Code { get; } = code;
    public HttpStatusCode StatusCode { get; } = statusCode;
}

internal sealed class AwpModelStreamException(string code, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public string Code { get; } = code;
}
