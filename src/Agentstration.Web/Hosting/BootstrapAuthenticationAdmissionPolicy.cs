using Agentstration.Identity.Contracts;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;

namespace Agentstration.Web.Hosting;

internal sealed class BootstrapAuthenticationAdmissionPolicy(
    Agentstration.Infrastructure.BootstrapStartupState startup,
    IPlatformAuthorizationService authorization) : IAuthenticationAdmissionPolicy
{
    public Task<bool> CanSignInAsync(Guid principalId, CancellationToken cancellationToken) =>
        startup.IsReady ? Task.FromResult(true)
            : startup.AdministrationReady ? authorization.IsPlatformAdministratorAsync(principalId, cancellationToken)
            : Task.FromResult(false);
}

internal static class BootstrapAuthenticationAdmissionRegistration
{
    internal static void AddBootstrapAuthenticationAdmission(this IServiceCollection services)
    {
        services.AddScoped<IAuthenticationAdmissionPolicy, BootstrapAuthenticationAdmissionPolicy>();
        services.PostConfigure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, options =>
        {
            var previous = options.Events.OnTicketReceived;
            options.Events.OnTicketReceived = async context =>
            {
                await previous(context);
                if (context.Result is not null) return;
                var startup = context.HttpContext.RequestServices.GetRequiredService<Agentstration.Infrastructure.BootstrapStartupState>();
                if (startup.IsReady) return;
                var issuer = context.Principal?.FindFirst("iss")?.Value;
                var subject = context.Principal?.FindFirst("sub")?.Value;
                var principal = issuer is null || subject is null ? null
                    : await context.HttpContext.RequestServices.GetRequiredService<IPrincipalResolver>()
                        .ResolveAsync(issuer, subject, context.HttpContext.RequestAborted);
                if (principal is not { Status: PrincipalStatus.Active } || !await context.HttpContext.RequestServices.GetRequiredService<IAuthenticationAdmissionPolicy>()
                        .CanSignInAsync(principal.Id, context.HttpContext.RequestAborted))
                    context.Fail("authentication_unavailable");
            };
        });
    }
}
