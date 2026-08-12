using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Agentstration.Aep.Abstractions;

namespace Agentstration.Aep.Client;

public interface IAepClient
{
    Task<AepManifest> GetManifestAsync(CancellationToken cancellationToken = default);
    Task<AepHealth> GetHealthAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<string, AepCapabilityDescriptor>> GetCapabilitiesAsync(CancellationToken cancellationToken = default);
}

public interface IAepModelProvidersClient
{
    Task<IReadOnlyList<AepModelProviderDescriptor>> ListModelProvidersAsync(CancellationToken cancellationToken = default);
    AepModelProviderClient CreateModelProvider(string providerId);
}

public sealed class AepClient(HttpClient httpClient) : IAepClient, IAepModelProvidersClient
{
    public Task<AepManifest> GetManifestAsync(CancellationToken cancellationToken = default) => DiscoverAsync(cancellationToken);

    public async Task<AepManifest> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Get, AepProtocol.DiscoveryPath, null, cancellationToken);
        var descriptor = await ReadAsync<AepManifest>(response, cancellationToken);
        if (!string.Equals(descriptor.ProtocolVersion, AepProtocol.Version, StringComparison.Ordinal))
            throw new AepProtocolException("protocol_incompatible", $"The extension uses AEP {descriptor.ProtocolVersion}; this client supports AEP {AepProtocol.Version}.", response.StatusCode);
        return descriptor;
    }

    public async Task<AepHealth> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Get, AepProtocol.HealthPath, null, cancellationToken);
        return await ReadAsync<AepHealth>(response, cancellationToken);
    }

    public async Task<IReadOnlyDictionary<string, AepCapabilityDescriptor>> GetCapabilitiesAsync(CancellationToken cancellationToken = default) =>
        (await DiscoverAsync(cancellationToken)).Capabilities;

    public async Task<IReadOnlyList<AepModelProviderDescriptor>> ListModelProvidersAsync(CancellationToken cancellationToken = default)
    {
        _ = await DiscoverAsync(cancellationToken);
        using var response = await SendAsync(HttpMethod.Get, AepProtocol.ModelProvidersPath, null, cancellationToken);
        return await ReadAsync<AepModelProviderDescriptor[]>(response, cancellationToken);
    }

    public AepModelProviderClient CreateModelProvider(string providerId) => new(this, providerId);

    internal async Task<AepChatResponse> ChatAsync(string providerId, AepChatRequest request, CancellationToken cancellationToken)
    {
        _ = await DiscoverAsync(cancellationToken);
        using var response = await SendAsync(HttpMethod.Post, $"{AepProtocol.ModelProvidersPath}/{Uri.EscapeDataString(providerId)}/chat", request, cancellationToken);
        return await ReadAsync<AepChatResponse>(response, cancellationToken);
    }

    internal async Task<IReadOnlyList<AepModelDescriptor>> ListModelsAsync(string providerId, CancellationToken cancellationToken)
    {
        _ = await DiscoverAsync(cancellationToken);
        using var response = await SendAsync(HttpMethod.Get, $"{AepProtocol.ModelProvidersPath}/{Uri.EscapeDataString(providerId)}/models", null, cancellationToken);
        return await ReadAsync<AepModelDescriptor[]>(response, cancellationToken);
    }

    internal async Task<AepProviderHealth> GetHealthAsync(string providerId, CancellationToken cancellationToken)
    {
        _ = await DiscoverAsync(cancellationToken);
        using var response = await SendAsync(HttpMethod.Get, $"{AepProtocol.ModelProvidersPath}/{Uri.EscapeDataString(providerId)}/health", null, cancellationToken);
        return await ReadAsync<AepProviderHealth>(response, cancellationToken);
    }

    internal async IAsyncEnumerable<AepChatUpdate> StreamAsync(
        string providerId,
        AepChatRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        _ = await DiscoverAsync(cancellationToken);
        using var message = new HttpRequestMessage(HttpMethod.Post, $"{AepProtocol.ModelProvidersPath}/{Uri.EscapeDataString(providerId)}/chat/stream")
        {
            Content = JsonContent.Create(request, options: AepProtocol.JsonOptions)
        };
        message.Headers.Accept.ParseAdd("text/event-stream");
        HttpResponseMessage response;
        try { response = await httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken); }
        catch (HttpRequestException exception) { throw new AepProtocolException("extension_unreachable", "The AEP extension is unreachable.", innerException: exception); }
        using (response)
        {
        await EnsureSuccessAsync(response, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var data = line[5..].TrimStart();
            if (data.Length == 0) continue;
            yield return JsonSerializer.Deserialize<AepChatUpdate>(data, AepProtocol.JsonOptions)
                ?? throw new AepProtocolException("invalid_response", "The extension returned an empty streaming update.");
        }
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body, options: AepProtocol.JsonOptions);
        HttpResponseMessage response;
        try { response = await httpClient.SendAsync(request, cancellationToken); }
        catch (HttpRequestException exception) { throw new AepProtocolException("extension_unreachable", "The AEP extension is unreachable.", innerException: exception); }
        await EnsureSuccessAsync(response, cancellationToken);
        return response;
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken) =>
        await response.Content.ReadFromJsonAsync<T>(AepProtocol.JsonOptions, cancellationToken)
        ?? throw new AepProtocolException("invalid_response", "The extension returned an empty response.", response.StatusCode);

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        AepErrorResponse? error = null;
        try { error = await response.Content.ReadFromJsonAsync<AepErrorResponse>(AepProtocol.JsonOptions, cancellationToken); }
        catch (JsonException) { }
        throw new AepProtocolException(
            error?.Error.Code ?? "extension_request_failed",
            error?.Error.Message ?? $"The AEP extension returned HTTP {(int)response.StatusCode}.",
            response.StatusCode);
    }
}

public sealed class AepModelProviderClient(AepClient client, string providerId)
{
    public Task<AepProviderHealth> GetHealthAsync(CancellationToken cancellationToken = default) =>
        client.GetHealthAsync(providerId, cancellationToken);

    public Task<IReadOnlyList<AepModelDescriptor>> ListModelsAsync(CancellationToken cancellationToken = default) =>
        client.ListModelsAsync(providerId, cancellationToken);

    public Task<AepChatResponse> ChatAsync(AepChatRequest request, CancellationToken cancellationToken = default) =>
        client.ChatAsync(providerId, request, cancellationToken);

    public IAsyncEnumerable<AepChatUpdate> ChatStreamingAsync(AepChatRequest request, CancellationToken cancellationToken = default) =>
        client.StreamAsync(providerId, request, cancellationToken);
}

public sealed class AepProtocolException(string code, string message, HttpStatusCode? statusCode = null, Exception? innerException = null)
    : Exception(message, innerException)
{
    public string Code { get; } = code;
    public HttpStatusCode? StatusCode { get; } = statusCode;
}
