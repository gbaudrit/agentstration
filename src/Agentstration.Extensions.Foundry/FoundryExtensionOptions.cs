using System.Net;
using Microsoft.Extensions.Configuration;

namespace Agentstration.Extensions.Foundry;

public enum FoundryAuthenticationMode
{
    ApiKeyEnvironment,
    ManagedIdentity,
    WorkloadIdentity,
    Development
}

public sealed record FoundryExtensionOptions
{
    public required Uri ProjectEndpoint { get; init; }
    public required Uri InferenceEndpoint { get; init; }
    public required FoundryAuthenticationMode AuthenticationMode { get; init; }
    public string? ManagedIdentityClientId { get; init; }
    public IReadOnlySet<string> AllowedPrivateHosts { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public int MaximumDiscoveryPages { get; init; } = 10;
    public int MaximumDiscoveredModels { get; init; } = 200;
    public int MaximumDiscoveryResponseBytes { get; init; } = 1024 * 1024;
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public static FoundryExtensionOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (!Enum.TryParse<FoundryAuthenticationMode>(configuration["Foundry:AuthenticationMode"], true, out var mode)
            || !Enum.IsDefined(mode))
            throw new InvalidOperationException("Foundry:AuthenticationMode must explicitly select ApiKeyEnvironment, ManagedIdentity, WorkloadIdentity, or Development.");
        if (!Uri.TryCreate(configuration["Foundry:ProjectEndpoint"], UriKind.Absolute, out var project)
            || !Uri.TryCreate(configuration["Foundry:InferenceEndpoint"], UriKind.Absolute, out var inference))
            throw new InvalidOperationException("Foundry project and inference endpoints must be absolute URLs.");

        var hosts = (configuration["Foundry:AllowedPrivateHosts"] ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var options = new FoundryExtensionOptions
        {
            ProjectEndpoint = project,
            InferenceEndpoint = inference,
            AuthenticationMode = mode,
            ManagedIdentityClientId = configuration["Foundry:ManagedIdentityClientId"],
            AllowedPrivateHosts = new HashSet<string>(hosts, StringComparer.OrdinalIgnoreCase),
            MaximumDiscoveryPages = ReadInt(configuration, "Foundry:MaximumDiscoveryPages", 10),
            MaximumDiscoveredModels = ReadInt(configuration, "Foundry:MaximumDiscoveredModels", 200),
            MaximumDiscoveryResponseBytes = ReadInt(configuration, "Foundry:MaximumDiscoveryResponseBytes", 1024 * 1024),
            RequestTimeout = TimeSpan.FromSeconds(ReadInt(configuration, "Foundry:RequestTimeoutSeconds", 30))
        };
        options.Validate();
        return options;
    }

    public void Validate()
    {
        ValidateEndpoint(ProjectEndpoint, "project");
        ValidateEndpoint(InferenceEndpoint, "inference");
        var projectSegments = ProjectEndpoint.AbsolutePath.TrimEnd('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (projectSegments.Length != 3 || projectSegments[0] != "api" || projectSegments[1] != "projects" || string.IsNullOrWhiteSpace(projectSegments[2]))
            throw new InvalidOperationException("The Foundry project endpoint must end in /api/projects/{project-name}.");
        var inferencePath = InferenceEndpoint.AbsolutePath.TrimEnd('/');
        if (inferencePath != "/openai/v1" && inferencePath != ProjectEndpoint.AbsolutePath.TrimEnd('/') + "/openai/v1")
            throw new InvalidOperationException("The Foundry inference endpoint must end in /openai/v1 or the configured project route /openai/v1.");
        if (MaximumDiscoveryPages is < 1 or > 50 || MaximumDiscoveredModels is < 1 or > 1000
            || MaximumDiscoveryResponseBytes is < 1024 or > 8 * 1024 * 1024
            || RequestTimeout < TimeSpan.FromSeconds(1) || RequestTimeout > TimeSpan.FromMinutes(2))
            throw new InvalidOperationException("Foundry discovery and timeout limits are outside their supported bounds.");
        foreach (var host in AllowedPrivateHosts)
        {
            if (string.IsNullOrWhiteSpace(host) || host.Contains('/') || host.Contains(':') || host.Contains('@'))
                throw new InvalidOperationException("Foundry allowed private hosts must be plain DNS names.");
            if (!string.Equals(host, ProjectEndpoint.IdnHost, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(host, InferenceEndpoint.IdnHost, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Foundry private-host allowances must name a configured endpoint host.");
        }
        if (!Enum.IsDefined(AuthenticationMode))
            throw new InvalidOperationException("Unsupported Foundry authentication mode.");
        if (AuthenticationMode != FoundryAuthenticationMode.ManagedIdentity && ManagedIdentityClientId is not null)
            throw new InvalidOperationException("Foundry:ManagedIdentityClientId applies only to ManagedIdentity mode.");
        if (ManagedIdentityClientId is not null && !Guid.TryParse(ManagedIdentityClientId, out _))
            throw new InvalidOperationException("Foundry:ManagedIdentityClientId must be a client ID GUID.");
    }

    public Uri DeploymentsEndpoint() => new(ProjectEndpoint.AbsoluteUri.TrimEnd('/') + "/deployments?api-version=v1&deploymentType=ModelDeployment", UriKind.Absolute);

    private static int ReadInt(IConfiguration configuration, string key, int fallback) =>
        configuration[key] is { } value
            ? int.TryParse(value, out var parsed) ? parsed : throw new InvalidOperationException($"{key} must be an integer.")
            : fallback;

    private static void ValidateEndpoint(Uri endpoint, string name)
    {
        if (!endpoint.IsAbsoluteUri || endpoint.Scheme != Uri.UriSchemeHttps || endpoint.Host.Length == 0
            || endpoint.UserInfo.Length > 0 || endpoint.Query.Length > 0 || endpoint.Fragment.Length > 0
            || IPAddress.TryParse(endpoint.IdnHost, out _))
            throw new InvalidOperationException($"The Foundry {name} endpoint must be a credential-free HTTPS DNS URL without query or fragment.");
    }
}
