using Agentstration.Management.Core;
using Agentstration.Web.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;

namespace Agentstration.Web.Configuration;

internal static class WebConsoleSecurityServiceCollectionExtensions
{
    internal static IServiceCollection AddAgentstrationConsoleAuthentication(
        this IServiceCollection services,
        AuthenticationOptions options,
        IHostEnvironment environment)
    {
        services.AddHttpContextAccessor();
        var local = string.Equals(options.Mode, AuthenticationOptions.Local, StringComparison.OrdinalIgnoreCase);
        var oidc = string.Equals(options.Mode, AuthenticationOptions.Oidc, StringComparison.OrdinalIgnoreCase);
        var hybrid = string.Equals(options.Mode, AuthenticationOptions.Hybrid, StringComparison.OrdinalIgnoreCase);
        if (local || oidc || hybrid)
        {
            if ((oidc || hybrid)
                && (string.IsNullOrWhiteSpace(options.Authority)
                    || string.IsNullOrWhiteSpace(options.Audience)
                    || string.IsNullOrWhiteSpace(options.ClientId)))
            {
                throw new InvalidOperationException(
                    "OIDC authentication requires Authority, Audience, and ClientId.");
            }

            var authentication = services.AddAuthentication(authenticationOptions =>
                {
                    authenticationOptions.DefaultScheme =
                        AgentstrationAuthenticationDefaults.PolicyScheme;
                    authenticationOptions.DefaultChallengeScheme =
                        AgentstrationAuthenticationDefaults.PolicyScheme;
                })
                .AddPolicyScheme(
                    AgentstrationAuthenticationDefaults.PolicyScheme,
                    "Agentstration authentication",
                    policy =>
                    {
                        policy.ForwardDefaultSelector = context =>
                        {
                            var authorization = context.Request.Headers.Authorization.ToString();
                            var bearer = authorization.StartsWith(
                                "Bearer ",
                                StringComparison.OrdinalIgnoreCase);
                            var personalAccessToken = authorization.StartsWith(
                                $"Bearer {PersonalAccessTokenService.TokenPrefix}",
                                StringComparison.Ordinal);
                            var apiWithoutWebSession = oidc
                                && (context.Request.Path.StartsWithSegments("/api")
                                    || context.Request.Path.StartsWithSegments("/mcp"))
                                && !context.Request.Cookies.ContainsKey(
                                    AgentstrationAuthenticationDefaults.ApplicationCookie);
                            if (personalAccessToken)
                                return PersonalAccessTokenAuthenticationDefaults.Scheme;
                            return (oidc || hybrid) && (bearer || apiWithoutWebSession)
                                ? JwtBearerDefaults.AuthenticationScheme
                                : IdentityConstants.ApplicationScheme;
                        };
                    })
                .AddCookie(IdentityConstants.ApplicationScheme, cookie =>
                {
                    cookie.Cookie.Name = AgentstrationAuthenticationDefaults.ApplicationCookie;
                    cookie.LoginPath = "/login";
                    cookie.AccessDeniedPath = "/access-denied";
                    cookie.SlidingExpiration = true;
                    if (oidc)
                        cookie.ForwardChallenge = OpenIdConnectDefaults.AuthenticationScheme;
                    cookie.Events.OnValidatePrincipal = SecurityStampValidator.ValidatePrincipalAsync;
                    cookie.Events.OnRedirectToLogin = context =>
                        ApiStatusOrRedirect(context, StatusCodes.Status401Unauthorized);
                    cookie.Events.OnRedirectToAccessDenied = context =>
                        ApiStatusOrRedirect(context, StatusCodes.Status403Forbidden);
                })
                .AddCookie(IdentityConstants.ExternalScheme)
                .AddCookie(IdentityConstants.TwoFactorRememberMeScheme)
                .AddCookie(IdentityConstants.TwoFactorUserIdScheme)
                .AddScheme<AuthenticationSchemeOptions, PersonalAccessTokenAuthenticationHandler>(
                    PersonalAccessTokenAuthenticationDefaults.Scheme,
                    _ => { });

            if (oidc || hybrid)
            {
                authentication.AddJwtBearer(jwt =>
                {
                    jwt.Authority = options.Authority;
                    jwt.Audience = options.Audience;
                    jwt.RequireHttpsMetadata = options.RequireHttpsMetadata;
                    jwt.MapInboundClaims = false;
                }).AddOpenIdConnect(oidcOptions =>
                {
                    oidcOptions.Authority = options.Authority;
                    oidcOptions.ClientId = options.ClientId;
                    oidcOptions.ClientSecret = options.ClientSecret;
                    oidcOptions.RequireHttpsMetadata = options.RequireHttpsMetadata;
                    oidcOptions.ResponseType = "code";
                    oidcOptions.UsePkce = true;
                    oidcOptions.SaveTokens = true;
                    oidcOptions.MapInboundClaims = false;
                    oidcOptions.SignInScheme = IdentityConstants.ApplicationScheme;
                    oidcOptions.Scope.Clear();
                    oidcOptions.Scope.Add("openid");
                    oidcOptions.Scope.Add("profile");
                    oidcOptions.Scope.Add("email");
                });
            }
        }
        else
        {
            if (!string.Equals(
                    options.Mode,
                    AuthenticationOptions.Development,
                    StringComparison.OrdinalIgnoreCase)
                && !string.Equals(
                    options.Mode,
                    AuthenticationOptions.Disabled,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Unsupported authentication mode '{options.Mode}'.");
            }
            if (!environment.IsDevelopment() && !environment.IsEnvironment("Testing"))
            {
                throw new InvalidOperationException(
                    $"Authentication mode '{options.Mode}' is permitted only in Development or Testing.");
            }
            services.AddAuthentication(DevelopmentAuthenticationHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, DevelopmentAuthenticationHandler>(
                    DevelopmentAuthenticationHandler.SchemeName,
                    _ => { });
        }

        return services;
    }

    internal static IServiceCollection AddAgentstrationConsoleAuthorization(
        this IServiceCollection services)
    {
        services.AddSingleton<IAuthorizationHandler, WorkspacePermissionHandler>();
        services.AddSingleton<IAuthorizationHandler, WorkspaceResourcePermissionHandler>();
        services.AddSingleton<IAuthorizationHandler, PlatformAdministratorHandler>();
        services.AddSingleton<IAuthorizationHandler, InteractiveUserHandler>();
        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build())
            .AddPolicy(
                AgentstrationPolicies.Authenticated,
                policy => policy.RequireAuthenticatedUser())
            .AddPolicy(AgentstrationPolicies.PlatformAdmin, policy =>
            {
                policy.RequireAuthenticatedUser();
                policy.AddRequirements(new InteractiveUserRequirement());
                policy.AddRequirements(new PlatformAdministratorRequirement());
            })
            .AddPolicy(AgentstrationPolicies.InteractiveUser, policy =>
            {
                policy.RequireAuthenticatedUser();
                policy.AddRequirements(new InteractiveUserRequirement());
            })
            .AddPolicy(
                AgentstrationPolicies.WorkspaceReader,
                policy => WorkspacePolicy(policy, AuthorizationPermissions.WorkspacesRead))
            .AddPolicy(
                AgentstrationPolicies.WorkspaceAdmin,
                policy => WorkspacePolicy(policy, AuthorizationPermissions.WorkspacesWrite))
            .AddPolicy(
                AgentstrationPolicies.AuthorizationReader,
                policy => WorkspacePolicy(policy, AuthorizationPermissions.AuthorizationRead))
            .AddPolicy(
                AgentstrationPolicies.AuthorizationAdmin,
                policy => WorkspacePolicy(policy, AuthorizationPermissions.AuthorizationWrite))
            .AddPolicy(
                AgentstrationPolicies.CanReadResources,
                policy => WorkspacePolicy(policy, AuthorizationPermissions.ResourcesRead))
            .AddPolicy(
                AgentstrationPolicies.CanWriteResources,
                policy => WorkspacePolicy(policy, AuthorizationPermissions.ResourcesWrite))
            .AddPolicy(
                AgentstrationPolicies.CanDeleteResources,
                policy => WorkspacePolicy(policy, AuthorizationPermissions.ResourcesDelete))
            .AddPolicy(
                AgentstrationPolicies.CanReadRuns,
                policy => WorkspacePolicy(policy, AuthorizationPermissions.RunsRead))
            .AddPolicy(
                AgentstrationPolicies.CanExecuteRuns,
                policy => WorkspacePolicy(policy, AuthorizationPermissions.RunsExecute))
            .AddPolicy(
                AgentstrationPolicies.CanDeleteRuns,
                policy => WorkspacePolicy(policy, AuthorizationPermissions.RunsDelete));
        return services;
    }

    private static void WorkspacePolicy(AuthorizationPolicyBuilder policy, string permission)
    {
        policy.RequireAuthenticatedUser();
        policy.AddRequirements(new WorkspacePermissionRequirement(permission));
    }

    private static Task ApiStatusOrRedirect(
        RedirectContext<CookieAuthenticationOptions> context,
        int statusCode)
    {
        if (context.Request.Path.StartsWithSegments("/api")
            || context.Request.Path.StartsWithSegments("/hubs")
            || context.Request.Path.StartsWithSegments("/mcp"))
        {
            context.Response.StatusCode = statusCode;
        }
        else
        {
            context.Response.Redirect(context.RedirectUri);
        }
        return Task.CompletedTask;
    }
}
