using Agentstration.Web.Components;
using Agentstration.Web.Components.State;
using Agentstration.Web.Console;
using Agentstration.Web.Features.Flows.Designer;
using Agentstration.Web.FlowDesigner.Backend;
using Agentstration.Web.FlowDesigner.DependencyInjection;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Agentstration.Web.Configuration;

public static class WebConsoleServiceCollectionExtensions
{
    public static IServiceCollection AddAgentstrationWebConsole(this IServiceCollection services, IConfiguration configuration)
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
        AddClient<ManagementApiClient, IAgentRunnerManagementClient>(services, configured.ManagementApi);
        AddClient<RuntimeApiClient, IAgentRunnerRuntimeClient>(services, configured.RuntimeApi);

        services.AddAuthentication(DevelopmentAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, DevelopmentAuthenticationHandler>(DevelopmentAuthenticationHandler.SchemeName, _ => { });
        services.AddAuthorizationBuilder()
            .AddPolicy("Viewer", policy => policy.RequireRole("Viewer", "Operator", "Administrator"))
            .AddPolicy("Operator", policy => policy.RequireRole("Operator", "Administrator"))
            .AddPolicy("Administrator", policy => policy.RequireRole("Administrator"));
        return services;
    }

    private static void AddClient<TImplementation, TContract>(IServiceCollection services, ApiEndpointOptions options)
        where TImplementation : class, TContract
        where TContract : class
    {
        Configure(services.AddHttpClient<TContract, TImplementation>(), options);
    }

    private static void AddClient(IServiceCollection services, string name, ApiEndpointOptions options) =>
        Configure(services.AddHttpClient(name), options);

    private static void Configure(IHttpClientBuilder builder, ApiEndpointOptions options)
    {
        builder.ConfigureHttpClient(client =>
        {
            client.BaseAddress = new Uri(options.BaseAddress, UriKind.Absolute);
            client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
            client.DefaultRequestHeaders.Add("X-Agentstration-Client", "Agentstration.Web");
        }).AddStandardResilienceHandler(resilience =>
        {
            resilience.AttemptTimeout.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
            resilience.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(Math.Min(120, options.TimeoutSeconds * 3));
            resilience.Retry.MaxRetryAttempts = 2;
        });
    }

    private static bool Validate(AgentstrationWebOptions options) => ValidateEndpoint(options.WorkApi) && (options.UseSimulatedData ||
        ValidateEndpoint(options.ManagementApi) && ValidateEndpoint(options.RuntimeApi) && ValidateEndpoint(options.FlowApi)) &&
        (string.IsNullOrWhiteSpace(options.WorkplaceBaseUrl) || Uri.TryCreate(options.WorkplaceBaseUrl, UriKind.Absolute, out var workplace) && workplace.Scheme is "http" or "https");

    private static bool ValidateEndpoint(ApiEndpointOptions options) =>
        options.TimeoutSeconds is >= 1 and <= 120 &&
        Uri.TryCreate(options.BaseAddress, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
