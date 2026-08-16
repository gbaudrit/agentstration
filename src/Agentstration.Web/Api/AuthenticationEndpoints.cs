using Agentstration.Security.AspNetCoreIdentity;
using Agentstration.Web.Configuration;
using Agentstration.Web.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using WebAuthenticationOptions = Agentstration.Web.Configuration.AuthenticationOptions;

namespace Agentstration.Web;

public static class AuthenticationEndpoints
{
    public static IEndpointRouteBuilder MapAgentstrationAuthentication(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/auth");
        group.MapGet("/bootstrap", BootstrapStatusAsync).AllowAnonymous();
        group.MapPost("/bootstrap", BootstrapAsync).AllowAnonymous();
        group.MapPost("/local/login", LoginAsync).AllowAnonymous();
        group.MapPost("/logout", LogoutAsync).RequireAuthorization(AgentstrationPolicies.Authenticated);
        group.MapGet("/oidc/login", OidcLogin).AllowAnonymous();
        return endpoints;
    }

    public static IEndpointRouteBuilder MapAgentstrationLocalAccountAdministration(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/identity/accounts")
            .RequireAuthorization(AgentstrationPolicies.PlatformAdmin);
        group.MapGet("/", ListAccountsAsync);
        group.MapPost("/", CreateAccountAsync);
        group.MapPut("/{accountId:guid}/status", SetAccountStatusAsync);
        return endpoints;
    }

    private static async Task<IResult> ListAccountsAsync(
        LocalAccountAdministrationService accounts,
        IOptions<AgentstrationWebOptions> options,
        CancellationToken cancellationToken)
    {
        if (!WebAuthenticationOptions.SupportsLocalAccounts(options.Value.Authentication.Mode)) return Results.NotFound();
        return Results.Ok(await accounts.ListAsync(cancellationToken));
    }

    private static async Task<IResult> CreateAccountAsync(
        CreateLocalAccountRequest request,
        LocalAccountAdministrationService accounts,
        IOptions<AgentstrationWebOptions> options,
        CancellationToken cancellationToken)
    {
        if (!WebAuthenticationOptions.SupportsLocalAccounts(options.Value.Authentication.Mode)) return Results.NotFound();
        try
        {
            var result = await accounts.CreateAsync(request, cancellationToken);
            if (result.Account is null)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["account"] = result.Errors.ToArray() });
            return Results.Created($"/api/identity/accounts/{result.Account.AccountId:D}", result.Account);
        }
        catch (ArgumentException exception)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["account"] = [exception.Message] });
        }
    }

    private static async Task<IResult> SetAccountStatusAsync(
        Guid accountId,
        SetLocalAccountStatusRequest request,
        LocalAccountAdministrationService accounts,
        IOptions<AgentstrationWebOptions> options,
        CancellationToken cancellationToken)
    {
        if (!WebAuthenticationOptions.SupportsLocalAccounts(options.Value.Authentication.Mode)) return Results.NotFound();
        try { return Results.Ok(await accounts.SetEnabledAsync(accountId, request.Enabled, cancellationToken)); }
        catch (ArgumentException exception) { return Results.NotFound(new { error = exception.Message }); }
        catch (InvalidOperationException exception) { return Results.Conflict(new { error = exception.Message }); }
    }

    private static async Task<IResult> BootstrapStatusAsync(
        LocalBootstrapCoordinator bootstrap,
        IOptions<AgentstrationWebOptions> options,
        CancellationToken cancellationToken)
    {
        if (!WebAuthenticationOptions.SupportsLocalAccounts(options.Value.Authentication.Mode)) return Results.NotFound();
        return Results.Ok(new { initialized = await bootstrap.IsInitializedAsync(cancellationToken) });
    }

    private static async Task<IResult> BootstrapAsync(
        LocalBootstrapRequest request,
        LocalBootstrapCoordinator bootstrap,
        UserManager<LocalIdentityUser> users,
        SignInManager<LocalIdentityUser> signIn,
        IOptions<AgentstrationWebOptions> options,
        CancellationToken cancellationToken)
    {
        if (!WebAuthenticationOptions.SupportsLocalAccounts(options.Value.Authentication.Mode)) return Results.NotFound();
        if (await bootstrap.IsInitializedAsync(cancellationToken))
            return Results.Conflict(new { error = "instance_already_initialized" });
        var result = await bootstrap.BootstrapAsync(request, cancellationToken);
        if (!result.Succeeded)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["bootstrap"] = result.Errors.ToArray() });

        var account = await users.FindByNameAsync(request.UserName.Trim());
        if (account is null) throw new InvalidOperationException("The bootstrapped local account could not be loaded.");
        await signIn.SignInAsync(account, isPersistent: false);
        return Results.Created("/api/identity/context", new { result.PrincipalId });
    }

    private static async Task<IResult> LoginAsync(
        LocalLoginRequest request,
        SignInManager<LocalIdentityUser> signIn,
        IOptions<AgentstrationWebOptions> options)
    {
        if (!WebAuthenticationOptions.SupportsLocalAccounts(options.Value.Authentication.Mode)) return Results.NotFound();
        if (string.IsNullOrWhiteSpace(request.UserName) || string.IsNullOrWhiteSpace(request.Password)) return Results.Unauthorized();
        var result = await signIn.PasswordSignInAsync(request.UserName.Trim(), request.Password, request.RememberMe, lockoutOnFailure: true);
        if (result.IsLockedOut) return Results.Problem(statusCode: StatusCodes.Status423Locked, title: "account_locked");
        if (!result.Succeeded) return Results.Unauthorized();
        return Results.NoContent();
    }

    private static async Task<IResult> LogoutAsync(SignInManager<LocalIdentityUser> signIn)
    {
        await signIn.SignOutAsync();
        return Results.NoContent();
    }

    private static IResult OidcLogin(string? returnUrl, IOptions<AgentstrationWebOptions> options)
    {
        var mode = options.Value.Authentication.Mode;
        if (!WebAuthenticationOptions.SupportsExternalLogin(mode)) return Results.NotFound();
        return Results.Challenge(
            new AuthenticationProperties { RedirectUri = AuthenticationReturnUrls.Normalize(returnUrl) },
            [OpenIdConnectDefaults.AuthenticationScheme]);
    }
}

public sealed record LocalLoginRequest(string UserName, string Password, bool RememberMe = false);
public sealed record SetLocalAccountStatusRequest(bool Enabled);
