using Agentstration.Identity.Contracts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;

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
        if (context.Principal is null)
        {
            await RejectAndRevokeAsync(context);
            return;
        }

        var request = BffSessionClaims.ValidationRequest(context.Principal);
        if (request is null)
        {
            await RejectAndRevokeAsync(context);
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
            await RejectAndRevokeAsync(context);
            return;
        }

        if (!validation.Active || validation.Identity is null)
        {
            await RejectAndRevokeAsync(context);
            return;
        }

        context.ReplacePrincipal(BffSessionClaims.Create(validation.Identity));
        context.ShouldRenew = true;
    }

    private static async Task RejectAndRevokeAsync(CookieValidatePrincipalContext context)
    {
        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(ConsoleAuthenticationDefaults.Scheme);
    }
}
