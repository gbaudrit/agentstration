using System.Text.Json;
using Agentstration.Aep.Abstractions;
using Agentstration.Aep.AspNetCore;
using Agentstration.Aep.Client;

namespace Agentstration.Extensions.OpenApi;

public sealed class OpenApiConnectedServiceAcquisition(
    OpenApiService service, OpenApiDestinationPolicy destinations, IHttpClientFactory clients)
    : IAepConnectedServiceOperationAcquisition
{
    public string OperationTypeId => "openapi-operation";

    public async Task<AepConnectedServiceAcquisitionResponse> AcquireAsync(
        AepConnectedServiceAcquisitionRequest request, CancellationToken cancellationToken)
    {
        using var http = clients.CreateClient("openapi-secret-access");
        try
        {
            var values = new AepValueResolver(request.BoundValues, http);
            using var origin = await values.ResolveAsync("baseUrl", cancellationToken);
            using var credential = values.Contains("bearerToken") ? await values.ResolveAsync("bearerToken", cancellationToken) : null;
            var baseUri = await destinations.ValidateAsync(origin.ReadString(), cancellationToken);
            var token = credential?.ReadString();
            if (token is not null && (token.Length is < 1 or > 65536 || token.Contains('\r') || token.Contains('\n')))
                throw new OpenApiExtensionException("openapi_credential_invalid", "The protected credential is invalid.");
            var definition = request.Definition;
            var selector = new OpenApiOperationSelector(Text("contractUri"), Text("contractDigest"), Text("server"),
                Text("operationId"), Text("method"), Text("path"))
            {
                ResponseStatus = ResponseChoice("status"),
                ResponseMediaType = ResponseChoice("mediaType")
            };
            var result = await service.InvokeAsync(selector, request.Parameters, null, null, 1, request.CorrelationId,
                cancellationToken, token, baseUri, requireReadOnly: true);
            if (result.MediaType != "application/json" && !result.MediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase))
                throw new OpenApiExtensionException("openapi_acquisition_json_required", "Acquisition requires a JSON response.");
            var bytes = Convert.FromBase64String(result.ContentBase64);
            if (bytes.Length > AepConnectedServiceAcquisitionProtocol.MaximumResponseBytes - 64)
                throw new OpenApiExtensionException("openapi_acquisition_too_large", "Acquisition exceeds the response bound.");
            var output = JsonSerializer.Deserialize<JsonElement>(bytes);
            if (token is not null && OpenApiService.ContainsCredential(output, token))
                throw new OpenApiExtensionException("openapi_sensitive_response", "The response contains protected connection material.");
            return new(output);

            string? ResponseChoice(string name) => definition.TryGetProperty("responseShape", out var shape)
                && shape.TryGetProperty("x-agentstration-response", out var choice) && choice.TryGetProperty(name, out var value)
                    ? value.GetString() : null;
            string Text(string name) => definition.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                && value.GetString() is { Length: > 0 } text ? text
                : throw new OpenApiExtensionException("openapi_selector_invalid", "The saved selector is invalid.");
        }
        catch (OpenApiExtensionException exception)
        { throw new AepServerException(exception.Code, "OpenAPI acquisition could not be completed."); }
        catch (AepProtocolException)
        { throw new AepServerException("openapi_connection_unavailable", "The protected connection could not be resolved."); }
        catch (JsonException)
        { throw new AepServerException("openapi_response_invalid", "The response is not valid JSON."); }
    }
}
