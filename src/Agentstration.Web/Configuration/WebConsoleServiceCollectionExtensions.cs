using Agentstration.Management.Abstractions;
using Agentstration.Web.Components;
using Agentstration.Web.Components.State;
using Agentstration.Web.Console;
using Agentstration.Web.Features.Flows.Designer;
using Agentstration.Web.FlowDesigner.Backend;
using Agentstration.Web.FlowDesigner.DependencyInjection;
using Agentstration.Web.Security;

namespace Agentstration.Web.Configuration;

public static class WebConsoleServiceCollectionExtensions
{
    public static IServiceCollection AddAgentstrationWebConsole(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        services.AddOptions<AgentstrationWebOptions>()
            .Bind(configuration.GetSection(AgentstrationWebOptions.SectionName))
            .Validate(Validate, "API base addresses must be absolute HTTP(S) URIs and timeouts must be between 1 and 120 seconds.")
            .ValidateOnStart();
        services.AddSingleton(TimeProvider.System);
        services.AddAgentstrationWebComponents();
        services.AddSingleton<ConsoleRealtimeSession>();
        services.AddSingleton<IConsoleRealtimeConnectionConfigurator>(provider => provider.GetRequiredService<ConsoleRealtimeSession>());
        services.AddScoped<IConsoleContextProvider, ConsoleContextProvider>();
        services.AddScoped<IResourceSearchProvider, ConsoleResourceSearchProvider>();
        services.AddAgentstrationFlowDesigner();
        services.AddScoped<PlatformDashboardService>();
        services.AddScoped<IFlowDesignerBackend, FlowDesignerBackend>();
        services.AddScoped<IFlowDesignerResourceProvider, FlowDesignerResourceProvider>();

        var configured = configuration.GetSection(AgentstrationWebOptions.SectionName).Get<AgentstrationWebOptions>() ?? new();
        AddClient<RuntimeApiClient, IRuntimeApiClient>(services, configured.RuntimeApi);
        AddClient(services, CleanupApiClient.RuntimeClient, configured.RuntimeApi);
        AddClient(services, CleanupApiClient.FlowClient, configured.FlowApi);
        AddClient(services, CleanupApiClient.ManagementClient, configured.ManagementApi);
        AddClient(services, CleanupApiClient.WorkClient, configured.WorkApi);
        services.AddScoped<ICleanupApiClient, CleanupApiClient>();
        services.AddScoped<IAgentstrationEventStream, HttpAgentstrationEventStream>();
        AddClient<WorkApiClient, IWorkApiClient>(services, configured.WorkApi);
        AddClient<EntryAdministrationApiClient, IEntryAdministrationApiClient>(services, configured.WorkApi);
        AddClient(services, EntryAdministrationApiClient.AgentResourceCatalogClient, configured.ManagementApi);
        AddClient(services, EntryAdministrationApiClient.FlowResourceCatalogClient, configured.FlowApi);
        services.AddScoped<IWorkOperationsRealtimeClient>(provider => new WorkOperationsRealtimeClient(
            new Uri(new Uri(configured.WorkApi.BaseAddress, UriKind.Absolute), "hubs/workplace"),
            provider.GetRequiredService<ConsoleRealtimeSession>(),
            provider.GetRequiredService<ILogger<WorkOperationsRealtimeClient>>()));
        AddClient<FlowApiClient, IFlowApiClient>(services, configured.FlowApi);
        AddClient<ToolGovernanceAuditApiClient, IToolGovernanceAuditClient>(services, configured.RuntimeApi);
        AddClient<ManagementApiClient, IManagementApiClient>(services, configured.ManagementApi);
        AddClient<IdentityAdministrationApiClient, IIdentityAdministrationApiClient>(services, configured.ManagementApi);
        AddClient<HttpUserPreferencesClient, IUserPreferencesClient>(services, configured.ManagementApi);
        AddClient<ModelProvidersApiClient, IModelProvidersClient>(services, configured.ManagementApi);
        AddClient<ExtensionsApiClient, IExtensionsClient>(services, configured.ManagementApi);
        AddClient<SourceConsoleApiClient, ISourceConsoleApiClient>(services, configured.ManagementApi);
        AddClient<SourceProvidersApiClient, ISourceProvidersClient>(services, configured.ManagementApi);
        AddClient<SourceRegistriesApiClient, ISourceRegistriesClient>(services, configured.ManagementApi);
        AddClient<ModelProfilesApiClient, IModelProfilesClient>(services, configured.ManagementApi);
        AddClient<AgentsModelApiClient, IAgentsModelClient>(services, configured.ManagementApi);
        AddClient<RuntimeProfilesApiClient, IRuntimeProfilesClient>(services, configured.ManagementApi);
        AddClient<PacksApiClient, IPacksClient>(services, configured.ManagementApi);
        AddSensitiveClient<BootstrapProfilesApiClient, IBootstrapProfilesApiClient>(services, configured.ManagementApi);
        AddClient<TriggerApiClient, ITriggerApiClient>(services, configured.ManagementApi);
        AddClient<ToolsApiClient, IToolsClient>(services, configured.ManagementApi);
        AddClient<ToolDefinitionsApiClient, IToolDefinitionsClient>(services, configured.ManagementApi);
        AddSensitiveClient<SecretsApiClient, ISecretsClient>(services, configured.ManagementApi);
        AddClient<ResourceScopeInventoryApiClient, IResourceScopeInventoryClient>(services, configured.ManagementApi);
        AddClient<ManagementApiClient, IAgentRunnerManagementClient>(services, configured.ManagementApi);
        AddClient<RuntimeApiClient, IAgentRunnerRuntimeClient>(services, configured.RuntimeApi);
        return services;
    }

    private static void AddClient<TImplementation, TContract>(IServiceCollection services, ApiEndpointOptions options)
        where TImplementation : class, TContract
        where TContract : class => Configure(services.AddHttpClient<TContract, TImplementation>(), options);

    private static void AddClient(IServiceCollection services, string name, ApiEndpointOptions options) =>
        Configure(services.AddHttpClient(name), options);

    private static void AddSensitiveClient<TImplementation, TContract>(IServiceCollection services, ApiEndpointOptions options)
        where TImplementation : class, TContract
        where TContract : class => ConfigureClient(services.AddHttpClient<TContract, TImplementation>(), options);

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

    private static bool Validate(AgentstrationWebOptions options) => ValidateEndpoint(options.WorkApi) &&
        ValidateEndpoint(options.ManagementApi) && ValidateEndpoint(options.RuntimeApi) && ValidateEndpoint(options.FlowApi) &&
        (string.IsNullOrWhiteSpace(options.WorkplaceBaseUrl) || Uri.TryCreate(options.WorkplaceBaseUrl, UriKind.Absolute, out var workplace) && workplace.Scheme is "http" or "https");

    private static bool ValidateEndpoint(ApiEndpointOptions options) =>
        options.TimeoutSeconds is >= 1 and <= 120 &&
        Uri.TryCreate(options.BaseAddress, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
