using System.Threading.RateLimiting;
using Agentstration.Aep.Abstractions;
using Agentstration.Web.Api;
using Agentstration.Web.Api.Models;
using Microsoft.AspNetCore.RateLimiting;

namespace Agentstration.Extensions.Api;

public static class ExtensionsApiModule
{
    public static IServiceCollection AddExtensionsApi(this IServiceCollection services)
    {
        services.AddRateLimiter(rateLimiting =>
        {
            rateLimiting.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            rateLimiting.OnRejected = static async (context, token) =>
                await context.HttpContext.Response.WriteAsJsonAsync(
                    new { error = new AepEnrollmentError("rate_limited", "Too many enrollment requests; retry later.") }, token);
            rateLimiting.AddPolicy("aep-enrollment-public", context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 30,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0
                    }));
        });
        return services;
    }

    public static IEndpointRouteBuilder MapExtensionsApi(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapAgentstrationAepEnrollment();
        ExtensionEndpoints.Map(endpoints);
        return endpoints;
    }
}
