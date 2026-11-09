using Agentstration.Console.Web.Features.Flows;
using Agentstration.Console.Web.Security;
using Agentstration.Identity.Contracts;
using Agentstration.Web.Components;
using Agentstration.Web.Components.State;
using Agentstration.Web.Configuration;
using Agentstration.Web.Console;
using Agentstration.Web.FlowDesigner.Backend;
using Agentstration.Web.FlowDesigner.DependencyInjection;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Agentstration.Console.Web.Configuration;

public static class ConsoleHostServiceCollectionExtensions
{
    public static IServiceCollection AddAgentstrationConsoleHost(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddAgentstrationConsoleOptions(configuration);
        services.AddAgentstrationConsoleComponents();
        var configured = configuration.GetSection(AgentstrationWebOptions.SectionName).Get<AgentstrationWebOptions>() ?? new();
        services.AddAgentstrationConsoleApiClients(configured);
        services.AddAgentstrationConsoleRealtimeClient(configured.WorkApi);
        services.AddAgentstrationConsoleBffAuthentication(configuration, configured.ManagementApi);
        services.AddAgentstrationConsoleAuthorization();
        return services;
    }

    internal static IServiceCollection AddAgentstrationConsoleOptions(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AgentstrationWebOptions>()
            .Bind(configuration.GetSection(AgentstrationWebOptions.SectionName))
            .Validate(Validate, "API base addresses must be absolute HTTP(S) URIs and timeouts must be between 1 and 120 seconds.")
            .ValidateOnStart();
        return services;
    }

    internal static IServiceCollection AddAgentstrationConsoleComponents(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddAgentstrationWebComponents();
        services.Replace(ServiceDescriptor.Scoped<IConsoleContextProvider, ConsoleContextProvider>());
        services.Replace(ServiceDescriptor.Scoped<IResourceSearchProvider, ConsoleResourceSearchProvider>());
        services.AddAgentstrationFlowDesigner();
        services.AddScoped<PlatformDashboardService>();
        services.Replace(ServiceDescriptor.Scoped<IPlatformStatusProvider>(provider => provider.GetRequiredService<PlatformDashboardService>()));
        services.AddScoped<IFlowDesignerBackend, FlowDesignerBackend>();
        services.AddScoped<IFlowDesignerResourceProvider, FlowDesignerResourceProvider>();

        return services;
    }

    internal static IServiceCollection AddAgentstrationConsoleApiClients(this IServiceCollection services, AgentstrationWebOptions configured)
    {
        AddClient<RuntimeApiClient, IRuntimeApiClient>(services, configured.RuntimeApi, InternalDelegationAudiences.Runtime);
        AddClient(services, CleanupApiClient.RuntimeClient, configured.RuntimeApi, InternalDelegationAudiences.Runtime);
        AddClient(services, CleanupApiClient.FlowClient, configured.FlowApi, InternalDelegationAudiences.Flow);
        AddClient(services, CleanupApiClient.ManagementClient, configured.ManagementApi, InternalDelegationAudiences.Management);
        AddClient(services, CleanupApiClient.WorkClient, configured.WorkApi, InternalDelegationAudiences.Work);
        services.AddScoped<ICleanupApiClient, CleanupApiClient>();
        services.AddScoped<IAgentstrationEventStream, HttpAgentstrationEventStream>();
        AddClient<WorkApiClient, IWorkApiClient>(services, configured.WorkApi, InternalDelegationAudiences.Work);
        AddClient<EntryAdministrationApiClient, IEntryAdministrationApiClient>(services, configured.WorkApi, InternalDelegationAudiences.Work);
        AddClient<FlowApiClient, IFlowApiClient>(services, configured.FlowApi, InternalDelegationAudiences.Flow);
        AddClient<ToolGovernanceAuditApiClient, IToolGovernanceAuditClient>(services, configured.RuntimeApi, InternalDelegationAudiences.Runtime);
        AddClient<ManagementApiClient, IManagementApiClient>(services, configured.ManagementApi);
        AddClient<IdentityAdministrationApiClient, IIdentityAdministrationApiClient>(services, configured.ManagementApi);
        AddClient<HttpUserPreferencesClient, IUserPreferencesClient>(services, configured.ManagementApi, resilient: false);
        AddClient<ModelProvidersApiClient, IModelProvidersClient>(services, configured.ManagementApi);
        AddClient<ModelsApiClient, IModelsClient>(services, configured.ManagementApi);
        AddClient<ExtensionsApiClient, IExtensionsClient>(services, configured.ManagementApi);
        AddClient<SourceConsoleApiClient, ISourceConsoleApiClient>(services, configured.ManagementApi);
        AddClient<SourceProvidersApiClient, ISourceProvidersClient>(services, configured.ManagementApi);
        AddClient<SourceRegistriesApiClient, ISourceRegistriesClient>(services, configured.ManagementApi);
        AddClient<ModelProfilesApiClient, IModelProfilesClient>(services, configured.ManagementApi);
        AddClient<AgentsModelApiClient, IAgentsModelClient>(services, configured.ManagementApi);
        AddClient<RuntimeProfilesApiClient, IRuntimeProfilesClient>(services, configured.ManagementApi);
        AddClient<PacksApiClient, IPacksClient>(services, configured.ManagementApi);
        AddClient<BootstrapProfilesApiClient, IBootstrapProfilesApiClient>(services, configured.ManagementApi, resilient: false);
        AddClient<TriggerApiClient, ITriggerApiClient>(services, configured.ManagementApi);
        AddClient<ResourcePlansApiClient, IResourcePlansApiClient>(services, configured.ManagementApi);
        AddClient<ToolsApiClient, IToolsClient>(services, configured.ManagementApi);
        AddClient<ToolSetsApiClient, IToolSetsClient>(services, configured.ManagementApi);
        AddClient<KnowledgeSourcesApiClient, IKnowledgeSourcesClient>(services, configured.ManagementApi);
        AddClient<ArtifactsApiClient, IArtifactsClient>(services, configured.ManagementApi);
        AddClient<ToolCategoriesApiClient, IToolCategoriesClient>(services, configured.ManagementApi);
        AddClient<ToolDefinitionsApiClient, IToolDefinitionsClient>(services, configured.ManagementApi);
        AddClient<SecretsApiClient, ISecretsClient>(services, configured.ManagementApi, resilient: false);
        AddClient<ParametersApiClient, IParametersClient>(services, configured.ManagementApi, resilient: false);
        AddClient<ConnectedServicesApiClient, IConnectedServicesClient>(services, configured.ManagementApi);
        AddClient<ConnectedServiceOperationsApiClient, IConnectedServiceOperationsClient>(services, configured.ManagementApi);
        AddClient<StoragesApiClient, IStoragesClient>(services, configured.ManagementApi);
        AddClient<StoragesApiClient, IStorageTypesClient>(services, configured.ManagementApi);
        AddClient<SchemasApiClient, ISchemasClient>(services, configured.ManagementApi, resilient: false);
        AddClient<ResourceScopeInventoryApiClient, IResourceScopeInventoryClient>(services, configured.ManagementApi);
        AddClient<ResourceAccessPolicyApiClient, IResourceAccessPolicyClient>(services, configured.ManagementApi);
        AddClient<ManagementApiClient, IAgentRunnerManagementClient>(services, configured.ManagementApi);
        AddClient<RuntimeApiClient, IAgentRunnerRuntimeClient>(services, configured.RuntimeApi, InternalDelegationAudiences.Runtime);
        services.AddScoped<BffDelegatingHttpClientFactory>();
        // The BFF factory decorates the framework's pooled handlers with per-request
        // delegation. Select it explicitly rather than retaining a shadowed default.
        services.Replace(ServiceDescriptor.Scoped<IHttpClientFactory>(provider => provider.GetRequiredService<BffDelegatingHttpClientFactory>()));
        services.AddScoped<IConsoleContextHttpClientFactory>(provider => provider.GetRequiredService<BffDelegatingHttpClientFactory>());
        return services;
    }

    internal static IServiceCollection AddAgentstrationConsoleRealtimeClient(this IServiceCollection services, ApiEndpointOptions workApi)
    {
        services.AddSingleton<IConsoleRealtimeConnectionConfigurator, NoOpConsoleRealtimeConnectionConfigurator>();
        services.AddScoped<IWorkOperationsRealtimeClient>(provider => new WorkOperationsRealtimeClient(
            new Uri(new Uri(workApi.BaseAddress, UriKind.Absolute), "hubs/workplace"),
            provider.GetRequiredService<IConsoleRealtimeConnectionConfigurator>(),
            provider.GetRequiredService<ILogger<WorkOperationsRealtimeClient>>()));
        return services;
    }

    internal static IServiceCollection AddAgentstrationConsoleBffAuthentication(
        this IServiceCollection services,
        IConfiguration configuration,
        ApiEndpointOptions managementApi)
    {
        services.AddOptions<BffWorkloadClientOptions>()
            .Bind(configuration.GetSection(BffWorkloadClientOptions.SectionName))
            .Validate(value => value.Validate(), "BFF workload client configuration is invalid.")
            .ValidateOnStart();
        services.AddOptions<BffSessionOptions>()
            .Bind(configuration.GetSection(BffSessionOptions.SectionName))
            .Validate(value => value.Validate(), "BFF session configuration is invalid.")
            .ValidateOnStart();
        services.AddSingleton<IBffServerSessionStore, InMemoryBffSessionStore>();
        services.AddSingleton<BffDelegationTokenCache>();
        services.AddScoped<BffDelegationTokenProvider>();
        services.AddSingleton<IPostConfigureOptions<Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationOptions>, BffCookieConfiguration>();
        services.AddTransient<BffWorkloadSigningHandler>();
        Configure(
            services.AddHttpClient<IBffWorkloadTrustClient, BffWorkloadTrustClient>()
                .AddHttpMessageHandler<BffWorkloadSigningHandler>(),
            managementApi,
            resilient: false);
        Configure(
            services.AddHttpClient<IBffSessionAuthorityClient, BffSessionAuthorityClient>()
                .AddHttpMessageHandler<BffWorkloadSigningHandler>(),
            managementApi,
            resilient: false);
        Configure(
            services.AddHttpClient<IBffDelegationClient, BffDelegationClient>()
                .AddHttpMessageHandler<BffWorkloadSigningHandler>(),
            managementApi,
            resilient: false);
        services.AddHttpContextAccessor();
        services.AddAuthentication(ConsoleAuthenticationDefaults.Scheme)
            .AddCookie(ConsoleAuthenticationDefaults.Scheme, options =>
            {
                options.Cookie.Name = ConsoleAuthenticationDefaults.Cookie;
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                options.Cookie.IsEssential = true;
                options.LoginPath = "/login";
                options.ReturnUrlParameter = "returnUrl";
            });
        return services;
    }

    internal static IServiceCollection AddAgentstrationConsoleAuthorization(this IServiceCollection services)
    {
        services.AddAuthorization();
        return services;
    }

    private static void AddClient<TImplementation, TContract>(
        IServiceCollection services,
        ApiEndpointOptions options,
        string audience = InternalDelegationAudiences.Management,
        bool resilient = true)
        where TImplementation : class, TContract
        where TContract : class
    {
        var name = typeof(TContract).Name;
        Configure(services.AddHttpClient(name), options, resilient);
        services.AddSingleton(new BffDelegationTarget(name, audience, new Uri(options.BaseAddress, UriKind.Absolute)));
        services.Replace(ServiceDescriptor.Scoped<TContract>(provider =>
        {
            var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(name);
            return ActivatorUtilities.CreateInstance<TImplementation>(provider, client);
        }));
    }

    private static void AddClient(IServiceCollection services, string name, ApiEndpointOptions options, string audience)
    {
        Configure(services.AddHttpClient(name), options, resilient: true);
        services.AddSingleton(new BffDelegationTarget(name, audience, new Uri(options.BaseAddress, UriKind.Absolute)));
    }

    private static void Configure(IHttpClientBuilder builder, ApiEndpointOptions options, bool resilient)
    {
        builder.RedactLoggedHeaders(name =>
            string.Equals(name, "Authorization", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "Cookie", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, BffWorkloadAuthentication.SignatureHeader, StringComparison.OrdinalIgnoreCase));
        builder.ConfigureHttpClient(client =>
        {
            client.BaseAddress = new Uri(options.BaseAddress, UriKind.Absolute);
            client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
            client.DefaultRequestHeaders.Add("X-Agentstration-Client", "Agentstration.Console.Web");
        });
        builder.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false
        });
        if (!resilient) return;
        builder.AddStandardResilienceHandler(resilience =>
        {
            resilience.AttemptTimeout.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
            resilience.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(Math.Min(120, options.TimeoutSeconds * 3));
            resilience.Retry.MaxRetryAttempts = 2;
        });
    }

    private static bool Validate(AgentstrationWebOptions options) =>
        ValidateEndpoint(options.ManagementApi) && ValidateEndpoint(options.RuntimeApi) &&
        ValidateEndpoint(options.WorkApi) && ValidateEndpoint(options.FlowApi);

    private static bool ValidateEndpoint(ApiEndpointOptions options) =>
        options.TimeoutSeconds is >= 1 and <= 120 &&
        Uri.TryCreate(options.BaseAddress, UriKind.Absolute, out var uri) &&
        uri.Scheme is "http" or "https";

    private sealed class NoOpConsoleRealtimeConnectionConfigurator : IConsoleRealtimeConnectionConfigurator
    {
        public void Configure(Uri endpoint, HttpConnectionOptions options)
        {
            ArgumentNullException.ThrowIfNull(endpoint);
            ArgumentNullException.ThrowIfNull(options);
        }
    }
}
