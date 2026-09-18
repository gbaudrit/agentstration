using System.Net;
using System.Text.Json;
using Agentstration.Aep.Abstractions;
using Agentstration.Aep.AspNetCore;

namespace Agentstration.Extensions.Foundry;

public sealed class FoundryAepModelProvider(
    HttpClient httpClient,
    FoundryExtensionOptions options,
    FoundryRequestAuthenticator authenticator) : IAepModelProvider
{
    public AepModelProviderDescriptor Descriptor { get; } = new(
        "microsoft-foundry",
        "Microsoft Foundry",
        new AepModelProviderCapabilities(
            Chat: true,
            Streaming: true,
            Tools: true,
            Thinking: false,
            StructuredOutput: false,
            Vision: false,
            ModelDiscovery: true));

    public Task<AepChatResponse> ChatAsync(AepChatRequest request, CancellationToken cancellationToken) =>
        FoundryChatCompletion.ExecuteAsync(httpClient, options, authenticator, request, cancellationToken);

    public IAsyncEnumerable<AepChatUpdate> ChatStreamingAsync(
        AepChatRequest request,
        CancellationToken cancellationToken) =>
        FoundryChatStream.ExecuteAsync(httpClient, options, authenticator, request, cancellationToken);

    public async Task<IReadOnlyList<AepModelDescriptor>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        var models = new Dictionary<string, AepModelDescriptor>(StringComparer.Ordinal);
        var current = options.DeploymentsEndpoint();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        for (var page = 0; ; page++)
        {
            if (page >= options.MaximumDiscoveryPages)
                throw new AepServerException("discovery_limit", "Foundry deployment discovery exceeded its page limit.");
            ValidatePageUri(current);
            if (!visited.Add(current.AbsoluteUri))
                throw new AepServerException("discovery_invalid", "Foundry deployment pagination contains a cycle.");

            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.RequestTimeout);
            HttpResponseMessage response;
            try
            {
                await authenticator.ApplyAsync(request, timeout.Token);
                response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new AepServerException("provider_timeout", "Foundry deployment discovery timed out.");
            }
            catch (HttpRequestException exception)
            {
                throw new AepServerException("provider_unavailable", "Foundry deployment discovery is unavailable.", innerException: exception);
            }
            using (response)
            {
                EnsureSuccess(response);
                if (!string.Equals(response.Content.Headers.ContentType?.MediaType, "application/json", StringComparison.OrdinalIgnoreCase))
                    throw new AepServerException("discovery_invalid", "Foundry deployment discovery requires a JSON response.");
                byte[] bytes;
                try { bytes = await ReadBoundedAsync(response.Content, options.MaximumDiscoveryResponseBytes, timeout.Token); }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new AepServerException("provider_timeout", "Foundry deployment discovery timed out.");
                }
                catch (HttpRequestException exception)
                {
                    throw new AepServerException("provider_unavailable", "Foundry deployment discovery is unavailable.", innerException: exception);
                }
                JsonDocument document;
                try { document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 }); }
                catch (JsonException exception)
                {
                    throw new AepServerException("discovery_invalid", "Foundry returned an invalid deployment document.", innerException: exception);
                }
                using (document)
                {
                    var root = document.RootElement;
                    if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("value", out var values)
                        || values.ValueKind != JsonValueKind.Array)
                        throw new AepServerException("discovery_invalid", "Foundry returned an invalid deployment list.");
                    foreach (var item in values.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.Object || GetString(item, "type") != "ModelDeployment") continue;
                        var name = GetString(item, "name");
                        if (string.IsNullOrWhiteSpace(name) || name.Length > 256) continue;
                        if (!HasChatCapability(item)) continue;
                        if (models.Count >= options.MaximumDiscoveredModels)
                            throw new AepServerException("discovery_limit", "Foundry deployment discovery exceeded its model limit.");
                        if (models.ContainsKey(name))
                            throw new AepServerException("discovery_invalid", "Foundry returned a duplicate deployment name.");
                        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
                        AddMetadata(item, metadata, "modelPublisher", "publisher");
                        AddMetadata(item, metadata, "modelName", "model");
                        AddMetadata(item, metadata, "modelVersion", "version");
                        var capabilities = new List<string> { "chat", "streaming" };
                        if (HasTrueCapability(item, "toolCalling", "tool_calls", "functionCalling"))
                            capabilities.Add("tools");
                        models.Add(name, new AepModelDescriptor(name, name, capabilities, metadata));
                    }
                    if (!root.TryGetProperty("nextLink", out var nextLink) || nextLink.ValueKind == JsonValueKind.Null)
                        return models.Values.ToArray();
                    if (nextLink.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(nextLink.GetString())
                        || !Uri.TryCreate(current, nextLink.GetString(), out var next))
                        throw new AepServerException("discovery_invalid", "Foundry returned an invalid deployment continuation.");
                    current = next;
                }
            }
        }
    }

    public async Task<AepProviderHealth> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _ = await ListModelsAsync(cancellationToken);
            return new AepProviderHealth("available");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (AepServerException exception) { return new AepProviderHealth("unavailable", exception.Code); }
    }

    private void ValidatePageUri(Uri uri)
    {
        var first = options.DeploymentsEndpoint();
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps
            || !string.Equals(uri.IdnHost, first.IdnHost, StringComparison.OrdinalIgnoreCase)
            || uri.Port != first.Port || uri.AbsolutePath != first.AbsolutePath
            || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0 || uri.Query.Length > 2048)
            throw new AepServerException("discovery_origin_invalid", "Foundry deployment continuation must remain on the configured endpoint.");
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, int maximumBytes, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > maximumBytes)
            throw new AepServerException("discovery_limit", "Foundry deployment response exceeded its size limit.");
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken);
            if (read == 0) return buffer.ToArray();
            if (buffer.Length + read > maximumBytes)
                throw new AepServerException("discovery_limit", "Foundry deployment response exceeded its size limit.");
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }
    }

    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        if ((int)response.StatusCode is >= 300 and < 400)
            throw new AepServerException("provider_redirect_denied", "Foundry redirected a credential-bearing request.");
        var code = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "authentication_failed",
            HttpStatusCode.Forbidden => "authorization_failed",
            HttpStatusCode.TooManyRequests => "rate_limited",
            HttpStatusCode.NotFound => "project_unavailable",
            _ => "provider_unavailable"
        };
        throw new AepServerException(code, $"Foundry deployment discovery returned HTTP {(int)response.StatusCode}.");
    }

    private static bool HasChatCapability(JsonElement item) =>
        HasTrueCapability(item, "chat", "chatCompletion", "chatCompletions", "chat_completion", "chat_completions");

    private static bool HasTrueCapability(JsonElement item, params string[] names)
    {
        if (!item.TryGetProperty("capabilities", out var capabilities) || capabilities.ValueKind != JsonValueKind.Object) return false;
        foreach (var capability in capabilities.EnumerateObject())
        {
            if (!names.Contains(capability.Name, StringComparer.OrdinalIgnoreCase)) continue;
            if (capability.Value.ValueKind == JsonValueKind.True
                || capability.Value.ValueKind == JsonValueKind.String && string.Equals(capability.Value.GetString(), "true", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string? GetString(JsonElement item, string property) =>
        item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static void AddMetadata(JsonElement item, IDictionary<string, string> metadata, string property, string key)
    {
        var value = GetString(item, property);
        if (!string.IsNullOrWhiteSpace(value) && value.Length <= 128 && !value.Any(char.IsControl))
            metadata[key] = value;
    }
}
