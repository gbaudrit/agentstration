using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;
using Agentstration.Identity.Contracts;

namespace Agentstration.Console.Web.Security;

public sealed class BffCookieConfiguration(
    IBffServerSessionStore sessions,
    IOptions<BffSessionOptions> sessionOptions) : IPostConfigureOptions<CookieAuthenticationOptions>
{
    public void PostConfigure(string? name, CookieAuthenticationOptions options)
    {
        if (!string.Equals(name, ConsoleAuthenticationDefaults.Scheme, StringComparison.Ordinal)) return;
        options.SessionStore = sessions;
        options.ExpireTimeSpan = TimeSpan.FromMinutes(sessionOptions.Value.IdleTimeoutMinutes);
        options.SlidingExpiration = true;
        options.Events.OnValidatePrincipal = ValidatePrincipalAsync;
    }

    private static async Task ValidatePrincipalAsync(CookieValidatePrincipalContext context)
    {
        var request = BffSessionClaims.ValidationRequest(context.Principal);
        if (request is null)
        {
            context.RejectPrincipal();
            return;
        }

        BffSessionValidationResponse validation;
        try
        {
            validation = await context.HttpContext.RequestServices
                .GetRequiredService<IBffSessionAuthorityClient>()
                .ValidateAsync(request, context.HttpContext.RequestAborted);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            context.HttpContext.RequestServices.GetRequiredService<ILogger<BffCookieConfiguration>>()
                .LogWarning("The authoritative identity service could not validate the BFF session.");
            context.RejectPrincipal();
            return;
        }

        if (!validation.Active || validation.Identity is null)
        {
            context.RejectPrincipal();
            return;
        }

        context.ReplacePrincipal(BffSessionClaims.Create(validation.Identity));
        context.ShouldRenew = true;
    }
}
