using System.Threading.RateLimiting;
using Agentstration.Web.Api.Models;
using Microsoft.AspNetCore.RateLimiting;

namespace Agentstration.Secrets.Api;

public static class SecretsApiModule
{
    public static IServiceCollection AddSecretsApi(this IServiceCollection services)
    {
        services.AddRateLimiter(options => options.AddPolicy("aep-secret-access", context =>
            RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 600,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0
                })));
        return services;
    }

    public static IEndpointRouteBuilder MapSecretsApi(this IEndpointRouteBuilder endpoints)
    {
        SecretEndpoints.Map(endpoints);
        AepSecretAccessEndpoints.Map(endpoints);
        return endpoints;
    }
}
