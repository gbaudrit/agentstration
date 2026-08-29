using Agentstration.Management.Abstractions;
using Agentstration.Management.Contracts;

namespace Agentstration.Web.Hosting;

public sealed class DeclarativeBootstrapException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException);

public sealed class DeclarativeBootstrapService(
    IConfiguration configuration,
    IHostEnvironment environment,
    IEnumerable<IBootstrapResourceHandler> resourceHandlers,
    ILogger<DeclarativeBootstrapService> logger)
{
    private const string ConfigurationSection = "Agentstration:Bootstrap";

    private readonly IReadOnlyDictionary<string, IBootstrapResourceHandler> handlers = resourceHandlers
        .ToDictionary(handler => handler.Kind, StringComparer.Ordinal);

    public async Task<int> ApplyAsync(CancellationToken cancellationToken)
    {
        var options = configuration.GetSection(ConfigurationSection).Get<DeclarativeBootstrapOptions>() ?? new();
        if (!options.InitialBootstrapEnabled) return 0;
        if (options.InitialProfiles.Count == 0) return 0;

        return await ApplyProfilesAsync(options.InitialProfiles, cancellationToken);
    }

    public async Task<int> ApplyProfilesAsync(
        IReadOnlyList<string> profiles,
        CancellationToken cancellationToken)
    {
        if (profiles.Count == 0) return 0;

        var configuredPath = configuration[$"{ConfigurationSection}:Path"];
        if (string.IsNullOrWhiteSpace(configuredPath))
            throw new DeclarativeBootstrapException(
                $"{ConfigurationSection}:Path is required when bootstrap profiles are selected.");

        var rootPath = Path.IsPathRooted(configuredPath)
            ? Path.GetFullPath(configuredPath)
            : Path.GetFullPath(configuredPath, environment.ContentRootPath);
        if (!Directory.Exists(rootPath))
            throw new DeclarativeBootstrapException($"Bootstrap root directory '{rootPath}' does not exist.");

        var distinctProfiles = new HashSet<string>(StringComparer.Ordinal);
        var applied = 0;
        foreach (var profile in profiles)
        {
            ValidateProfileName(profile);
            if (!distinctProfiles.Add(profile))
                throw new DeclarativeBootstrapException($"Bootstrap profile '{profile}' is configured more than once.");

            var profilePath = Path.Combine(rootPath, profile);
            if (!Directory.Exists(profilePath))
                throw new DeclarativeBootstrapException(
                    $"Bootstrap profile '{profile}' does not exist under root directory '{rootPath}'.");

            applied += await ApplyProfileAsync(profile, profilePath, cancellationToken);
        }

        return applied;
    }

    private async Task<int> ApplyProfileAsync(
        string profile,
        string profilePath,
        CancellationToken cancellationToken)
    {
        var files = Directory.EnumerateFiles(profilePath, "*", SearchOption.TopDirectoryOnly)
            .Where(file => string.Equals(Path.GetExtension(file), ".yaml", StringComparison.OrdinalIgnoreCase)
                || string.Equals(Path.GetExtension(file), ".yml", StringComparison.OrdinalIgnoreCase))
            .OrderBy(Path.GetFileName, StringComparer.Ordinal)
            .ToArray();

        var applied = 0;
        foreach (var file in files)
        {
            var fileName = Path.GetFileName(file);
            var profileFile = $"{profile}/{fileName}";
            IReadOnlyList<BootstrapResourceDocument> resources;
            try
            {
                resources = ResourceManifestSerializer.FromYamlDocuments<BootstrapResourceDocument>(
                    await File.ReadAllTextAsync(file, cancellationToken));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw new DeclarativeBootstrapException(
                    $"Bootstrap file '{profileFile}' contains invalid YAML or an invalid resource envelope.", exception);
            }

            for (var index = 0; index < resources.Count; index++)
            {
                var resource = resources[index];
                var location = $"{profileFile} document {index + 1}";
                ValidateEnvelope(resource, location);
                if (!handlers.TryGetValue(resource.Kind, out var handler))
                    throw new DeclarativeBootstrapException(
                        $"Bootstrap resource '{location}' uses unknown kind '{resource.Kind}'.");
                try
                {
                    var result = await handler.ApplyAsync(resource, cancellationToken);
                    applied++;
                    if (logger.IsEnabled(LogLevel.Information))
                    {
                        switch (result)
                        {
                            case BootstrapResourceApplyResult.Created:
                                logger.LogInformation(
                                    "Created bootstrap resource {BootstrapKind}/{BootstrapName} from {BootstrapFile}",
                                    resource.Kind,
                                    resource.Metadata.Name,
                                    profileFile);
                                break;
                            case BootstrapResourceApplyResult.Skipped:
                                logger.LogInformation(
                                    "Skipped existing bootstrap resource {BootstrapKind}/{BootstrapName} from {BootstrapFile}",
                                    resource.Kind,
                                    resource.Metadata.Name,
                                    profileFile);
                                break;
                            case BootstrapResourceApplyResult.Conflict:
                                logger.LogWarning(
                                    "Skipped conflicting bootstrap resource {BootstrapKind}/{BootstrapName} from {BootstrapFile}; existing state was preserved",
                                    resource.Kind,
                                    resource.Metadata.Name,
                                    profileFile);
                                break;
                        }
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException and not DeclarativeBootstrapException)
                {
                    throw new DeclarativeBootstrapException(
                        $"Bootstrap resource '{resource.Kind}/{resource.Metadata.Name}' from '{location}' failed: {exception.Message}",
                        exception);
                }
            }
        }
        return applied;
    }

    private static void ValidateProfileName(string profile)
    {
        if (string.IsNullOrWhiteSpace(profile)
            || !string.Equals(profile, profile.Trim(), StringComparison.Ordinal)
            || string.Equals(profile, ".", StringComparison.Ordinal)
            || string.Equals(profile, "..", StringComparison.Ordinal)
            || profile.Contains('/')
            || profile.Contains('\\')
            || !string.Equals(profile, Path.GetFileName(profile), StringComparison.Ordinal)
            || profile.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new DeclarativeBootstrapException(
                $"Bootstrap profile name '{profile}' must be a single valid directory name.");
    }

    private static void ValidateEnvelope(BootstrapResourceDocument resource, string location)
    {
        if (string.IsNullOrWhiteSpace(resource.ApiVersion))
            throw new DeclarativeBootstrapException($"Bootstrap resource '{location}' is missing apiVersion.");
        if (!string.Equals(resource.ApiVersion, ManagementApiVersions.CoreV1, StringComparison.Ordinal))
            throw new DeclarativeBootstrapException(
                $"Bootstrap resource '{location}' uses unsupported apiVersion '{resource.ApiVersion}'. Expected '{ManagementApiVersions.CoreV1}'.");
        if (string.IsNullOrWhiteSpace(resource.Kind))
            throw new DeclarativeBootstrapException($"Bootstrap resource '{location}' is missing kind.");
        if (string.IsNullOrWhiteSpace(resource.Metadata.Name))
            throw new DeclarativeBootstrapException($"Bootstrap resource '{location}' is missing metadata.name.");
        if (resource.Definition.ValueKind != System.Text.Json.JsonValueKind.Object)
            throw new DeclarativeBootstrapException($"Bootstrap resource '{location}' requires an object definition.");
    }
}

public sealed class DeclarativeBootstrapOptions
{
    public string? Path { get; set; }
    public bool InitialBootstrapEnabled { get; set; }
    public List<string> InitialProfiles { get; set; } = [];
}

public static class DeclarativeBootstrapServiceProviderExtensions
{
    public static async Task ApplyDeclarativeBootstrapAsync(
        this IServiceProvider services,
        CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<DeclarativeBootstrapService>().ApplyAsync(cancellationToken);
    }
}
