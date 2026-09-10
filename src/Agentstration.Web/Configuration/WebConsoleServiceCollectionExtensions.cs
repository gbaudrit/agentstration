namespace Agentstration.Web.Configuration;

public static class WebConsoleServiceCollectionExtensions
{
    public static IServiceCollection AddAgentstrationWebConsole(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        services.AddOptions<AgentstrationWebOptions>()
            .Bind(configuration.GetSection(AgentstrationWebOptions.SectionName))
            .Validate(Validate, "API base addresses must be absolute HTTP(S) URIs and timeouts must be between 1 and 120 seconds.")
            .ValidateOnStart();

        var configured = configuration.GetSection(AgentstrationWebOptions.SectionName)
            .Get<AgentstrationWebOptions>() ?? new();
        return services
            .AddAgentstrationConsoleComponents()
            .AddAgentstrationConsoleClients(configured)
            .AddAgentstrationConsoleRealtimeClient(configured)
            .AddAgentstrationConsoleAuthentication(configured.Authentication, environment)
            .AddAgentstrationConsoleAuthorization();
    }

    private static bool Validate(AgentstrationWebOptions options) =>
        ValidateEndpoint(options.WorkApi)
        && ValidateEndpoint(options.ManagementApi)
        && ValidateEndpoint(options.RuntimeApi)
        && ValidateEndpoint(options.FlowApi)
        && (string.IsNullOrWhiteSpace(options.WorkplaceBaseUrl)
            || Uri.TryCreate(options.WorkplaceBaseUrl, UriKind.Absolute, out var workplace)
            && workplace.Scheme is "http" or "https");

    private static bool ValidateEndpoint(ApiEndpointOptions options) =>
        options.TimeoutSeconds is >= 1 and <= 120
        && Uri.TryCreate(options.BaseAddress, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
