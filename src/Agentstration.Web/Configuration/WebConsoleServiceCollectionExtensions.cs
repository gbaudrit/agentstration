using Agentstration.Web.Components;
using Agentstration.Web.Components.State;
using Agentstration.Web.Console;
using Agentstration.Management.Abstractions;
using Agentstration.Web.Features.Flows.Designer;
using Agentstration.Web.FlowDesigner.Backend;
using Agentstration.Web.FlowDesigner.DependencyInjection;
using Agentstration.Web.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace Agentstration.Web.Configuration;

public static class WebConsoleServiceCollectionExtensions
{
    public static IServiceCollection AddAgentstrationWebConsole(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        services.AddOptions<AgentstrationWebOptions>()
            .Bind(configuration.GetSection(AgentstrationWebOptions.SectionName))
            .Validate(Validate, "API base addresses must be absolute HTTP(S) URIs and timeouts must be between 1 and 120 seconds.")
            .ValidateOnStart();
        services.AddSingleton(TimeProvider.System);
        services.AddAgentstrationWebComponents();
        services.AddScoped<IConsoleContextProvider, ConsoleContextProvider>();
        services.AddScoped<IResourceSearchProvider, ConsoleResourceSearchProvider>();
        services.AddAgentstrationFlowDesigner();
        services.AddScoped<PlatformDashboardService>();
        services.AddScoped<IFlowDesignerBackend, FlowDesignerBackend>();
        services.AddScoped<IFlowDesignerResourceProvider, FlowDesignerResourceProvider>();

        var configured = configuration.GetSection(AgentstrationWebOptions.SectionName).Get<AgentstrationWebOptions>() ?? new();
        if (configured.UseSimulatedData)
        {
            services.AddScoped<MockApiClient>();
            services.AddScoped<IRuntimeApiClient>(provider => provider.GetRequiredService<MockApiClient>());
            services.AddScoped<IAgentstrationEventStream>(provider => provider.GetRequiredService<MockApiClient>());
        }
        else
        {
            AddClient<RuntimeApiClient, IRuntimeApiClient>(services, configured.RuntimeApi);
            services.AddScoped<IAgentstrationEventStream, HttpAgentstrationEventStream>();
        }

        // Tasks are always real Work API resources, even when unrelated Console
        // projections still use deterministic demonstration data.
        AddClient<WorkApiClient, IWorkApiClient>(services, configured.WorkApi);
        AddClient<EntryAdministrationApiClient, IEntryAdministrationApiClient>(services, configured.WorkApi);
        AddClient(services, EntryAdministrationApiClient.AgentResourceCatalogClient, configured.ManagementApi);
        AddClient(services, EntryAdministrationApiClient.FlowResourceCatalogClient, configured.FlowApi);
        services.AddScoped<IWorkOperationsRealtimeClient>(provider => new WorkOperationsRealtimeClient(
            new Uri(new Uri(configured.WorkApi.BaseAddress, UriKind.Absolute), "hubs/workplace"), provider.GetRequiredService<ILogger<WorkOperationsRealtimeClient>>()));

        AddClient<FlowApiClient, IFlowApiClient>(services, configured.FlowApi);

        // Agent and model management always use the canonical HTTP APIs so that
        // edits and Runtime activation observe the same persisted generations and
        // profiles, even when unrelated dashboard widgets use simulated data.
        AddClient<ManagementApiClient, IManagementApiClient>(services, configured.ManagementApi);
        AddClient<ModelProvidersApiClient, IModelProvidersClient>(services, configured.ManagementApi);
        AddClient<ModelProfilesApiClient, IModelProfilesClient>(services, configured.ManagementApi);
        AddClient<AgentsModelApiClient, IAgentsModelClient>(services, configured.ManagementApi);
        AddClient<RuntimeProfilesApiClient, IRuntimeProfilesClient>(services, configured.ManagementApi);
        AddClient<PacksApiClient, IPacksClient>(services, configured.ManagementApi);
        AddClient<ToolsApiClient, IToolsClient>(services, configured.ManagementApi);
        AddSensitiveClient<SecretsApiClient, ISecretsClient>(services, configured.ManagementApi);
        AddClient<ManagementApiClient, IAgentRunnerManagementClient>(services, configured.ManagementApi);
        AddClient<RuntimeApiClient, IAgentRunnerRuntimeClient>(services, configured.RuntimeApi);

        AddSecurity(services, configured.Authentication, environment);
        return services;
    }

    private static void AddSecurity(IServiceCollection services, AuthenticationOptions options, IHostEnvironment environment)
    {
        services.AddHttpContextAccessor();
        var local = string.Equals(options.Mode, AuthenticationOptions.Local, StringComparison.OrdinalIgnoreCase);
        var oidc = string.Equals(options.Mode, AuthenticationOptions.Oidc, StringComparison.OrdinalIgnoreCase);
        var hybrid = string.Equals(options.Mode, AuthenticationOptions.Hybrid, StringComparison.OrdinalIgnoreCase);
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
                        var apiWithoutWebSession = oidc
                            && (context.Request.Path.StartsWithSegments("/api") || context.Request.Path.StartsWithSegments("/mcp"))
                            && !context.Request.Cookies.ContainsKey(AgentstrationAuthenticationDefaults.ApplicationCookie);
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
                    cookie.Events.OnRedirectToLogin = context => ApiStatusOrRedirect(context, StatusCodes.Status401Unauthorized);
                    cookie.Events.OnRedirectToAccessDenied = context => ApiStatusOrRedirect(context, StatusCodes.Status403Forbidden);
                });

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
            if (!string.Equals(options.Mode, AuthenticationOptions.Development, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(options.Mode, AuthenticationOptions.Disabled, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Unsupported authentication mode '{options.Mode}'.");
            if (!environment.IsDevelopment() && !environment.IsEnvironment("Testing"))
                throw new InvalidOperationException($"Authentication mode '{options.Mode}' is permitted only in Development or Testing.");
            services.AddAuthentication(DevelopmentAuthenticationHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, DevelopmentAuthenticationHandler>(DevelopmentAuthenticationHandler.SchemeName, _ => { });
        }

        services.AddSingleton<IAuthorizationHandler, WorkspacePermissionHandler>();
        services.AddSingleton<IAuthorizationHandler, WorkspaceResourcePermissionHandler>();
        services.AddSingleton<IAuthorizationHandler, PlatformAdministratorHandler>();
        services.AddAuthorizationBuilder()
            .AddPolicy(AgentstrationPolicies.Authenticated, policy => policy.RequireAuthenticatedUser())
            .AddPolicy(AgentstrationPolicies.PlatformAdmin, policy =>
            {
                policy.RequireAuthenticatedUser();
                policy.AddRequirements(new PlatformAdministratorRequirement());
            })
            .AddPolicy(AgentstrationPolicies.WorkspaceReader, policy => WorkspacePolicy(policy, AuthorizationPermissions.WorkspacesRead))
            .AddPolicy(AgentstrationPolicies.WorkspaceAdmin, policy => WorkspacePolicy(policy, AuthorizationPermissions.WorkspacesWrite))
            .AddPolicy(AgentstrationPolicies.AuthorizationReader, policy => WorkspacePolicy(policy, AuthorizationPermissions.AuthorizationRead))
            .AddPolicy(AgentstrationPolicies.AuthorizationAdmin, policy => WorkspacePolicy(policy, AuthorizationPermissions.AuthorizationWrite))
            .AddPolicy(AgentstrationPolicies.CanReadAgents, policy => WorkspacePolicy(policy, AuthorizationPermissions.ResourcesRead))
            .AddPolicy(AgentstrationPolicies.CanManageAgents, policy => WorkspacePolicy(policy, AuthorizationPermissions.ResourcesWrite))
            .AddPolicy(AgentstrationPolicies.CanRunAgents, policy => WorkspacePolicy(policy, AuthorizationPermissions.RunsExecute));
    }

    private static void WorkspacePolicy(AuthorizationPolicyBuilder policy, string permission)
    {
        policy.RequireAuthenticatedUser();
        policy.AddRequirements(new WorkspacePermissionRequirement(permission));
    }

    private static Task ApiStatusOrRedirect(RedirectContext<CookieAuthenticationOptions> context, int statusCode)
    {
        if (context.Request.Path.StartsWithSegments("/api") || context.Request.Path.StartsWithSegments("/mcp"))
            context.Response.StatusCode = statusCode;
        else context.Response.Redirect(context.RedirectUri);
        return Task.CompletedTask;
    }

    private static void AddClient<TImplementation, TContract>(IServiceCollection services, ApiEndpointOptions options)
        where TImplementation : class, TContract
        where TContract : class
    {
        Configure(services.AddHttpClient<TContract, TImplementation>(), options);
    }

    private static void AddClient(IServiceCollection services, string name, ApiEndpointOptions options) =>
        Configure(services.AddHttpClient(name), options);

    private static void AddSensitiveClient<TImplementation, TContract>(IServiceCollection services, ApiEndpointOptions options)
        where TImplementation : class, TContract where TContract : class =>
        ConfigureClient(services.AddHttpClient<TContract, TImplementation>(), options);

    private static void Configure(IHttpClientBuilder builder, ApiEndpointOptions options)
    {
        ConfigureClient(builder, options).AddStandardResilienceHandler(resilience =>
        {
            resilience.AttemptTimeout.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
            resilience.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(Math.Min(120, options.TimeoutSeconds * 3));
            resilience.Retry.MaxRetryAttempts = 2;
        });
    }

    private static IHttpClientBuilder ConfigureClient(IHttpClientBuilder builder, ApiEndpointOptions options)
    {
        var baseAddress = new Uri(options.BaseAddress, UriKind.Absolute);
        builder.ConfigureHttpClient(client =>
        {
            client.BaseAddress = baseAddress;
            client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
            client.DefaultRequestHeaders.Add("X-Agentstration-Client", "Agentstration.Web");
        });
        if (options.ForwardSessionCookie)
        {
            builder.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
            builder.AddHttpMessageHandler(provider => new ConsoleApiSessionHandler(
                provider.GetRequiredService<IHttpContextAccessor>(),
                provider.GetRequiredService<ICurrentRequestContext>(),
                baseAddress,
                AgentstrationAuthenticationDefaults.ApplicationCookie));
        }
        return builder;
    }

    private static bool Validate(AgentstrationWebOptions options) => ValidateEndpoint(options.WorkApi) && (options.UseSimulatedData ||
        ValidateEndpoint(options.ManagementApi) && ValidateEndpoint(options.RuntimeApi) && ValidateEndpoint(options.FlowApi)) &&
        (string.IsNullOrWhiteSpace(options.WorkplaceBaseUrl) || Uri.TryCreate(options.WorkplaceBaseUrl, UriKind.Absolute, out var workplace) && workplace.Scheme is "http" or "https");

    private static bool ValidateEndpoint(ApiEndpointOptions options) =>
        options.TimeoutSeconds is >= 1 and <= 120 &&
        Uri.TryCreate(options.BaseAddress, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
