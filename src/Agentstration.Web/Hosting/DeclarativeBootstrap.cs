using System.Security.Cryptography;
using System.Text;
using Agentstration.Management.Abstractions;

namespace Agentstration.Web.Hosting;

public sealed class DeclarativeBootstrapException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException);

public sealed record BootstrapProfileSelection(IReadOnlyList<string> Profiles, BootstrapApplicationTarget? Target = null);

public sealed record BootstrapResourcePreview(
    string Profile,
    string Location,
    string Kind,
    string Name,
    BootstrapResourceDisposition Disposition,
    string? Message = null,
    IReadOnlyList<BootstrapResourcePlanDetail>? Details = null);

public sealed record BootstrapCompositionPreview(
    IReadOnlyList<BootstrapProfileSummary> Profiles,
    BootstrapProfileScope Scope,
    BootstrapApplicationTarget? Target,
    string Digest,
    IReadOnlyList<BootstrapResourcePreview> Resources)
{
    public bool CanApply => Resources.All(resource => resource.Disposition != BootstrapResourceDisposition.Invalid);
}

public sealed record BootstrapExecutionResult(
    BootstrapCompositionPreview Preview,
    IReadOnlyList<BootstrapAppliedResource> Resources,
    string? Error = null);

public sealed class DeclarativeBootstrapService(
    IConfiguration configuration,
    BootstrapProfileCatalog catalog,
    IEnumerable<IBootstrapResourceHandler> resourceHandlers,
    ILogger<DeclarativeBootstrapService> logger)
{
    private const string ConfigurationSection = "Agentstration:Bootstrap";
    private readonly IReadOnlyDictionary<string, IBootstrapResourceHandler> handlers = resourceHandlers
        .ToDictionary(handler => handler.Kind, StringComparer.Ordinal);

    public async Task<int> ApplyAsync(CancellationToken cancellationToken)
    {
        var options = configuration.GetSection(ConfigurationSection).Get<DeclarativeBootstrapOptions>() ?? new();
        if (!options.InitialBootstrapEnabled || options.InitialProfiles.Count == 0) return 0;
        return await ApplyProfilesAsync(options.InitialProfiles, cancellationToken);
    }

    public async Task<int> ApplyProfilesAsync(IReadOnlyList<string> profiles, CancellationToken cancellationToken)
    {
        if (profiles.Count == 0) return 0;
        var execution = await ExecuteAsync(new(profiles), cancellationToken);
        if (execution.Error is not null) throw new DeclarativeBootstrapException(execution.Error);
        return execution.Resources.Count;
    }

    public async Task<BootstrapCompositionPreview> PreviewAsync(
        BootstrapProfileSelection selection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (selection.Profiles.Count == 0)
            throw new DeclarativeBootstrapException("At least one bootstrap profile must be selected.");
        var profiles = await catalog.LoadAsync(selection.Profiles, cancellationToken);
        var scope = profiles[0].Summary.Scope;
        var incompatible = profiles.FirstOrDefault(profile => profile.Summary.Scope != scope);
        if (incompatible is not null)
            throw new DeclarativeBootstrapException(
                $"Bootstrap profile '{incompatible.Summary.Name}' has scope '{incompatible.Summary.Scope}' and cannot be composed with scope '{scope}'.");
        ValidateTarget(scope, selection.Target);

        var planning = new BootstrapPlanningContext();
        var resources = new List<BootstrapResourcePreview>();
        foreach (var profile in profiles)
        {
            var operation = new BootstrapResourceOperationContext(profile.Summary.Name, profile.DirectoryPath, profile.Summary.Scope, selection.Target);
            foreach (var source in profile.Resources)
            {
                if (!handlers.TryGetValue(source.Resource.Kind, out var handler))
                {
                    resources.Add(new(profile.Summary.Name, source.Location, source.Resource.Kind, source.Resource.Metadata.Name,
                        BootstrapResourceDisposition.Invalid, $"Unknown bootstrap resource kind '{source.Resource.Kind}'."));
                    continue;
                }
                if (handler.Scope != profile.Summary.Scope)
                {
                    resources.Add(new(profile.Summary.Name, source.Location, source.Resource.Kind, source.Resource.Metadata.Name,
                        BootstrapResourceDisposition.Invalid, $"Resource scope '{handler.Scope}' does not match profile scope '{profile.Summary.Scope}'."));
                    continue;
                }
                try
                {
                    var plan = await handler.PlanAsync(source.Resource, operation, planning, cancellationToken);
                    resources.Add(new(profile.Summary.Name, source.Location, source.Resource.Kind, source.Resource.Metadata.Name,
                        plan.Disposition, Details: plan.Details));
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    resources.Add(new(profile.Summary.Name, source.Location, source.Resource.Kind, source.Resource.Metadata.Name,
                        BootstrapResourceDisposition.Invalid, exception.Message));
                }
            }
        }

        return new(
            profiles.Select(profile => profile.Summary).ToArray(),
            scope,
            selection.Target,
            ComputeDigest(profiles, scope, selection.Target),
            resources);
    }

    public async Task<BootstrapExecutionResult> ExecuteAsync(
        BootstrapProfileSelection selection,
        CancellationToken cancellationToken)
    {
        var preview = await PreviewAsync(selection, cancellationToken);
        var invalid = preview.Resources.FirstOrDefault(resource => resource.Disposition == BootstrapResourceDisposition.Invalid);
        if (invalid is not null)
            return new(preview, [], $"Bootstrap resource '{invalid.Kind}/{invalid.Name}' from '{invalid.Location}' is invalid: {invalid.Message}");

        var loaded = await catalog.LoadAsync(selection.Profiles, cancellationToken);
        var executionDigest = ComputeDigest(loaded, preview.Scope, selection.Target);
        if (!string.Equals(executionDigest, preview.Digest, StringComparison.Ordinal))
            return new(preview, [], "The bootstrap catalog changed while the application was being prepared. Preview the application again.");
        var resources = new List<BootstrapAppliedResource>();
        foreach (var profile in loaded)
        {
            var operation = new BootstrapResourceOperationContext(profile.Summary.Name, profile.DirectoryPath, profile.Summary.Scope, selection.Target);
            foreach (var source in profile.Resources)
            {
                try
                {
                    var result = await handlers[source.Resource.Kind].ApplyAsync(source.Resource, operation, cancellationToken);
                    var disposition = result switch
                    {
                        BootstrapResourceApplyResult.Created => BootstrapResourceDisposition.Create,
                        BootstrapResourceApplyResult.Skipped => BootstrapResourceDisposition.Skip,
                        BootstrapResourceApplyResult.Conflict => BootstrapResourceDisposition.Conflict,
                        _ => throw new InvalidOperationException($"Unsupported bootstrap result '{result}'.")
                    };
                    resources.Add(new(profile.Summary.Name, source.Location, source.Resource.Kind, source.Resource.Metadata.Name, disposition));
                    LogResult(result, source.Resource, source.Location);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    var message = $"Bootstrap resource '{source.Resource.Kind}/{source.Resource.Metadata.Name}' from '{source.Location}' failed: {exception.Message}";
                    resources.Add(new(profile.Summary.Name, source.Location, source.Resource.Kind, source.Resource.Metadata.Name,
                        BootstrapResourceDisposition.Failed, exception.Message));
                    return new(preview, resources, message);
                }
            }
        }
        return new(preview, resources);
    }

    private static void ValidateTarget(BootstrapProfileScope scope, BootstrapApplicationTarget? target)
    {
        if (scope == BootstrapProfileScope.Instance && target is not null)
            throw new DeclarativeBootstrapException("Instance bootstrap profiles cannot receive a Tenant or Workspace target.");
        if (scope == BootstrapProfileScope.Tenant && target?.TenantId is null)
            throw new DeclarativeBootstrapException("Tenant bootstrap profiles require a Tenant target.");
        if (scope == BootstrapProfileScope.Workspace && (target?.TenantId is null || target.WorkspaceId is null))
            throw new DeclarativeBootstrapException("Workspace bootstrap profiles require a Tenant and Workspace target.");
    }

    private static string ComputeDigest(
        IReadOnlyList<LoadedBootstrapProfile> profiles,
        BootstrapProfileScope scope,
        BootstrapApplicationTarget? target)
    {
        var lines = new List<string>
        {
            scope.ToString(),
            target?.TenantId?.ToString("D") ?? string.Empty,
            target?.WorkspaceId?.ToString("D") ?? string.Empty
        };
        lines.AddRange(profiles.Select(profile => $"{profile.Summary.Name}:{profile.Summary.Digest}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', lines)))).ToLowerInvariant();
    }

    private void LogResult(BootstrapResourceApplyResult result, BootstrapResourceDocument resource, string location)
    {
        if (!logger.IsEnabled(LogLevel.Information)) return;
        switch (result)
        {
            case BootstrapResourceApplyResult.Created:
                logger.LogInformation("Created bootstrap resource {BootstrapKind}/{BootstrapName} from {BootstrapFile}", resource.Kind, resource.Metadata.Name, location);
                break;
            case BootstrapResourceApplyResult.Skipped:
                logger.LogInformation("Skipped existing bootstrap resource {BootstrapKind}/{BootstrapName} from {BootstrapFile}", resource.Kind, resource.Metadata.Name, location);
                break;
            case BootstrapResourceApplyResult.Conflict:
                logger.LogWarning("Skipped conflicting bootstrap resource {BootstrapKind}/{BootstrapName} from {BootstrapFile}; existing state was preserved", resource.Kind, resource.Metadata.Name, location);
                break;
        }
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
    public static async Task ApplyDeclarativeBootstrapAsync(this IServiceProvider services, CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<DeclarativeBootstrapService>().ApplyAsync(cancellationToken);
    }
}
