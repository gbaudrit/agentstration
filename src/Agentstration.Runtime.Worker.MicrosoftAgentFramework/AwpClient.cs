using System.Net;
using System.Net.Http.Headers;
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

    public Task<AwpInvokeToolResponse> InvokeToolAsync(AwpInvokeToolRequest request,
        CancellationToken cancellationToken) => PostAsync<AwpInvokeToolRequest, AwpInvokeToolResponse>(
            AwpProtocol.InvokeToolPath, request, Guid.NewGuid(), cancellationToken);

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
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            HttpResponseMessage? response = null;
            try
            {
                using var request = CreateRequest(method, path, body);
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

    private HttpRequestMessage CreateRequest(HttpMethod method, string path, byte[] body)
    {
        var request = new HttpRequestMessage(method, path)
        {
            Content = new ByteArrayContent(body)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
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
}

internal sealed class AwpClientException(string code, string message, HttpStatusCode statusCode) : Exception(message)
{
    public string Code { get; } = code;
    public HttpStatusCode StatusCode { get; } = statusCode;
}
