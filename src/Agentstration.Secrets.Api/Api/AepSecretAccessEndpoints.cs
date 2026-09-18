using Agentstration.Aep.Abstractions;
using Agentstration.Secrets.Abstractions;
using Microsoft.AspNetCore.Mvc;

namespace Agentstration.Web.Api.Models;

internal static class AepSecretAccessEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(AepProtocol.SecretAccessPath, RedeemAsync)
            .AllowAnonymous()
            .RequireRateLimiting("aep-secret-access")
            .Produces<AepSecretAccessResponse>(StatusCodes.Status200OK)
            .Produces<AepErrorResponse>(StatusCodes.Status403Forbidden)
            .Produces<AepErrorResponse>(StatusCodes.Status410Gone)
            .Produces<AepErrorResponse>(StatusCodes.Status422UnprocessableEntity)
            .Produces<AepErrorResponse>(StatusCodes.Status503ServiceUnavailable)
            .WithSummary("Redeem a one-use AEP Secret capability")
            .WithMetadata(new RequestSizeLimitAttribute(4096));
    }

    private static async Task<IResult> RedeemAsync(
        AepSecretAccessRequest request,
        HttpResponse response,
        ISecretCapabilityService capabilities,
        CancellationToken cancellationToken)
    {
        response.Headers.CacheControl = "no-store";
        response.Headers.Pragma = "no-cache";
        if (request.Version != AepProtocol.SecretAccessVersion)
            return Error("secret_access_version_unsupported", StatusCodes.Status422UnprocessableEntity);
        try
        {
            using var secret = await capabilities.RedeemAsync(
                request.SecretCapability,
                request.ExtensionId,
                request.RequirementId,
                request.ExecutionId,
                cancellationToken);
            var bytes = secret.Value.AccessValue();
            if (bytes.Length is < 1 or > 65_536)
                return Error("secret_value_invalid", StatusCodes.Status422UnprocessableEntity);
            return Results.Json(new AepSecretAccessResponse(AepProtocol.SecretAccessVersion,
                Convert.ToBase64String(bytes.Span)), AepProtocol.JsonOptions);
        }
        catch (SecretCapabilityException exception)
        {
            var status = exception.Code switch
            {
                "secret_unavailable" or "vault_unavailable" => StatusCodes.Status503ServiceUnavailable,
                "capability_expired" or "context_terminated" => StatusCodes.Status410Gone,
                _ => StatusCodes.Status403Forbidden
            };
            return Error(exception.Code, status);
        }
    }

    private static IResult Error(string code, int status) =>
        Results.Json(new AepErrorResponse(new AepError(code, "Secret access failed.")),
            AepProtocol.JsonOptions, statusCode: status);
}
