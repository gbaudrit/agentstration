using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentstration.ConnectedServices;
using Agentstration.ConnectedServices.Contracts;
using Agentstration.DataSources;
using Agentstration.Infrastructure.Knowledge;
using Agentstration.Resources;

namespace Agentstration.Infrastructure.ConnectedServices;

// The URL is a sample of the request shape, never a source of acquisition values.
internal static class BuiltinHttpOperation
{
    internal const string TypeName = "http-get";
    internal const string Version = "1.0.0";
    internal const string DiscoveryFlowName = "http-operation-discovery-builtin";
    internal const string AcquisitionFlowName = "http-operation-acquisition-builtin";
    internal const string ProviderOperationId = "builtin.http.get";
    internal const int MaximumUrlLength = 2048;
    internal const int MaximumParameters = 64;
    internal const int MaximumParameterNameLength = 128;

    internal static JsonElement DefinitionSchema { get; } = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            method = new { type = "string", @enum = new[] { "GET" } },
            endpoint = new { type = "string", maxLength = MaximumUrlLength },
            queryParameters = new { type = "array", maxItems = MaximumParameters,
                items = new { type = "string", minLength = 1, maxLength = MaximumParameterNameLength } },
            readOnly = new { type = "boolean" },
            inputSchema = new { type = "object" },
            outputSchema = new { type = "object" }
        },
        required = new[] { "method", "endpoint", "queryParameters", "readOnly", "inputSchema", "outputSchema" },
        additionalProperties = false
    });

    internal static JsonElement OutputSchema { get; } = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            mediaType = new { type = "string" },
            content = new { type = "string" }
        },
        required = new[] { "mediaType", "content" },
        additionalProperties = false
    });

    internal static bool Owns(ConnectedServiceTypeResource type, ConnectedServiceTypeVersionDescriptor version) =>
        IsCore(type, ConnectedServicePlatformResourceProvisioner.HttpTypeName)
        && version.Version == ConnectedServicePlatformResourceProvisioner.HttpTypeVersion;

    internal static bool IsCore(Resource resource, string name) => resource.Name == name
        && resource.Namespace == ResourceNamespace.Default && resource.ScopeRef == ResourceScopeRef.Instance
        && resource.Governance?.ManagedBy?.Kind == ResourceManagementAuthorityKind.Core
        && resource.Governance.ManagedBy.Reference == "connected-services.http"
        && resource.Governance.Origin.Kind == ResourceOriginKind.BuiltIn;

    internal static ConnectedServiceOperationCandidate Discover(string sample,
        ConnectedServiceOperationTypeReference type)
    {
        if (sample.Length > MaximumUrlLength || sample.Any(char.IsControl)
            || !Uri.TryCreate(sample, UriKind.Absolute, out var url)
            || !url.IsWellFormedOriginalString() || url.Scheme is not ("http" or "https") || string.IsNullOrEmpty(url.Host)
            || !string.IsNullOrEmpty(url.UserInfo) || !string.IsNullOrEmpty(url.Fragment))
            throw Invalid("http_url_invalid");

        var names = new SortedSet<string>(StringComparer.Ordinal);
        if (url.Query.Length > 1)
        {
            foreach (var field in url.Query[1..].Split('&'))
            {
                var encoded = field.Split('=', 2)[0];
                for (var index = 0; index < encoded.Length; index++)
                    if (encoded[index] == '%')
                    {
                        if (index + 2 >= encoded.Length || !Uri.IsHexDigit(encoded[index + 1]) || !Uri.IsHexDigit(encoded[index + 2]))
                            throw Invalid("http_query_invalid");
                        index += 2;
                    }
                string name;
                try { name = System.Web.HttpUtility.UrlDecode(encoded, new UTF8Encoding(false, true)); }
                catch (DecoderFallbackException) { throw Invalid("http_query_invalid"); }
                if (string.IsNullOrWhiteSpace(name) || name.Length > MaximumParameterNameLength || name.Any(char.IsControl))
                    throw Invalid("http_query_invalid");
                if (!names.Add(name)) throw Invalid("http_query_repeated_parameter");
                if (names.Count > MaximumParameters) throw Invalid("http_query_too_many_parameters");
            }
        }
        var endpoint = new UriBuilder(url) { Query = "", Fragment = "" }.Uri.AbsoluteUri;
        var display = "GET " + url.AbsolutePath;
        return new()
        {
            ExternalId = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("GET\n" + endpoint))),
            DisplayName = display[..Math.Min(200, display.Length)],
            Type = type,
            Definition = JsonSerializer.SerializeToElement(new
            {
                method = "GET", endpoint, queryParameters = names.ToArray(), readOnly = true,
                inputSchema = new
                {
                    type = "object",
                    properties = names.ToDictionary(name => name, _ => new { type = "string", maxLength = MaximumUrlLength }),
                    additionalProperties = false
                },
                outputSchema = OutputSchema
            })
        };
    }

    internal static Uri RequestUri(JsonElement definition, JsonElement parameters)
    {
        DataSourceDatasetCompositionService.ValidateValue(definition.GetProperty("inputSchema"), parameters);
        if (parameters.EnumerateObject().Select(item => item.Name).Distinct(StringComparer.Ordinal).Count() != parameters.EnumerateObject().Count())
            throw Invalid("http_query_repeated_parameter");
        var query = string.Join('&', parameters.EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal)
            .Select(item => Uri.EscapeDataString(item.Name) + "=" + Uri.EscapeDataString(item.Value.GetString()!)));
        var url = new UriBuilder(definition.GetProperty("endpoint").GetString()!) { Query = query }.Uri;
        if (url.AbsoluteUri.Length > MaximumUrlLength) throw Invalid("http_url_too_long");
        return url;
    }

    private static ConnectedServiceOperationDiscoveryException Invalid(string code) =>
        new(code, "The HTTP request URL or query parameter structure is invalid.");
}

internal sealed class BuiltinHttpOperationExecutor(IConnectedServiceResolver resolver, IHttpContentFetcher fetcher)
{
    private static readonly IReadOnlySet<string> AllowedMediaTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "application/json", "application/xml", "text/xml", "application/rss+xml", "application/atom+xml",
        "text/plain", "text/html", "text/markdown"
    };

    internal async Task<JsonElement> DiscoverAsync(ConnectedServiceResource service,
        ConnectedServiceTypeVersionDescriptor version, ConnectedServiceResolutionContext context,
        CancellationToken cancellationToken)
    {
        using var connection = await ResolveAsync(service, context, cancellationToken);
        var candidate = BuiltinHttpOperation.Discover(Sample(connection), version.OperationDiscovery!.ProducedTypes.Single());
        return JsonSerializer.SerializeToElement(new ConnectedServiceOperationDiscoveryManifest
            { Operations = [candidate] }, JsonSerializerOptions.Web);
    }

    internal async Task<JsonElement> AcquireAsync(ConnectedServiceResource service,
        ConnectedServiceOperationResource operation, JsonElement parameters, ConnectedServiceResolutionContext context,
        CancellationToken cancellationToken)
    {
        using var connection = await ResolveAsync(service, context, cancellationToken);
        var expected = BuiltinHttpOperation.Discover(Sample(connection), operation.Definition.Type);
        if (expected.ExternalId != operation.Definition.ExternalId
            || !JsonElement.DeepEquals(expected.Definition, operation.Definition.Definition))
            throw new DataSourceAcquisitionException("http_operation_changed", "The HTTP operation no longer matches its connection. Rediscover and reconcile explicitly.");
        var url = BuiltinHttpOperation.RequestUri(expected.Definition, parameters);
        // Redirects cannot change the selected service authority. Reuse the existing public-network,
        // timeout, media-type and bounded-body transport rather than introducing another HTTP client.
        var response = await fetcher.FetchAsync(new(url, 48 * 1024, 0, TimeSpan.FromSeconds(20), AllowedMediaTypes), cancellationToken);
        string content;
        try { content = new UTF8Encoding(false, true).GetString(response.Content); }
        catch (DecoderFallbackException)
        { throw new DataSourceAcquisitionException("http_encoding_invalid", "The HTTP response must contain UTF-8 text."); }
        var result = JsonSerializer.SerializeToElement(new { mediaType = response.MediaType, content });
        DataSourceDatasetCompositionService.ValidateValue(BuiltinHttpOperation.OutputSchema, result);
        return result;
    }

    private async Task<ResolvedConnectedService> ResolveAsync(ConnectedServiceResource service,
        ConnectedServiceResolutionContext context, CancellationToken cancellationToken)
    {
        var resolved = await resolver.ResolveAsync(new(service.Address, service.ScopeRef!.Value), context, cancellationToken)
            ?? throw new ConnectedServiceOperationDiscoveryException("connected_service_unavailable", "The HTTP Connected Service is unavailable.");
        if (resolved.Type == service.Definition.Type) return resolved;
        resolved.Dispose();
        throw new ConnectedServiceOperationDiscoveryException("connected_service_revision_changed", "The HTTP Connected Service revision changed.");
    }

    private static string Sample(ResolvedConnectedService connection) =>
        connection.Inputs.Single(item => item.Id == "baseUrl").Value?.GetString()
        ?? throw new ConnectedServiceOperationDiscoveryException("http_url_invalid", "An HTTP request URL is required.");
}
