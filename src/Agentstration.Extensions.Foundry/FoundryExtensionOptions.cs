using System.Net;
using Microsoft.Extensions.Configuration;

namespace Agentstration.Extensions.Foundry;

public enum FoundryAuthenticationMode { ApiKey, ManagedIdentity, WorkloadIdentity, Development }

public sealed record FoundryConnection
{
    public required Uri ProjectEndpoint { get; init; }
    public required Uri InferenceEndpoint { get; init; }
    public required FoundryAuthenticationMode AuthenticationMode { get; init; }
    public string? Credential { get; init; }
    public string? ManagedIdentityClientId { get; init; }
    public string? WorkloadIdentityTenantId { get; init; }
    public string? WorkloadIdentityClientId { get; init; }
    public string? WorkloadIdentityTokenFile { get; init; }

    public void Validate()
    {
        ValidateEndpoint(ProjectEndpoint, "project");
        ValidateEndpoint(InferenceEndpoint, "inference");
        var projectSegments = ProjectEndpoint.AbsolutePath.TrimEnd('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (projectSegments.Length != 3 || projectSegments[0] != "api" || projectSegments[1] != "projects" || string.IsNullOrWhiteSpace(projectSegments[2]))
            throw Invalid("The Foundry project endpoint must end in /api/projects/{project-name}.");
        var inferencePath = InferenceEndpoint.AbsolutePath.TrimEnd('/');
        if (inferencePath != "/openai/v1" && inferencePath != ProjectEndpoint.AbsolutePath.TrimEnd('/') + "/openai/v1")
            throw Invalid("The Foundry inference endpoint must end in /openai/v1 or the configured project route /openai/v1.");
        if (!Enum.IsDefined(AuthenticationMode)) throw Invalid("The Foundry authentication mode is unsupported.");
        switch (AuthenticationMode)
        {
            case FoundryAuthenticationMode.ApiKey:
                if (string.IsNullOrWhiteSpace(Credential) || Credential.Length > 8192 || Credential.Any(char.IsWhiteSpace) || Credential.Any(char.IsControl))
                    throw Invalid("ApiKey mode requires a bounded, single-line credential.");
                RejectIdentityValues();
                break;
            case FoundryAuthenticationMode.ManagedIdentity:
                RejectCredential();
                RejectWorkloadIdentityValues();
                if (ManagedIdentityClientId is not null && !Guid.TryParse(ManagedIdentityClientId, out _))
                    throw Invalid("managedIdentityClientId must be a client ID GUID.");
                break;
            case FoundryAuthenticationMode.WorkloadIdentity:
                RejectCredential();
                if (ManagedIdentityClientId is not null) throw Invalid("managedIdentityClientId applies only to ManagedIdentity mode.");
                if (!Guid.TryParse(WorkloadIdentityTenantId, out _) || !Guid.TryParse(WorkloadIdentityClientId, out _)
                    || string.IsNullOrWhiteSpace(WorkloadIdentityTokenFile) || !Path.IsPathFullyQualified(WorkloadIdentityTokenFile))
                    throw Invalid("WorkloadIdentity mode requires tenant ID, client ID, and an absolute token file path.");
                break;
            case FoundryAuthenticationMode.Development:
                RejectCredential();
                RejectIdentityValues();
                break;
        }
    }

    public Uri DeploymentsEndpoint() => new(ProjectEndpoint.AbsoluteUri.TrimEnd('/') + "/deployments?api-version=v1&deploymentType=ModelDeployment", UriKind.Absolute);

    private void RejectCredential()
    {
        if (Credential is not null) throw Invalid("credential applies only to ApiKey mode.");
    }
    private void RejectIdentityValues()
    {
        if (ManagedIdentityClientId is not null || WorkloadIdentityTenantId is not null || WorkloadIdentityClientId is not null || WorkloadIdentityTokenFile is not null)
            throw Invalid("Identity values do not apply to the selected authentication mode.");
    }
    private void RejectWorkloadIdentityValues()
    {
        if (WorkloadIdentityTenantId is not null || WorkloadIdentityClientId is not null || WorkloadIdentityTokenFile is not null)
            throw Invalid("Workload identity values apply only to WorkloadIdentity mode.");
    }
    private static void ValidateEndpoint(Uri endpoint, string name)
    {
        if (!endpoint.IsAbsoluteUri || endpoint.Scheme != Uri.UriSchemeHttps || endpoint.Host.Length == 0
            || endpoint.UserInfo.Length > 0 || endpoint.Query.Length > 0 || endpoint.Fragment.Length > 0 || IPAddress.TryParse(endpoint.IdnHost, out _))
            throw Invalid($"The Foundry {name} endpoint must be a credential-free HTTPS DNS URL without query or fragment.");
    }
    private static InvalidOperationException Invalid(string message) => new(message);
}

public sealed record FoundryExtensionOptions
{
    public IReadOnlySet<string> AllowedPrivateHosts { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public int MaximumDiscoveryPages { get; init; } = 10;
    public int MaximumDiscoveredModels { get; init; } = 200;
    public int MaximumDiscoveryResponseBytes { get; init; } = 1024 * 1024;
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public static FoundryExtensionOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var hosts = (configuration["Foundry:AllowedPrivateHosts"] ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var options = new FoundryExtensionOptions
        {
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
        if (MaximumDiscoveryPages is < 1 or > 50 || MaximumDiscoveredModels is < 1 or > 1000
            || MaximumDiscoveryResponseBytes is < 1024 or > 8 * 1024 * 1024 || RequestTimeout < TimeSpan.FromSeconds(1) || RequestTimeout > TimeSpan.FromMinutes(2))
            throw new InvalidOperationException("Foundry discovery and timeout limits are outside their supported bounds.");
        foreach (var host in AllowedPrivateHosts)
            if (string.IsNullOrWhiteSpace(host) || host.Contains('/') || host.Contains(':') || host.Contains('@') || IPAddress.TryParse(host, out _))
                throw new InvalidOperationException("Foundry allowed private hosts must be plain DNS names.");
    }

    private static int ReadInt(IConfiguration configuration, string key, int fallback) => configuration[key] is { } value
        ? int.TryParse(value, out var parsed) ? parsed : throw new InvalidOperationException($"{key} must be an integer.") : fallback;
}
