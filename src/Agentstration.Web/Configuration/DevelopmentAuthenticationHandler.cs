using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Agentstration.Web.Configuration;

public sealed class DevelopmentAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IOptions<AgentstrationWebOptions> webOptions,
    IHostEnvironment environment)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "AgentstrationDevelopment";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var authentication = webOptions.Value.Authentication;
        if (string.Equals(authentication.Mode, AuthenticationOptions.Disabled, StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(AuthenticateResult.NoResult());
        if (!string.Equals(authentication.Mode, AuthenticationOptions.Development, StringComparison.OrdinalIgnoreCase)
            || (!environment.IsDevelopment() && !environment.IsEnvironment("Testing")))
            return Task.FromResult(AuthenticateResult.NoResult());
        var claims = new[]
        {
            new Claim("iss", authentication.DevelopmentIssuer),
            new Claim("sub", authentication.DevelopmentSubject),
            new Claim(ClaimTypes.Name, authentication.DevelopmentDisplayName)
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
    }
}
