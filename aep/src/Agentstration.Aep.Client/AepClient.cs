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
    Task<AepConfigurationCatalog> GetConfigurationAsync(CancellationToken cancellationToken = default);
    Task<AepOptionMigrationResponse> MigrateOptionsAsync(AepOptionMigrationRequest request, CancellationToken cancellationToken = default);
}

public interface IAepModelProvidersClient
{
    Task<IReadOnlyList<AepModelProviderDescriptor>> ListModelProvidersAsync(CancellationToken cancellationToken = default);
    AepModelProviderClient CreateModelProvider(string providerId);
}

public interface IAepSourceProvidersClient
{
    Task<IReadOnlyList<AepSourceProviderDescriptor>> ListSourceProvidersAsync(CancellationToken cancellationToken = default);
    AepSourceProviderClient CreateSourceProvider(string providerId);
}

public sealed class AepClient(HttpClient httpClient) : IAepClient, IAepModelProvidersClient, IAepSourceProvidersClient
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

    public async Task<AepConfigurationCatalog> GetConfigurationAsync(CancellationToken cancellationToken = default)
    {
        var manifest = await DiscoverAsync(cancellationToken);
        if (!manifest.Capabilities.TryGetValue(AepCapabilityNames.Configuration, out var capability))
            return new AepConfigurationCatalog([]);
        var endpoint = string.IsNullOrWhiteSpace(capability.Endpoint) ? AepProtocol.ConfigurationPath : capability.Endpoint;
        using var response = await SendAsync(HttpMethod.Get, endpoint, null, cancellationToken);
        return await ReadAsync<AepConfigurationCatalog>(response, cancellationToken);
    }

    public async Task<AepOptionMigrationResponse> MigrateOptionsAsync(
        AepOptionMigrationRequest request,
        CancellationToken cancellationToken = default)
    {
        var manifest = await DiscoverAsync(cancellationToken);
        if (!manifest.Capabilities.ContainsKey(AepCapabilityNames.Configuration))
            throw new AepProtocolException("configuration_unsupported", "The extension does not publish configuration contracts.");
        using var response = await SendAsync(HttpMethod.Post, AepProtocol.ConfigurationMigrationPath, request, cancellationToken);
        return await ReadAsync<AepOptionMigrationResponse>(response, cancellationToken);
    }

    public async Task<IReadOnlyList<AepModelProviderDescriptor>> ListModelProvidersAsync(CancellationToken cancellationToken = default)
    {
        _ = await DiscoverAsync(cancellationToken);
        using var response = await SendAsync(HttpMethod.Get, AepProtocol.ModelProvidersPath, null, cancellationToken);
        return await ReadAsync<AepModelProviderDescriptor[]>(response, cancellationToken);
    }

    public AepModelProviderClient CreateModelProvider(string providerId) => new(this, providerId);

    public async Task<IReadOnlyList<AepSourceProviderDescriptor>> ListSourceProvidersAsync(CancellationToken cancellationToken = default)
    {
        var manifest = await DiscoverAsync(cancellationToken);
        if (!manifest.Capabilities.ContainsKey(AepCapabilityNames.SourceProvider)) return [];
        using var response = await SendAsync(HttpMethod.Get, AepProtocol.SourceProvidersPath, null, cancellationToken);
        return await ReadAsync<AepSourceProviderDescriptor[]>(response, cancellationToken);
    }

    public AepSourceProviderClient CreateSourceProvider(string providerId) => new(this, providerId);

    internal async Task<AepSourceResolveResponse> ResolveSourceAsync(
        string providerId,
        AepSourceResolveRequest request,
        CancellationToken cancellationToken)
    {
        await RequireSourceProviderAsync(providerId, cancellationToken);
        using var response = await SendAsync(HttpMethod.Post, $"{AepProtocol.SourceProvidersPath}/{Uri.EscapeDataString(providerId)}/resolve", request, cancellationToken);
        var result = await ReadAsync<AepSourceResolveResponse>(response, cancellationToken);
        if (string.IsNullOrWhiteSpace(result.Revision)
            || result.Integrity is null
            || string.IsNullOrWhiteSpace(result.Integrity.Algorithm)
            || string.IsNullOrWhiteSpace(result.Integrity.Digest))
            throw new AepProtocolException("invalid_source_response", "The extension returned an invalid immutable source revision.");
        return result;
    }

    internal async Task<AepSourceMaterializeResponse> MaterializeSourceAsync(
        string providerId,
        AepSourceMaterializeRequest request,
        CancellationToken cancellationToken)
    {
        await RequireSourceProviderAsync(providerId, cancellationToken);
        if (request.Limits.MaxArchiveBytes <= 0 || request.Limits.MaxEntries <= 0 || request.Limits.MaxExpandedBytes <= 0 || request.Limits.TimeoutSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(request), "All source materialization limits must be positive.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(request.Limits.TimeoutSeconds).Add(TimeSpan.FromSeconds(5)));
        HttpResponseMessage response;
        try
        {
            response = await SendAsync(HttpMethod.Post, $"{AepProtocol.SourceProvidersPath}/{Uri.EscapeDataString(providerId)}/materialize", request, timeout.Token);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            throw new AepProtocolException("source_provider_timeout", "Source materialization exceeded its time limit.", innerException: exception);
        }
        using (response)
        {
            var wireLimit = request.Limits.MaxArchiveBytes > (long.MaxValue - 65_536) / 2
                ? long.MaxValue
                : request.Limits.MaxArchiveBytes * 2 + 65_536;
            try { await response.Content.LoadIntoBufferAsync(wireLimit, timeout.Token); }
            catch (HttpRequestException exception)
            {
                throw new AepProtocolException("source_archive_too_large", "The extension response exceeds the requested archive limit.", innerException: exception);
            }
            var result = await ReadAsync<AepSourceMaterializeResponse>(response, timeout.Token);
            if (result.Archive is null || result.Archive.Content is null || result.Archive.Integrity is null)
                throw new AepProtocolException("invalid_source_response", "The extension returned an incomplete source archive.");
            if (!string.Equals(result.Revision, request.Revision, StringComparison.Ordinal))
                throw new AepProtocolException("source_revision_mismatch", "The extension materialized a different source revision.");
            if (result.Archive.Content.LongLength > request.Limits.MaxArchiveBytes
                || result.Archive.EntryCount < 0
                || result.Archive.EntryCount > request.Limits.MaxEntries
                || result.Archive.ExpandedBytes < 0
                || result.Archive.ExpandedBytes > request.Limits.MaxExpandedBytes)
                throw new AepProtocolException("source_limits_exceeded", "The extension returned a source archive outside the requested limits.");
            var actualDigest = AepContentIntegrity.Sha256(result.Archive.Content);
            if (!string.Equals(actualDigest.Algorithm, result.Archive.Integrity.Algorithm, StringComparison.Ordinal)
                || !string.Equals(actualDigest.Digest, result.Archive.Integrity.Digest, StringComparison.OrdinalIgnoreCase))
                throw new AepProtocolException("source_integrity_mismatch", "The extension returned a source archive with invalid integrity metadata.");
            return result;
        }
    }

    private async Task RequireSourceProviderAsync(string providerId, CancellationToken cancellationToken)
    {
        var manifest = await DiscoverAsync(cancellationToken);
        if (!manifest.Capabilities.ContainsKey(AepCapabilityNames.SourceProvider)
            || !(manifest.Contributions.SourceProviders ?? []).Any(value => string.Equals(value.Id, providerId, StringComparison.OrdinalIgnoreCase)))
            throw new AepProtocolException("source_provider_unavailable", $"Source provider '{providerId}' is not advertised by the extension.");
    }

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
                var update = JsonSerializer.Deserialize<AepChatUpdate>(data, AepProtocol.JsonOptions)
                    ?? throw new AepProtocolException("invalid_response", "The extension returned an empty streaming update.");
                yield return update;
                if (update.FinishReason is not null) yield break;
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

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<T>(AepProtocol.JsonOptions, cancellationToken)
                ?? throw new AepProtocolException("invalid_response", "The extension returned an empty response.", response.StatusCode);
        }
        catch (JsonException exception)
        {
            throw new AepProtocolException("invalid_response", "The extension returned malformed JSON.", response.StatusCode, exception);
        }
    }

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

public sealed class AepSourceProviderClient(AepClient client, string providerId)
{
    public Task<AepSourceResolveResponse> ResolveAsync(
        AepSourceResolveRequest request,
        CancellationToken cancellationToken = default) =>
        client.ResolveSourceAsync(providerId, request, cancellationToken);

    public Task<AepSourceMaterializeResponse> MaterializeAsync(
        AepSourceMaterializeRequest request,
        CancellationToken cancellationToken = default) =>
        client.MaterializeSourceAsync(providerId, request, cancellationToken);
}

public sealed class AepProtocolException(string code, string message, HttpStatusCode? statusCode = null, Exception? innerException = null)
    : Exception(message, innerException)
{
    public string Code { get; } = code;
    public HttpStatusCode? StatusCode { get; } = statusCode;
}
