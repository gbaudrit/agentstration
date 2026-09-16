using Agentstration.Console.Web.Features.Flows;
using Agentstration.Console.Web.Security;
using Agentstration.Web.Components;
using Agentstration.Web.Components.State;
using Agentstration.Web.Configuration;
using Agentstration.Web.Console;
using Agentstration.Web.FlowDesigner.Backend;
using Agentstration.Web.FlowDesigner.DependencyInjection;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.Extensions.Options;

namespace Agentstration.Console.Web.Configuration;

public static class ConsoleHostServiceCollectionExtensions
{
    public static IServiceCollection AddAgentstrationConsoleHost(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<AgentstrationWebOptions>()
            .Bind(configuration.GetSection(AgentstrationWebOptions.SectionName))
            .Validate(Validate, "API base addresses must be absolute HTTP(S) URIs and timeouts must be between 1 and 120 seconds.")
            .ValidateOnStart();
        services.AddOptions<BffWorkloadClientOptions>()
            .Bind(configuration.GetSection(BffWorkloadClientOptions.SectionName))
            .Validate(value => value.Validate(), "BFF workload client configuration is invalid.")
            .ValidateOnStart();
        services.AddOptions<BffSessionOptions>()
            .Bind(configuration.GetSection(BffSessionOptions.SectionName))
            .Validate(value => value.Validate(), "BFF session configuration is invalid.")
            .ValidateOnStart();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IBffServerSessionStore, InMemoryBffSessionStore>();
        services.AddSingleton<IPostConfigureOptions<Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationOptions>, BffCookieConfiguration>();
        services.AddAgentstrationWebComponents();
        services.AddSingleton<IConsoleRealtimeConnectionConfigurator, NoOpConsoleRealtimeConnectionConfigurator>();
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
            provider.GetRequiredService<IConsoleRealtimeConnectionConfigurator>(),
            provider.GetRequiredService<ILogger<WorkOperationsRealtimeClient>>()));
        AddClient<FlowApiClient, IFlowApiClient>(services, configured.FlowApi);
        AddClient<ToolGovernanceAuditApiClient, IToolGovernanceAuditClient>(services, configured.RuntimeApi);
        AddClient<ManagementApiClient, IManagementApiClient>(services, configured.ManagementApi);
        AddClient<IdentityAdministrationApiClient, IIdentityAdministrationApiClient>(services, configured.ManagementApi);
        AddClient<HttpUserPreferencesClient, IUserPreferencesClient>(services, configured.ManagementApi, resilient: false);
        AddClient<ModelProvidersApiClient, IModelProvidersClient>(services, configured.ManagementApi);
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
        AddClient<ToolDefinitionsApiClient, IToolDefinitionsClient>(services, configured.ManagementApi);
        AddClient<SecretsApiClient, ISecretsClient>(services, configured.ManagementApi, resilient: false);
        services.AddTransient<BffWorkloadSigningHandler>();
        Configure(
            services.AddHttpClient<IBffWorkloadTrustClient, BffWorkloadTrustClient>()
                .AddHttpMessageHandler<BffWorkloadSigningHandler>(),
            configured.ManagementApi,
            resilient: false);
        Configure(
            services.AddHttpClient<IBffSessionAuthorityClient, BffSessionAuthorityClient>()
                .AddHttpMessageHandler<BffWorkloadSigningHandler>(),
            configured.ManagementApi,
            resilient: false);
        AddClient<ResourceScopeInventoryApiClient, IResourceScopeInventoryClient>(services, configured.ManagementApi);
        AddClient<ManagementApiClient, IAgentRunnerManagementClient>(services, configured.ManagementApi);
        AddClient<RuntimeApiClient, IAgentRunnerRuntimeClient>(services, configured.RuntimeApi);
        return services;
    }

    private static void AddClient<TImplementation, TContract>(
        IServiceCollection services,
        ApiEndpointOptions options,
        bool resilient = true)
        where TImplementation : class, TContract
        where TContract : class => Configure(services.AddHttpClient<TContract, TImplementation>(), options, resilient);

    private static void AddClient(IServiceCollection services, string name, ApiEndpointOptions options) =>
        Configure(services.AddHttpClient(name), options, resilient: true);

    private static void Configure(IHttpClientBuilder builder, ApiEndpointOptions options, bool resilient)
    {
        builder.ConfigureHttpClient(client =>
        {
            client.BaseAddress = new Uri(options.BaseAddress, UriKind.Absolute);
            client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
            client.DefaultRequestHeaders.Add("X-Agentstration-Client", "Agentstration.Console.Web");
        });
        builder.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
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
