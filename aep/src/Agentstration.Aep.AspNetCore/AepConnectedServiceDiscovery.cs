using System.Text.Json;
using Agentstration.Aep.Abstractions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Agentstration.Aep.AspNetCore;

public interface IAepConnectedServiceOperationDiscovery
{
    string ServiceTypeId { get; }
    Task<AepConnectedServiceDiscoveryResponse> DiscoverAsync(
        AepConnectedServiceDiscoveryRequest request, CancellationToken cancellationToken);
}

public static partial class AepServerExtensions
{
    private static async Task<IResult> DiscoverConnectedServiceOperationsAsync(
        HttpRequest httpRequest,
        IEnumerable<IAepConnectedServiceOperationDiscovery> providers,
        IOptions<AepExtensionOptions> options, HttpResponse response, CancellationToken cancellationToken)
    {
        response.Headers.CacheControl = "no-store";
        const int maximumRequestBytes = 64 * 1024;
        if (httpRequest.ContentLength > maximumRequestBytes) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        using var body = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await httpRequest.Body.ReadAsync(buffer, cancellationToken)) != 0)
        {
            if (body.Length + count > maximumRequestBytes) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            await body.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
        }
        AepConnectedServiceDiscoveryRequest? request;
        try { request = JsonSerializer.Deserialize<AepConnectedServiceDiscoveryRequest>(body.GetBuffer().AsSpan(0, checked((int)body.Length)), AepProtocol.JsonOptions); }
        catch (JsonException) { return Results.BadRequest(new { code = "connected_service_request_invalid" }); }
        if (request is null || request.BoundValues is null) return Results.BadRequest(new { code = "connected_service_request_invalid" });
        var provider = providers.SingleOrDefault(value => value.ServiceTypeId == request.ServiceTypeId);
        var version = options.Value.ConnectedServiceTypes.SingleOrDefault(value => value.Id == request.ServiceTypeId)?
            .Versions.SingleOrDefault(value => value.Version == request.ServiceTypeVersion);
        if (provider is null || version is null
            || !options.Value.Capabilities.TryGetValue(AepConnectedServiceDiscoveryProtocol.Capability, out var capability)
            || capability.Version != AepConnectedServiceDiscoveryProtocol.Version) return Results.NotFound();
        if (request.Options.ValueKind != JsonValueKind.Object || request.CorrelationId is not { Length: > 0 and <= 128 })
            return Results.BadRequest(new { code = "connected_service_request_invalid" });
        var requirements = version.Inputs.Select(input => new AepValueRequirement(
            AepContributionKinds.ConnectedServiceType, request.ServiceTypeId, input.Id, input.Required,
            input.Type, input.Protection)).ToArray();
        if (AepBoundValueValidator.Validate(request.BoundValues, requirements,
            AepContributionKinds.ConnectedServiceType, request.ServiceTypeId, requireAll: true).Count != 0)
            return Results.BadRequest(new { code = "connected_service_values_invalid" });
        AepConnectedServiceDiscoveryResponse result;
        try { result = await provider.DiscoverAsync(request, cancellationToken); }
        catch (AepServerException exception) { return Error(exception.StatusCode, exception.Code, exception.Message); }
        if (JsonSerializer.SerializeToUtf8Bytes(result, AepProtocol.JsonOptions).Length > AepConnectedServiceDiscoveryProtocol.MaximumResponseBytes)
            return Results.BadRequest(new { code = "connected_service_discovery_too_large" });
        return Results.Json(result, AepProtocol.JsonOptions);
    }
}
