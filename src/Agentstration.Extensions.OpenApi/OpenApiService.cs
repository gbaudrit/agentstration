using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace Agentstration.Extensions.OpenApi;

public sealed class OpenApiService(
    HttpClient client,
    OpenApiDestinationPolicy destinations,
    OpenApiContractParser parser,
    IOptions<OpenApiOptions> options,
    ILogger<OpenApiService> logger)
{
    private readonly OpenApiOptions options = options.Value;

    public async Task<OpenApiDiscoveryResult> DiscoverAsync(
        string origin, IReadOnlyList<string>? candidatePaths, string? correlationId, CancellationToken cancellationToken)
    {
        var id = Correlation(correlationId);
        var originUri = await destinations.ValidateAsync(origin, cancellationToken);
        var candidates = Candidates(originUri, candidatePaths);
        var contracts = new List<OpenApiContractDescriptor>();
        var diagnostics = new List<OpenApiDiagnostic>();
        foreach (var candidate in candidates)
        {
            try
            {
                var fetched = await FetchAsync(candidate, options.MaximumDocumentBytes, options.AllowedContractMediaTypes,
                    "openapi_contract", cancellationToken);
                if (fetched is null) continue;
                contracts.Add(parser.Parse(Encoding.UTF8.GetString(fetched.Value.Content), fetched.Value.MediaType, candidate).Descriptor);
            }
            catch (OpenApiExtensionException exception)
            {
                diagnostics.Add(new(candidate.AbsoluteUri, exception.Code, SafeMessage(exception)));
            }
        }
        var choices = contracts.SelectMany(Choices).Take(options.MaximumOperations + contracts.Count).ToArray();
        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation("OpenAPI discovery {CorrelationId} checked {CandidateCount} candidates and found {ContractCount} contracts with {ChoiceCount} choices.",
                id, candidates.Count, contracts.Count, choices.Length);
        return new(id, contracts, choices, diagnostics);
    }

    public async Task<OpenApiInvocationResult> InvokeAsync(
        OpenApiOperationSelector selector,
        JsonElement arguments,
        OpenApiFixedBindings? fixedBindings,
        string? credentialId,
        int? maximumPages,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        var id = Correlation(correlationId);
        if (arguments.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null or JsonValueKind.Undefined))
            throw Error("openapi_arguments_invalid", "Invocation arguments must be a JSON object.");
        var contractUri = await destinations.ValidateAsync(selector.ContractUri, cancellationToken);
        var fetched = await FetchAsync(contractUri, options.MaximumDocumentBytes, options.AllowedContractMediaTypes,
            "openapi_contract", cancellationToken)
            ?? throw Error("openapi_contract_unavailable", "The selected OpenAPI contract is unavailable.");
        var digest = Convert.ToHexStringLower(SHA256.HashData(fetched.Content));
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(digest), Encoding.ASCII.GetBytes(selector.ContractDigest.ToLowerInvariant())))
            throw Error("openapi_contract_digest_mismatch", "The current contract does not match the discovered contract digest.");
        var contract = parser.Parse(Encoding.UTF8.GetString(fetched.Content), fetched.MediaType, contractUri);
        var operation = contract.Descriptor.Operations.SingleOrDefault(value =>
            value.Eligible && string.Equals(value.OperationId, selector.OperationId, StringComparison.Ordinal));
        if (operation is null) throw Error("openapi_operation_not_selectable", "The selected operation is absent, ambiguous, or ineligible.");
        if (!string.Equals(operation.Method, selector.Method, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(operation.Path, selector.Path, StringComparison.Ordinal))
            throw Error("openapi_selector_mismatch", "The selector method or path does not match its operationId.");
        if (!options.AllowedMethods.Contains(operation.Method, StringComparer.OrdinalIgnoreCase))
            throw Error("openapi_method_denied", $"HTTP method '{operation.Method}' is not allowed.");
        if (!contract.Descriptor.Servers.Contains(selector.Server, StringComparer.Ordinal))
            throw Error("openapi_server_not_discovered", "The selected server was not present in the discovered contract.");

        var server = await destinations.ValidateAsync(selector.Server, cancellationToken);
        var requestUri = BuildUri(server, operation.Path, contract.Document, operation, arguments, fixedBindings, out var headers, out var body);
        var pageLimit = Math.Clamp(maximumPages ?? 1, 1, options.MaximumPages);
        var pages = new List<(int Status, string MediaType, byte[] Content)>();
        for (var page = 0; page < pageLimit; page++)
        {
            using var request = new HttpRequestMessage(new HttpMethod(operation.Method), requestUri);
            request.Headers.TryAddWithoutValidation("X-Correlation-ID", id);
            foreach (var header in headers) request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            ApplyCredential(request, credentialId);
            if (body is { } content) request.Content = JsonContent.Create(content);
            using var response = await SendAsync(request, cancellationToken);
            await EnsureSuccessAsync(response);
            var responseMediaType = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? "application/octet-stream";
            if (!options.AllowedResponseMediaTypes.Contains(responseMediaType, StringComparer.OrdinalIgnoreCase))
                throw Error("openapi_response_content_type_denied", $"Response content type '{responseMediaType}' is not allowed.");
            var responseBytes = await ReadBoundedAsync(response.Content, options.MaximumResponseBytes, "openapi_response_too_large", cancellationToken);
            pages.Add(((int)response.StatusCode, responseMediaType, responseBytes));
            var next = NextLink(response);
            if (next is null) break;
            if (page + 1 >= pageLimit) throw Error("openapi_pagination_limit", $"The response exceeded the configured {pageLimit}-page limit.");
            var validatedNext = await destinations.ValidateAsync(new Uri(requestUri, next).AbsoluteUri, cancellationToken);
            if (!OpenApiDestinationPolicy.SameOrigin(server, validatedNext))
                throw Error("openapi_pagination_destination_denied", "Pagination cannot leave the selected server origin.");
            requestUri = validatedNext;
        }

        var output = CombinePages(pages);
        var outputDigest = Convert.ToHexStringLower(SHA256.HashData(output.Content));
        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation("OpenAPI invocation {CorrelationId} completed operation {OperationId} with status {StatusCode} and {PageCount} page(s).",
                id, selector.OperationId, output.Status, pages.Count);
        return new(id, output.Status, output.MediaType, Convert.ToBase64String(output.Content), output.Content.LongLength, outputDigest, pages.Count);
    }

    private IReadOnlyList<Uri> Candidates(Uri origin, IReadOnlyList<string>? candidatePaths)
    {
        var hints = candidatePaths?.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal).ToArray() ?? [];
        if (hints.Length > options.MaximumCandidatePaths)
            throw Error("openapi_candidate_limit", $"At most {options.MaximumCandidatePaths} discovery candidates are allowed.");
        if (hints.Length == 0) return [origin];
        var result = new List<Uri>(hints.Length);
        foreach (var hint in hints)
        {
            if (hint.Length > 512 || Uri.TryCreate(hint, UriKind.Absolute, out _))
                throw Error("openapi_candidate_invalid", "Discovery candidates must be bounded same-origin relative paths.");
            var resolved = new Uri(origin, hint);
            if (!OpenApiDestinationPolicy.SameOrigin(origin, resolved))
                throw Error("openapi_candidate_origin_denied", "Discovery candidates cannot leave the configured origin.");
            result.Add(resolved);
        }
        return result;
    }

    private static IEnumerable<OpenApiDiscoveryChoice> Choices(OpenApiContractDescriptor contract)
    {
        var contractId = $"contract-{contract.Sha256[..16]}";
        yield return new(contractId, contract.Title ?? new Uri(contract.Uri).Host, contract.Description, null, false,
            JsonSerializer.SerializeToElement(new { contractUri = contract.Uri, contractDigest = contract.Sha256 }),
            new Dictionary<string, string> { ["kind"] = "contract", ["openapiVersion"] = contract.OpenApiVersion });
        var server = contract.Servers.First();
        foreach (var operation in contract.Operations)
        {
            var id = operation.OperationId ?? $"ineligible-{Hash($"{operation.Method}:{operation.Path}")[..16]}";
            var selector = new OpenApiOperationSelector(contract.Uri, contract.Sha256, server,
                operation.OperationId ?? string.Empty, operation.Method, operation.Path);
            yield return new(id, operation.Summary ?? operation.OperationId ?? $"{operation.Method} {operation.Path}",
                operation.Diagnostics.Count == 0 ? null : string.Join(' ', operation.Diagnostics.Select(value => value.Message)),
                contractId, operation.Eligible, JsonSerializer.SerializeToElement(selector),
                new Dictionary<string, string>
                {
                    ["kind"] = "operation", ["method"] = operation.Method, ["path"] = operation.Path,
                    ["readOnly"] = operation.ReadOnly ? "true" : "false", ["contractDigest"] = contract.Sha256
                });
        }
    }

    private Uri BuildUri(Uri server, string path, JsonObject document, OpenApiOperationDescriptor descriptor,
        JsonElement arguments, OpenApiFixedBindings? fixedBindings, out Dictionary<string, string> headers, out JsonElement? body)
    {
        var pathValues = Arguments(arguments, "path");
        var queryValues = Arguments(arguments, "query");
        var headerValues = Arguments(arguments, "headers");
        body = fixedBindings?.Body ?? Property(arguments, "body");
        ApplyFixed(pathValues, fixedBindings?.Path, "path");
        ApplyFixed(queryValues, fixedBindings?.Query, "query");
        ApplyFixed(headerValues, fixedBindings?.Header, "header");
        ValidateParameters(document, descriptor, pathValues, queryValues, headerValues, body);
        foreach (var value in pathValues)
            path = path.Replace($"{{{value.Key}}}", Uri.EscapeDataString(value.Value), StringComparison.Ordinal);
        if (path.Contains('{', StringComparison.Ordinal))
            throw Error("openapi_path_argument_required", "One or more path parameters are missing.");
        var uri = new Uri(server.AbsoluteUri.TrimEnd('/') + "/" + path.TrimStart('/'));
        if (queryValues.Count > 0)
        {
            var builder = new UriBuilder(uri);
            var existing = builder.Query.TrimStart('?');
            var query = string.Join('&', queryValues.Select(value => $"{Uri.EscapeDataString(value.Key)}={Uri.EscapeDataString(value.Value)}"));
            builder.Query = string.IsNullOrEmpty(existing) ? query : $"{existing}&{query}";
            uri = builder.Uri;
        }
        headers = headerValues;
        if (headers.Keys.Any(ReservedHeader)) throw Error("openapi_header_denied", "Host, Content-Length, and Authorization cannot be supplied as arguments or fixed bindings.");
        return uri;
    }

    private static void ValidateParameters(JsonObject document, OpenApiOperationDescriptor descriptor,
        IReadOnlyDictionary<string, string> path, IReadOnlyDictionary<string, string> query,
        IReadOnlyDictionary<string, string> headers, JsonElement? body)
    {
        if (document["paths"]?[descriptor.Path] is not JsonObject pathItem
            || pathItem[descriptor.Method.ToLowerInvariant()] is not JsonObject operation) return;
        var parameters = new List<JsonObject>();
        if (pathItem["parameters"] is JsonArray pathParameters) parameters.AddRange(pathParameters.OfType<JsonObject>());
        if (operation["parameters"] is JsonArray operationParameters) parameters.AddRange(operationParameters.OfType<JsonObject>());
        foreach (var parameter in parameters)
        {
            var name = parameter["name"]?.GetValue<string>();
            var location = parameter["in"]?.GetValue<string>();
            var required = Boolean(parameter["required"]) || location == "path";
            if (name is null || location is null || !required) continue;
            var present = location switch
            {
                "path" => path.ContainsKey(name), "query" => query.ContainsKey(name),
                "header" => headers.ContainsKey(name), _ => true
            };
            if (!present) throw Error("openapi_argument_required", $"Required {location} argument '{name}' is missing.");
        }
        if (operation["requestBody"] is JsonObject requestBody
            && Boolean(requestBody["required"])
            && body is null)
            throw Error("openapi_request_body_required", "The selected operation requires a request body.");
    }

    private static bool Boolean(JsonNode? value) => value is JsonValue scalar
        && (scalar.TryGetValue<bool>(out var boolean) && boolean
            || scalar.TryGetValue<string>(out var text) && bool.TryParse(text, out boolean) && boolean);

    private static Dictionary<string, string> Arguments(JsonElement arguments, string name)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty(name, out var values)
            || values.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return result;
        if (values.ValueKind != JsonValueKind.Object) throw Error("openapi_arguments_invalid", $"'{name}' arguments must be an object.");
        foreach (var value in values.EnumerateObject())
        {
            if (value.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                throw Error("openapi_argument_type_invalid", $"Argument '{name}.{value.Name}' must be scalar.");
            result[value.Name] = value.Value.ValueKind == JsonValueKind.String ? value.Value.GetString()! : value.Value.GetRawText();
        }
        return result;
    }

    private static void ApplyFixed(Dictionary<string, string> caller, IReadOnlyDictionary<string, string>? fixedValues, string location)
    {
        if (fixedValues is null) return;
        foreach (var value in fixedValues)
        {
            if (caller.ContainsKey(value.Key))
                throw Error("openapi_fixed_binding_override", $"Caller arguments cannot override fixed {location} binding '{value.Key}'.");
            caller[value.Key] = value.Value;
        }
    }

    private void ApplyCredential(HttpRequestMessage request, string? credentialId)
    {
        if (string.IsNullOrWhiteSpace(credentialId)) return;
        if (!options.Credentials.TryGetValue(credentialId, out var credential))
            throw Error("openapi_credential_unavailable", "The selected credential reference is unavailable.");
        if (credential.Location == "query")
        {
            var builder = new UriBuilder(request.RequestUri!);
            var value = $"{Uri.EscapeDataString(credential.Name)}={Uri.EscapeDataString(credential.Value)}";
            builder.Query = string.IsNullOrEmpty(builder.Query) ? value : $"{builder.Query.TrimStart('?')}&{value}";
            request.RequestUri = builder.Uri;
        }
        else
        {
            var value = string.Equals(credential.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase)
                ? $"Bearer {credential.Value}" : credential.Value;
            request.Headers.TryAddWithoutValidation(credential.Name, value);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
        try { return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token); }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        { throw Error("openapi_timeout", "The remote request timed out.", exception); }
        catch (HttpRequestException exception) { throw Error("openapi_remote_unavailable", "The remote service is unavailable.", exception); }
    }

    private async Task<(string MediaType, byte[] Content)?> FetchAsync(Uri uri, int maximumBytes,
        IReadOnlyList<string> allowedMediaTypes, string prefix, CancellationToken cancellationToken)
    {
        _ = await destinations.ValidateAsync(uri.AbsoluteUri, cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(response);
        var mediaType = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? GuessMediaType(uri);
        if (!allowedMediaTypes.Contains(mediaType, StringComparer.OrdinalIgnoreCase))
            throw Error($"{prefix}_content_type_denied", $"Content type '{mediaType}' is not allowed.");
        return (mediaType, await ReadBoundedAsync(response.Content, maximumBytes, $"{prefix}_too_large", cancellationToken));
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        throw response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => Error("openapi_authentication_failed", "The remote service rejected authentication."),
            HttpStatusCode.Forbidden => Error("openapi_authorization_failed", "The remote service denied access."),
            HttpStatusCode.TooManyRequests => Error("openapi_rate_limited", "The remote service rate-limited the request."),
            >= HttpStatusCode.InternalServerError => Error("openapi_remote_unavailable", "The remote service is unavailable."),
            _ => Error("openapi_remote_response_invalid", $"The remote service returned status {(int)response.StatusCode}.")
        };
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, int maximumBytes, string code, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > maximumBytes) throw Error(code, $"Content cannot exceed {maximumBytes} bytes.");
        await using var input = await content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            if (output.Length + read > maximumBytes) throw Error(code, $"Content cannot exceed {maximumBytes} bytes.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static (int Status, string MediaType, byte[] Content) CombinePages(IReadOnlyList<(int Status, string MediaType, byte[] Content)> pages)
    {
        if (pages.Count == 1) return pages[0];
        if (pages.Any(value => value.MediaType != "application/json"))
            throw Error("openapi_pagination_content_type", "Multi-page responses must use application/json.");
        try
        {
            var values = pages.Select(value => JsonSerializer.Deserialize<JsonElement>(value.Content)).ToArray();
            return (pages[^1].Status, "application/json", JsonSerializer.SerializeToUtf8Bytes(values));
        }
        catch (JsonException exception) { throw Error("openapi_response_invalid", "A paginated JSON response was malformed.", exception); }
    }

    private static string? NextLink(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Link", out var values)) return null;
        foreach (var part in string.Join(',', values).Split(','))
        {
            var sections = part.Split(';', StringSplitOptions.TrimEntries);
            if (sections.Length < 2 || !sections.Skip(1).Any(value => value.Equals("rel=\"next\"", StringComparison.OrdinalIgnoreCase))) continue;
            var target = sections[0].Trim();
            if (target.StartsWith('<') && target.EndsWith('>')) return target[1..^1];
        }
        return null;
    }

    private static JsonElement? Property(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) ? property.Clone() : null;
    private static bool ReservedHeader(string value) => value.Equals("Host", StringComparison.OrdinalIgnoreCase)
        || value.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
        || value.Equals("Authorization", StringComparison.OrdinalIgnoreCase);
    private static string GuessMediaType(Uri uri) => uri.AbsolutePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
        ? "application/json" : "application/yaml";
    private static string Correlation(string? value) => string.IsNullOrWhiteSpace(value) ? Guid.NewGuid().ToString("N") : value.Trim()[..Math.Min(value.Trim().Length, 128)];
    private static string SafeMessage(OpenApiExtensionException exception) => exception.Message[(exception.Code.Length + 2)..];
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static OpenApiExtensionException Error(string code, string message, Exception? inner = null) => new(code, message, inner);
}
