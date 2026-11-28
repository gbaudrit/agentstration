using System.Text.Json;
using Agentstration.Aep.Abstractions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Aep.AspNetCore;

public interface IAepOperationalConfiguration
{
    AepOperationalConfigurationState Read();
    ValueTask<AepOperationalConfigurationState> ApplyAsync(AepOperationalConfigurationRequest request, CancellationToken cancellationToken);
}

public static partial class AepServerExtensions
{
    private static async Task<IResult> ApplyOperationalConfigurationAsync(HttpContext http,
        IAepOperationalConfiguration configuration, CancellationToken cancellationToken)
    {
        http.Response.Headers.CacheControl = "no-store";
        if (http.Request.ContentLength > AepOperationalConfigurationProtocol.MaximumBytes)
            return Results.Problem(statusCode: 413);
        try
        {
            using var body = new MemoryStream();
            var buffer = new byte[4096];
            int count;
            while ((count = await http.Request.Body.ReadAsync(buffer, cancellationToken)) != 0)
            {
                if (body.Length + count > AepOperationalConfigurationProtocol.MaximumBytes)
                    return Results.Problem(statusCode: 413);
                body.Write(buffer, 0, count);
            }
            var request = JsonSerializer.Deserialize<AepOperationalConfigurationRequest>(body.ToArray(), AepProtocol.JsonOptions);
            var descriptor = configuration.Read().Descriptor;
            if (request is null || !descriptor.IsValid() || request.ConfigurationId == Guid.Empty || request.Generation < 1
                || request.Revision is not { Length: > 0 and <= 128 } || request.Values is null
                || request.OptionSet != descriptor.Id || request.Version != descriptor.Version || request.SchemaDigest != descriptor.SchemaDigest
                || request.Values.Any(v => v is null)
                || request.Values.Count > descriptor.Options.Count
                || request.Values.Any(v => !descriptor.Options.Any(o => o.Id == v.RequirementId))
                || request.Values.Select(v => v.RequirementId).Distinct(StringComparer.Ordinal).Count() != request.Values.Count)
                return OperationalConfigurationError("configuration_invalid", 422);
            foreach (var option in descriptor.Options)
            {
                var value = request.Values.SingleOrDefault(v => v.RequirementId == option.Id);
                if (value is null)
                {
                    if (option.Required) return OperationalConfigurationError("configuration_invalid", 422);
                    continue;
                }
                if ((value.InlineValue is null) == (value.SecretGrant is null)
                    || value.InlineValue is not null && value.Kind != AepBoundValueKind.Inline
                    || value.SecretGrant is not null && (value.Kind != AepBoundValueKind.SecretGrant
                        || !option.AllowedSources.Contains(AepConfigurationValueSource.Secret))
                    || value.InlineValue is not null && !option.AllowedSources.Any(s => s is AepConfigurationValueSource.Literal or AepConfigurationValueSource.Parameter)
                    || option.Protection == AepValueProtection.Secured && value.SecretGrant is null
                    || value.InlineValue is { } literal && AepOptionSchemaValidator.Validate(literal, option.Schema).Count != 0)
                    return OperationalConfigurationError("configuration_invalid", 422);
            }
            return Results.Json(PublicOperationalState(await configuration.ApplyAsync(request, cancellationToken)), AepProtocol.JsonOptions);
        }
        catch (JsonException) { return OperationalConfigurationError("configuration_invalid", 422); }
        catch (AepOperationalConfigurationException exception) { return OperationalConfigurationError(exception.Code, exception.Conflict ? 409 : 422); }
    }

    private static IResult OperationalConfigurationError(string code, int status) =>
        Results.Json(new { error = new { code, message = "The operational configuration could not be applied." } }, statusCode: status);

    private static AepOperationalConfigurationState PublicOperationalState(AepOperationalConfigurationState state) => state with
    {
        AppliedValues = state.AppliedValues.Where(p => state.Descriptor.Options.Any(o => o.Id == p.Key
            && o.Protection != AepValueProtection.Secured)).ToDictionary(p => p.Key, p => p.Value)
    };
}

public sealed class AepOperationalConfigurationException(string code, bool conflict = false) : Exception(code)
{
    public string Code { get; } = code;
    public bool Conflict { get; } = conflict;
}
