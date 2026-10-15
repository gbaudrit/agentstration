using System.ComponentModel;
using System.Text.Json;
using Agentstration.Aep.Abstractions;
using ModelContextProtocol.Server;

namespace Agentstration.Extensions.OpenApi;

[McpServerToolType]
public sealed class OpenApiTools(OpenApiService service, OpenApiContractParser parser)
{
    [McpServerTool(Name = "openapi_validate_definition", UseStructuredContent = true),
     Description("Validate an OpenAPI 3.0 or 3.1 JSON or YAML contract with the extension's bounded reference policy.")]
    public AepSchemaValidationResult ValidateDefinition(JsonElement definition, string? dialect = null)
    {
        try
        {
            var input = SchemaInput(definition, dialect);
            _ = parser.Parse(input.Content, input.ContentType, input.Origin);
            return new([]);
        }
        catch (OpenApiExtensionException exception)
        {
            return new([new("$", exception.Code, SafeMessage(exception))]);
        }
    }

    [McpServerTool(Name = "openapi_validate_instance", UseStructuredContent = true),
     Description("Validate an OpenAPI contract instance using the same bounded parser and reference policy.")]
    public AepSchemaValidationResult ValidateInstance(JsonElement definition, JsonElement instance, string? dialect = null)
    {
        _ = definition;
        return ValidateDefinition(instance, dialect);
    }

    [McpServerTool(Name = "openapi_discover", UseStructuredContent = true),
     Description("Discover bounded OpenAPI contracts from explicit same-origin candidates and return exact contract content plus ineligible or selectable operation choices.")]
    public Task<OpenApiDiscoveryResult> DiscoverAsync(
        [Description("Configured credential-free absolute HTTP(S) origin or contract URL.")] string origin,
        [Description("Explicit bounded same-origin contract candidate paths.")] string[]? candidatePaths = null,
        [Description("Optional caller correlation identifier.")] string? correlationId = null,
        CancellationToken cancellationToken = default) =>
        service.DiscoverAsync(origin, candidatePaths, correlationId, cancellationToken);

    [McpServerTool(Name = "openapi_invoke", UseStructuredContent = true),
     Description("Invoke exactly one eligible operation selected by contract digest, server, operationId, method and path.")]
    public Task<OpenApiInvocationResult> InvokeAsync(
        [Description("Opaque selector promoted from one discovery choice.")] OpenApiOperationSelector selector,
        [Description("Caller-supplied path, query, headers and body values.")] JsonElement arguments,
        [Description("Governed values that caller arguments cannot override.")] OpenApiFixedBindings? fixedBindings = null,
        [Description("Optional configured credential reference; never a raw credential.")] string? credentialId = null,
        [Description("Maximum Link-header pages to retrieve within extension policy.")] int? maximumPages = null,
        [Description("Optional caller correlation identifier.")] string? correlationId = null,
        CancellationToken cancellationToken = default) =>
        service.InvokeAsync(selector, arguments, fixedBindings, credentialId, maximumPages, correlationId, cancellationToken);

    private static (string Content, string ContentType, Uri Origin) SchemaInput(JsonElement value, string? dialect)
    {
        var origin = new Uri("https://schema.invalid/openapi");
        if (value.ValueKind == JsonValueKind.String)
            return (value.GetString()!, ContentType(dialect, value.GetString()!), origin);
        if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("content", out var content)
            && content.ValueKind == JsonValueKind.String)
        {
            var contentType = value.TryGetProperty("contentType", out var type) && type.ValueKind == JsonValueKind.String
                ? type.GetString()! : ContentType(dialect, content.GetString()!);
            if (value.TryGetProperty("origin", out var source) && source.ValueKind == JsonValueKind.String
                && Uri.TryCreate(source.GetString(), UriKind.Absolute, out var supplied)) origin = supplied;
            return (content.GetString()!, contentType, origin);
        }
        return (value.GetRawText(), "application/json", origin);
    }

    private static string ContentType(string? dialect, string value) =>
        string.Equals(dialect, "application/json", StringComparison.OrdinalIgnoreCase) || value.TrimStart().StartsWith('{')
            ? "application/json" : "application/yaml";
    private static string SafeMessage(OpenApiExtensionException exception) => exception.Message[(exception.Code.Length + 2)..];
}
