using System.Text.Json;
using Agentstration.Aep.Abstractions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Agentstration.Aep.AspNetCore;

public interface IAepConnectedServiceOperationAcquisition
{
    string OperationTypeId { get; }
    Task<AepConnectedServiceAcquisitionResponse> AcquireAsync(
        AepConnectedServiceAcquisitionRequest request, CancellationToken cancellationToken);
}

public static partial class AepServerExtensions
{
    private static async Task<IResult> AcquireConnectedServiceOperationAsync(
        HttpRequest httpRequest,
        IEnumerable<IAepConnectedServiceOperationAcquisition> providers,
        IOptions<AepExtensionOptions> options, HttpResponse response, CancellationToken cancellationToken)
    {
        response.Headers.CacheControl = "no-store";
        const int maximumRequestBytes = AepConnectedServiceAcquisitionProtocol.MaximumRequestBytes;
        if (httpRequest.ContentLength > maximumRequestBytes) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        using var body = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await httpRequest.Body.ReadAsync(buffer, cancellationToken)) != 0)
        {
            if (body.Length + count > maximumRequestBytes) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            await body.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
        }
        AepConnectedServiceAcquisitionRequest? request;
        try { request = JsonSerializer.Deserialize<AepConnectedServiceAcquisitionRequest>(body.GetBuffer().AsSpan(0, checked((int)body.Length)), AepProtocol.JsonOptions); }
        catch (JsonException) { return Results.BadRequest(new { code = "connected_service_request_invalid" }); }
        if (request is null || request.BoundValues is null) return Results.BadRequest(new { code = "connected_service_request_invalid" });
        var provider = providers.SingleOrDefault(value => value.OperationTypeId == request.OperationTypeId);
        var version = options.Value.ConnectedServiceTypes.SingleOrDefault(value => value.Id == request.ServiceTypeId)?
            .Versions.SingleOrDefault(value => value.Version == request.ServiceTypeVersion);
        var operationVersion = options.Value.ConnectedServiceOperationTypes.SingleOrDefault(value => value.Id == request.OperationTypeId)?
            .Versions.SingleOrDefault(value => value.Version == request.OperationTypeVersion);
        if (provider is null || version is null || operationVersion?.Acquisition is null
            || !options.Value.Capabilities.TryGetValue(AepConnectedServiceAcquisitionProtocol.Capability, out var capability)
            || capability.Version != AepConnectedServiceAcquisitionProtocol.Version) return Results.NotFound();
        if (operationVersion.Interaction != AepConnectedServiceOperationInteraction.RequestResponse
            || request.Definition.ValueKind != JsonValueKind.Object
            || !request.Definition.TryGetProperty(operationVersion.Acquisition.ReadOnlyProperty, out var readOnly)
            || readOnly.ValueKind != JsonValueKind.True
            || request.Parameters.ValueKind == JsonValueKind.Undefined)
            return Results.BadRequest(new { code = "connected_service_acquisition_invalid" });
        if (request.Options.ValueKind != JsonValueKind.Object || request.CorrelationId is not { Length: > 0 and <= 128 })
            return Results.BadRequest(new { code = "connected_service_request_invalid" });
        var requirements = version.Inputs.Select(input => new AepValueRequirement(
            AepContributionKinds.ConnectedServiceType, request.ServiceTypeId, input.Id, input.Required,
            input.Type, input.Protection)).ToArray();
        if (AepBoundValueValidator.Validate(request.BoundValues, requirements,
            AepContributionKinds.ConnectedServiceType, request.ServiceTypeId, requireAll: true).Count != 0)
            return Results.BadRequest(new { code = "connected_service_values_invalid" });
        AepConnectedServiceAcquisitionResponse result;
        try { result = await provider.AcquireAsync(request, cancellationToken); }
        catch (AepServerException exception) { return Error(exception.StatusCode, exception.Code, exception.Message); }
        if (JsonSerializer.SerializeToUtf8Bytes(result, AepProtocol.JsonOptions).Length > AepConnectedServiceAcquisitionProtocol.MaximumResponseBytes)
            return Results.BadRequest(new { code = "connected_service_acquisition_too_large" });
        return Results.Json(result, AepProtocol.JsonOptions);
    }
}
