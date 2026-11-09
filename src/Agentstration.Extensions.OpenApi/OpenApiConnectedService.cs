using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentstration.Aep.Abstractions;
using Agentstration.Aep.AspNetCore;
using Agentstration.Aep.Client;

namespace Agentstration.Extensions.OpenApi;

public static class OpenApiConnectedService
{
    public const string Version = "1.0.0";
    public const string FlowName = "openapi-operation-discovery";
    public static JsonElement OptionsSchema { get; } = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            candidatePaths = new { type = "array", minItems = 1, maxItems = 8, items = new { type = "string", minLength = 1, maxLength = 512 } }
        },
        required = new[] { "candidatePaths" },
        additionalProperties = false
    });

    public static JsonElement DefinitionSchema { get; } = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            contractUri = new { type = "string", maxLength = 2048 },
            contractDigest = new { type = "string", minLength = 64, maxLength = 64 },
            server = new { type = "string", maxLength = 2048 },
            operationId = new { type = "string", minLength = 1, maxLength = 512 },
            method = new { type = "string", minLength = 1, maxLength = 16 },
            path = new { type = "string", minLength = 1, maxLength = 2048 },
            readOnly = new { type = "boolean" },
            requestShape = new { type = "object" },
            responseShape = new { type = "object" }
        },
        required = new[] { "contractUri", "contractDigest", "server", "operationId", "method", "path", "readOnly" },
        additionalProperties = false
    });

    public static void Contribute(AepExtensionOptions options)
    {
        options.Capabilities[AepConnectedServiceDiscoveryProtocol.Capability] = new(AepConnectedServiceDiscoveryProtocol.Version,
            AepConnectedServiceDiscoveryProtocol.Path);
        options.Capabilities[AepCapabilityNames.SecretAccess] = new(AepProtocol.SecretAccessVersion);
        options.Capabilities[AepCapabilityNames.BoundValues] = new(AepProtocol.BoundValuesCapabilityVersion);
        options.ConnectedServiceOperationTypes.Add(new("openapi-operation", "OpenAPI operation", Version,
            [AepConnectedServiceOperationTypeVersionDescriptor.Create(Version, DefinitionSchema,
                AepConnectedServiceOperationInteraction.RequestResponse, new("openapi.operation.invoke", "1.0"))]));
        options.ConnectedServiceTypes.Add(new("openapi", "OpenAPI service", Version,
            [AepConnectedServiceTypeVersionDescriptor.Create(Version, OptionsSchema,
                [new("baseUrl", true, Format: "uri"), new("bearerToken", false, Protection: AepValueProtection.Secured)],
                [new("baseUrl", Format: "uri"), new("bearerToken", Protection: AepValueProtection.Secured)]) with
            {
                OperationDiscovery = new(FlowName, Version, [new("openapi-operation", Version)])
            }]));
    }

    public const string Flow = """
        apiVersion: agentstration.io/v1
        kind: Flow
        metadata: { name: openapi-operation-discovery, namespace: Agentstration.Extensions.OpenApi }
        definition:
          displayName: Discover OpenAPI service operations
          description: Discovers typed operations through the protected Connected Service execution boundary.
          version: 1.0.0
          enabled: true
          metadata: { flow.contract: connected-service-operation-discovery/v1 }
          spec:
            flowKind: workflow
            entryNodeId: input
            nodes: [{ id: input, kind: input }]
            edges: []
          graph:
            entryStep: input
            inputSchema:
              type: object
              properties:
                contract: { type: string }
                discoveryId: { type: string }
                service: { type: object }
                serviceType: { type: object }
                correlationId: { type: string }
                caller: { type: object }
              required: [contract, discoveryId, service, serviceType, correlationId, caller]
              additionalProperties: false
            steps:
              - { type: input, name: input, displayName: Discovery request }
              - type: tool
                name: discover
                displayName: Discover service operations
                tool: { resourceId: agentstration.connected-service.operations.discover, namespace: default, scopeRef: /instance }
                argumentsMapping: { discoveryId: "${input.discoveryId}", discoveryNamespace: "${input.service.service.address.namespace}" }
              - type: output
                name: output
                displayName: Discovered operations
                outputMapping:
                  contract: connected-service-operation-discovery/v1
                  operations: "${steps.discover.output.operations}"
                  diagnostics: "${steps.discover.output.diagnostics}"
              - { type: output, name: failure, displayName: Discovery failed, outcome: error }
            transitions:
              - { id: input-discover, fromStep: input, event: completed, toStep: discover }
              - { id: discover-output, fromStep: discover, event: success, toStep: output }
              - { id: discover-failure, fromStep: discover, event: error, toStep: failure }
            outputSchema:
              type: object
              properties: { contract: { type: string }, operations: { type: array, items: { type: object } }, diagnostics: { type: array, items: { type: object } } }
              required: [contract, operations, diagnostics]
              additionalProperties: false
          publish: true
          activate: true
        """;
}

public sealed class OpenApiConnectedServiceDiscovery(OpenApiService service, IHttpClientFactory clients)
    : IAepConnectedServiceOperationDiscovery
{
    public string ServiceTypeId => "openapi";

    public async Task<AepConnectedServiceDiscoveryResponse> DiscoverAsync(
        AepConnectedServiceDiscoveryRequest request, CancellationToken cancellationToken)
    {
        if (!request.Options.TryGetProperty("candidatePaths", out var paths) || paths.ValueKind != JsonValueKind.Array
            || paths.GetArrayLength() is < 1 or > 8
            || paths.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String || item.GetString() is not { Length: > 0 and <= 512 })
            || request.Options.EnumerateObject().Any(item => item.Name != "candidatePaths"))
            throw new AepServerException("openapi_options_invalid", "OpenAPI discovery requires bounded explicit candidate paths.", 400);
        using var http = clients.CreateClient("openapi-secret-access");
        try
        {
            var values = new AepValueResolver(request.BoundValues, http);
            using var origin = await values.ResolveAsync("baseUrl", cancellationToken);
            using var credential = values.Contains("bearerToken") ? await values.ResolveAsync("bearerToken", cancellationToken) : null;
            return await service.DiscoverConnectionAsync(origin.ReadString(),
                paths.EnumerateArray().Select(item => item.GetString()!).ToArray(),
                credential?.ReadString(), request.CorrelationId, cancellationToken);
        }
        catch (OpenApiExtensionException exception)
        {
            throw new AepServerException(exception.Code, "OpenAPI service discovery could not be completed.");
        }
        catch (AepProtocolException)
        {
            throw new AepServerException("openapi_connection_unavailable", "The protected connection could not be resolved.");
        }
    }
}

public sealed partial class OpenApiService
{
    public async Task<AepConnectedServiceDiscoveryResponse> DiscoverConnectionAsync(
        string origin, IReadOnlyList<string> candidatePaths, string? bearerToken, string correlationId,
        CancellationToken cancellationToken)
    {
        var originUri = await destinations.ValidateAsync(origin, cancellationToken);
        if (bearerToken is not null && (bearerToken.Length is < 1 or > 65536 || bearerToken.Contains('\r') || bearerToken.Contains('\n')))
            throw new OpenApiExtensionException("openapi_credential_invalid", "The protected credential is invalid.");
        var operations = new List<AepDiscoveredServiceOperation>();
        var diagnostics = new List<AepServiceDiscoveryDiagnostic>();
        var contractCount = 0;
        foreach (var candidate in Candidates(originUri, candidatePaths))
        {
            try
            {
                var fetched = await FetchAsync(candidate, options.MaximumDocumentBytes, options.AllowedContractMediaTypes,
                    "openapi_contract", cancellationToken, bearerToken);
                if (fetched is null) continue;
                contractCount++;
                var contract = parser.Parse(Encoding.UTF8.GetString(fetched.Value.Content), fetched.Value.MediaType, candidate).Descriptor;
                foreach (var operation in contract.Operations)
                {
                    if (!operation.Eligible)
                    {
                        diagnostics.Add(new("openapi_operation_ineligible", "An operation has a missing or duplicate operationId."));
                        continue;
                    }
                    // Identity depends on location and operationId, never on mutable contract content/digest.
                    var externalId = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{candidate.AbsoluteUri}\n{operation.OperationId}")));
                    var definition = new Dictionary<string, object?>
                    {
                        ["contractUri"] = candidate.AbsoluteUri,
                        ["contractDigest"] = contract.Sha256,
                        ["server"] = contract.Servers[0],
                        ["operationId"] = operation.OperationId,
                        ["method"] = operation.Method,
                        ["path"] = operation.Path,
                        ["readOnly"] = operation.ReadOnly
                    };
                    if (operation.RequestShape is { } input) definition["requestShape"] = input;
                    if (operation.ResponseShape is { } output) definition["responseShape"] = output;
                    var displayName = string.IsNullOrWhiteSpace(operation.Summary) ? operation.OperationId! : operation.Summary;
                    operations.Add(new(externalId, displayName[..Math.Min(200, displayName.Length)],
                        "openapi-operation", OpenApiConnectedService.Version, JsonSerializer.SerializeToElement(definition)));
                    if (operations.Count > options.MaximumOperations)
                        throw new OpenApiExtensionException("openapi_operations_exceeded", "The operation inventory exceeds its bound.");
                }
            }
            catch (OpenApiExtensionException exception)
            {
                // Keep provider/parser details and document-controlled messages outside durable diagnostics.
                throw new OpenApiExtensionException(exception.Code, "The OpenAPI contract could not be discovered.");
            }
        }
        if (contractCount == 0)
            throw new OpenApiExtensionException("openapi_contract_unavailable", "No OpenAPI contract was found at the configured candidates.");
        var result = new AepConnectedServiceDiscoveryResponse(operations, diagnostics);
        if (bearerToken is not null && ContainsCredential(JsonSerializer.SerializeToElement(result, AepProtocol.JsonOptions), bearerToken))
            throw new OpenApiExtensionException("openapi_sensitive_contract", "The discovered metadata contains protected connection material.");
        if (JsonSerializer.SerializeToUtf8Bytes(result, AepProtocol.JsonOptions).Length > AepConnectedServiceDiscoveryProtocol.MaximumResponseBytes
            || operations.Count > options.MaximumOperations)
            throw new OpenApiExtensionException("openapi_manifest_too_large", "The operation inventory exceeds its bound.");
        return result;
    }

    private static bool ContainsCredential(JsonElement value, string credential) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString()!.Contains(credential, StringComparison.Ordinal),
        JsonValueKind.Object => value.EnumerateObject().Any(property => property.Name.Contains(credential, StringComparison.Ordinal)
            || ContainsCredential(property.Value, credential)),
        JsonValueKind.Array => value.EnumerateArray().Any(item => ContainsCredential(item, credential)),
        _ => false
    };
}
