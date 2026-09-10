using Agentstration.Management.Abstractions;
using Agentstration.Web.Components.State;
using Agentstration.Web.Console;
using Agentstration.Web.Hosting;
using Agentstration.Web.Security;

namespace Agentstration.Web.Configuration;

internal static class WebConsoleClientServiceCollectionExtensions
{
    internal static IServiceCollection AddAgentstrationConsoleClients(
        this IServiceCollection services,
        AgentstrationWebOptions options)
    {
        AddClient<RuntimeApiClient, IRuntimeApiClient>(services, options.RuntimeApi);
        AddClient(services, CleanupApiClient.RuntimeClient, options.RuntimeApi);
        AddClient(services, CleanupApiClient.FlowClient, options.FlowApi);
        AddClient(services, CleanupApiClient.ManagementClient, options.ManagementApi);
        AddClient(services, CleanupApiClient.WorkClient, options.WorkApi);
        services.AddScoped<ICleanupApiClient, CleanupApiClient>();
        services.AddScoped<IAgentstrationEventStream, HttpAgentstrationEventStream>();

        AddClient<WorkApiClient, IWorkApiClient>(services, options.WorkApi);
        AddClient<EntryAdministrationApiClient, IEntryAdministrationApiClient>(services, options.WorkApi);
        AddClient(services, EntryAdministrationApiClient.AgentResourceCatalogClient, options.ManagementApi);
        AddClient(services, EntryAdministrationApiClient.FlowResourceCatalogClient, options.FlowApi);
        AddClient<FlowApiClient, IFlowApiClient>(services, options.FlowApi);
        AddClient<ToolGovernanceAuditApiClient, IToolGovernanceAuditClient>(services, options.RuntimeApi);
        AddClient<ManagementApiClient, IManagementApiClient>(services, options.ManagementApi);
        AddClient<HttpUserPreferencesClient, IUserPreferencesClient>(services, options.ManagementApi);
        AddClient<ModelProvidersApiClient, IModelProvidersClient>(services, options.ManagementApi);
        AddClient<ExtensionsApiClient, IExtensionsClient>(services, options.ManagementApi);
        AddClient<SourceProvidersApiClient, ISourceProvidersClient>(services, options.ManagementApi);
        AddClient<ModelProfilesApiClient, IModelProfilesClient>(services, options.ManagementApi);
        AddClient<AgentsModelApiClient, IAgentsModelClient>(services, options.ManagementApi);
        AddClient<RuntimeProfilesApiClient, IRuntimeProfilesClient>(services, options.ManagementApi);
        AddClient<PacksApiClient, IPacksClient>(services, options.ManagementApi);
        AddClient<ToolsApiClient, IToolsClient>(services, options.ManagementApi);
        AddSensitiveClient<SecretsApiClient, ISecretsClient>(services, options.ManagementApi);
        AddClient<ResourceScopeInventoryApiClient, IResourceScopeInventoryClient>(services, options.ManagementApi);
        AddClient<ManagementApiClient, IAgentRunnerManagementClient>(services, options.ManagementApi);
        AddClient<RuntimeApiClient, IAgentRunnerRuntimeClient>(services, options.RuntimeApi);
        return services;
    }

    internal static IServiceCollection AddAgentstrationConsoleRealtimeClient(
        this IServiceCollection services,
        AgentstrationWebOptions options)
    {
        services.AddSingleton<ConsoleRealtimeSession>();
        services.AddScoped<IWorkOperationsRealtimeClient>(provider => new WorkOperationsRealtimeClient(
            new Uri(new Uri(options.WorkApi.BaseAddress, UriKind.Absolute), "hubs/workplace"),
            provider.GetRequiredService<ConsoleRealtimeSession>(),
            provider.GetRequiredService<ILogger<WorkOperationsRealtimeClient>>()));
        return services;
    }

    private static void AddClient<TImplementation, TContract>(
        IServiceCollection services,
        ApiEndpointOptions options)
        where TImplementation : class, TContract
        where TContract : class =>
        Configure(services.AddHttpClient<TContract, TImplementation>(), options);

    private static void AddClient(
        IServiceCollection services,
        string name,
        ApiEndpointOptions options) =>
        Configure(services.AddHttpClient(name), options);

    private static void AddSensitiveClient<TImplementation, TContract>(
        IServiceCollection services,
        ApiEndpointOptions options)
        where TImplementation : class, TContract
        where TContract : class =>
        ConfigureClient(services.AddHttpClient<TContract, TImplementation>(), options);

    private static void Configure(IHttpClientBuilder builder, ApiEndpointOptions options)
    {
        ConfigureClient(builder, options).AddStandardResilienceHandler(resilience =>
        {
            resilience.AttemptTimeout.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
            resilience.TotalRequestTimeout.Timeout =
                TimeSpan.FromSeconds(Math.Min(120, options.TimeoutSeconds * 3));
            resilience.Retry.MaxRetryAttempts = 2;
        });
    }

    private static IHttpClientBuilder ConfigureClient(
        IHttpClientBuilder builder,
        ApiEndpointOptions options)
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
            builder.ConfigurePrimaryHttpMessageHandler(() =>
                new HttpClientHandler { AllowAutoRedirect = false });
            builder.AddHttpMessageHandler(provider => new ConsoleApiSessionHandler(
                provider.GetRequiredService<IHttpContextAccessor>(),
                provider.GetRequiredService<ICurrentRequestContext>(),
                baseAddress,
                AgentstrationAuthenticationDefaults.ApplicationCookie));
        }
        return builder;
    }
}
