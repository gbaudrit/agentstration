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
    private readonly IReadOnlyDictionary<string, IBootstrapResourceHandler> handlers = resourceHandlers
        .ToDictionary(handler => handler.Kind, StringComparer.Ordinal);

    public async Task<int> ApplyAsync(CancellationToken cancellationToken)
    {
        var configuredPath = configuration["Agentstration:Bootstrap:Path"];
        if (string.IsNullOrWhiteSpace(configuredPath)) return 0;

        var path = Path.IsPathRooted(configuredPath)
            ? Path.GetFullPath(configuredPath)
            : Path.GetFullPath(configuredPath, environment.ContentRootPath);
        if (!Directory.Exists(path)) return 0;

        var files = Directory.EnumerateFiles(path, "*", SearchOption.TopDirectoryOnly)
            .Where(file => string.Equals(Path.GetExtension(file), ".yaml", StringComparison.OrdinalIgnoreCase)
                || string.Equals(Path.GetExtension(file), ".yml", StringComparison.OrdinalIgnoreCase))
            .OrderBy(Path.GetFileName, StringComparer.Ordinal)
            .ToArray();
        var applied = 0;
        foreach (var file in files)
        {
            var fileName = Path.GetFileName(file);
            IReadOnlyList<BootstrapResourceDocument> resources;
            try
            {
                resources = ResourceManifestSerializer.FromYamlDocuments<BootstrapResourceDocument>(
                    await File.ReadAllTextAsync(file, cancellationToken));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw new DeclarativeBootstrapException(
                    $"Bootstrap file '{fileName}' contains invalid YAML or an invalid resource envelope.", exception);
            }

            for (var index = 0; index < resources.Count; index++)
            {
                var resource = resources[index];
                var location = $"{fileName} document {index + 1}";
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
                        if (result == BootstrapResourceApplyResult.Created)
                            logger.LogInformation(
                                "Created bootstrap resource {BootstrapKind}/{BootstrapName} from {BootstrapFile}",
                                resource.Kind,
                                resource.Metadata.Name,
                                fileName);
                        else
                            logger.LogInformation(
                                "Skipped existing bootstrap resource {BootstrapKind}/{BootstrapName} from {BootstrapFile}",
                                resource.Kind,
                                resource.Metadata.Name,
                                fileName);
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
