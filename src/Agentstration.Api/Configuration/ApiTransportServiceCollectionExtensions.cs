using System.Threading.RateLimiting;
using Agentstration.Aep.Abstractions;
using Agentstration.Application.Work;
using Agentstration.Flows.Application;
using Agentstration.Infrastructure.Flows;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Core;
using Agentstration.Web.Api;
using Agentstration.Web.Features.Flows;
using Agentstration.Web.Features.Workplace;
using Agentstration.Web.Hosting;
using Agentstration.Web.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using ModelContextProtocol.AspNetCore;

namespace Agentstration.Web.Configuration;

public static class ApiTransportServiceCollectionExtensions
{
    public static IServiceCollection AddAgentstrationApi(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        services.AddOptions<AgentstrationApiOptions>()
            .Bind(configuration.GetSection(AgentstrationApiOptions.SectionName));
        var options = configuration.GetSection($"{AgentstrationApiOptions.SectionName}:Authentication")
            .Get<ApiAuthenticationOptions>() ?? new();

        services.AddProblemDetails();
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
        services.AddAgentstrationOpenApi();
        services.AddSignalR();
        services.AddHttpContextAccessor();
        AddSecurity(services, options, environment);

        services.AddSingleton<BootstrapProfileCatalog>();
        services.AddSingleton<SourceBootstrapProfileLoader>();
        services.AddSingleton<BootstrapApplicationLock>();
        services.AddScoped<BootstrapProfileManagementService>();
        services.AddScoped<SourceConsoleManagementService>();
        services.AddSingleton<SignalRFlowRunEventSink>();
        services.AddSingleton<WorkplaceFlowConversationProjectionSink>();
        services.AddSingleton<IFlowRunEventSink>(provider => new CompositeFlowRunEventSink(
        [
            provider.GetRequiredService<WorkplaceFlowConversationProjectionSink>(),
            provider.GetRequiredService<SignalRFlowRunEventSink>()
        ]));
        services.AddSingleton<IWorkplaceEventSink, SignalRWorkplaceEventSink>();
        services.AddMcpServer()
            .WithHttpTransport()
            .WithToolsFromAssembly()
            .WithListToolsHandler(AgentstrationMcpHandlers.ListToolsAsync)
            .WithCallToolHandler(AgentstrationMcpHandlers.CallToolAsync);
        return services;
    }

    private static void AddSecurity(
        IServiceCollection services,
        ApiAuthenticationOptions options,
        IHostEnvironment environment)
    {
        var local = string.Equals(options.Mode, ApiAuthenticationOptions.Local, StringComparison.OrdinalIgnoreCase);
        var oidc = string.Equals(options.Mode, ApiAuthenticationOptions.Oidc, StringComparison.OrdinalIgnoreCase);
        var hybrid = string.Equals(options.Mode, ApiAuthenticationOptions.Hybrid, StringComparison.OrdinalIgnoreCase);
        if (local || oidc || hybrid)
        {
            if ((oidc || hybrid) && (string.IsNullOrWhiteSpace(options.Authority) || string.IsNullOrWhiteSpace(options.Audience)
                || string.IsNullOrWhiteSpace(options.ClientId)))
                throw new InvalidOperationException("OIDC authentication requires Authority, Audience, and ClientId.");

            var authentication = services.AddAuthentication(authenticationOptions =>
                {
                    authenticationOptions.DefaultScheme = AgentstrationAuthenticationDefaults.PolicyScheme;
                    authenticationOptions.DefaultChallengeScheme = AgentstrationAuthenticationDefaults.PolicyScheme;
                })
                .AddPolicyScheme(AgentstrationAuthenticationDefaults.PolicyScheme, "Agentstration authentication", policy =>
                {
                    policy.ForwardDefaultSelector = context =>
                    {
                        var bearer = context.Request.Headers.Authorization.ToString()
                            .StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase);
                        var personalAccessToken = context.Request.Headers.Authorization.ToString()
                            .StartsWith($"Bearer {PersonalAccessTokenService.TokenPrefix}", StringComparison.Ordinal);
                        var apiWithoutWebSession = oidc
                            && (context.Request.Path.StartsWithSegments("/api") || context.Request.Path.StartsWithSegments("/mcp"))
                            && !context.Request.Cookies.ContainsKey(AgentstrationAuthenticationDefaults.ApplicationCookie);
                        if (personalAccessToken) return PersonalAccessTokenAuthenticationDefaults.Scheme;
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
                    if (oidc) cookie.ForwardChallenge = OpenIdConnectDefaults.AuthenticationScheme;
                    cookie.Events.OnValidatePrincipal = SecurityStampValidator.ValidatePrincipalAsync;
                    cookie.Events.OnRedirectToLogin = context => ApiStatusOrRedirect(context, StatusCodes.Status401Unauthorized);
                    cookie.Events.OnRedirectToAccessDenied = context => ApiStatusOrRedirect(context, StatusCodes.Status403Forbidden);
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
            if (!string.Equals(options.Mode, ApiAuthenticationOptions.Development, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(options.Mode, ApiAuthenticationOptions.Disabled, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Unsupported authentication mode '{options.Mode}'.");
            if (!environment.IsDevelopment() && !environment.IsEnvironment("Testing"))
                throw new InvalidOperationException($"Authentication mode '{options.Mode}' is permitted only in Development or Testing.");
            services.AddAuthentication(DevelopmentAuthenticationHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, DevelopmentAuthenticationHandler>(DevelopmentAuthenticationHandler.SchemeName, _ => { });
        }

        services.AddSingleton<IAuthorizationHandler, WorkspacePermissionHandler>();
        services.AddSingleton<IAuthorizationHandler, WorkspaceResourcePermissionHandler>();
        services.AddSingleton<IAuthorizationHandler, PlatformAdministratorHandler>();
        services.AddSingleton<IAuthorizationHandler, InteractiveUserHandler>();
        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
            .AddPolicy(AgentstrationPolicies.Authenticated, policy => policy.RequireAuthenticatedUser())
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
            .AddPolicy(AgentstrationPolicies.WorkspaceReader, policy => WorkspacePolicy(policy, AuthorizationPermissions.WorkspacesRead))
            .AddPolicy(AgentstrationPolicies.WorkspaceAdmin, policy => WorkspacePolicy(policy, AuthorizationPermissions.WorkspacesWrite))
            .AddPolicy(AgentstrationPolicies.AuthorizationReader, policy => WorkspacePolicy(policy, AuthorizationPermissions.AuthorizationRead))
            .AddPolicy(AgentstrationPolicies.AuthorizationAdmin, policy => WorkspacePolicy(policy, AuthorizationPermissions.AuthorizationWrite))
            .AddPolicy(AgentstrationPolicies.CanReadResources, policy => WorkspacePolicy(policy, AuthorizationPermissions.ResourcesRead))
            .AddPolicy(AgentstrationPolicies.CanWriteResources, policy => WorkspacePolicy(policy, AuthorizationPermissions.ResourcesWrite))
            .AddPolicy(AgentstrationPolicies.CanDeleteResources, policy => WorkspacePolicy(policy, AuthorizationPermissions.ResourcesDelete))
            .AddPolicy(AgentstrationPolicies.CanReadRuns, policy => WorkspacePolicy(policy, AuthorizationPermissions.RunsRead))
            .AddPolicy(AgentstrationPolicies.CanExecuteRuns, policy => WorkspacePolicy(policy, AuthorizationPermissions.RunsExecute))
            .AddPolicy(AgentstrationPolicies.CanDeleteRuns, policy => WorkspacePolicy(policy, AuthorizationPermissions.RunsDelete));
    }

    private static void WorkspacePolicy(AuthorizationPolicyBuilder policy, string permission)
    {
        policy.RequireAuthenticatedUser();
        policy.AddRequirements(new WorkspacePermissionRequirement(permission));
    }

    private static Task ApiStatusOrRedirect(RedirectContext<CookieAuthenticationOptions> context, int statusCode)
    {
        if (context.Request.Path.StartsWithSegments("/api")
            || context.Request.Path.StartsWithSegments("/hubs")
            || context.Request.Path.StartsWithSegments("/mcp"))
            context.Response.StatusCode = statusCode;
        else context.Response.Redirect(context.RedirectUri);
        return Task.CompletedTask;
    }
}
