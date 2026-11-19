using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agentstration.Bootstrap.Contracts;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Agentstration.Web.Hosting;

public sealed class DeclarativeBootstrapException(string message, Exception? innerException = null)
    : BootstrapRequestException(message, innerException);

public sealed partial class DeclarativeBootstrapService(
    IConfiguration configuration,
    BootstrapProfileCatalog catalog,
    IEnumerable<IBootstrapResourceHandler> resourceHandlers,
    ILogger<DeclarativeBootstrapService> logger,
    SourceBootstrapProfileLoader? sourceProfiles = null,
    IBootstrapExtensionReferenceResolver? extensionReferences = null)
{
    private readonly BootstrapExtensionReferencePreparation extensionPreparation = new(extensionReferences);
    private const string ConfigurationSection = "Agentstration:Bootstrap";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
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

    public Task<BootstrapCompositionPreview> PreviewAsync(BootstrapProfileSelection selection, CancellationToken cancellationToken) =>
        PreviewPhaseAsync(selection, BootstrapStartupPhase.All, cancellationToken);

    private async Task<BootstrapCompositionPreview> PreviewPhaseAsync(
        BootstrapProfileSelection selection, BootstrapStartupPhase phase, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (selection.Source is null && selection.Profiles.Count == 0)
            throw new DeclarativeBootstrapException("At least one bootstrap profile must be selected.");
        if (selection.Source is not null && selection.Profiles.Count != 0)
            throw new DeclarativeBootstrapException("Local and Source Bootstrap profiles cannot be composed in one application.");
        await using var loadedSelection = await LoadAsync(selection, cancellationToken, phase);
        var profiles = loadedSelection.Profiles;
        var scope = profiles[0].Summary.Scope;
        var incompatible = profiles.FirstOrDefault(profile => profile.Summary.Scope != scope);
        if (incompatible is not null)
            throw new DeclarativeBootstrapException(
                $"Bootstrap profile '{incompatible.Summary.Name}' has scope '{incompatible.Summary.Scope}' and cannot be composed with scope '{scope}'.");
        ValidateTarget(scope, selection.Target);
        var fingerprints = new List<string>();
        var bindings = await ResolveBindingsAsync(profiles, selection, fingerprints, cancellationToken);

        var planning = new BootstrapPlanningContext();
        var resources = new List<BootstrapResourcePreview>();
        foreach (var profile in profiles)
        {
            var operation = new BootstrapResourceOperationContext(profile.Summary.Name, profile.DirectoryPath, profile.Summary.Scope, selection.Target);
            foreach (var source in profile.Resources)
            {
                BootstrapResourceDocument resource;
                try
                {
                    resource = ResolveResource(source, profile, bindings);
                    resource = await extensionPreparation.ResolveDocumentAsync(resource,
                        handlers.TryGetValue(resource.Kind, out var owner) ? owner.ExtensionReferencePaths : [],
                        BootstrapExtensionReferencePreparation.ConsumerScope(operation), fingerprints, cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    resources.Add(new(profile.Summary.Name, source.Location, source.Resource.Kind, source.Resource.Metadata.Name,
                        BootstrapResourceDisposition.Invalid, exception.Message));
                    continue;
                }
                if (!handlers.TryGetValue(resource.Kind, out var handler))
                {
                    resources.Add(new(profile.Summary.Name, source.Location, resource.Kind, resource.Metadata.Name,
                        BootstrapResourceDisposition.Invalid, $"Unknown bootstrap resource kind '{resource.Kind}'."));
                    continue;
                }
                if (!handler.SupportsProfileScope(profile.Summary.Scope))
                {
                    resources.Add(new(profile.Summary.Name, source.Location, resource.Kind, resource.Metadata.Name,
                        BootstrapResourceDisposition.Invalid, $"Resource scope '{handler.Scope}' does not match profile scope '{profile.Summary.Scope}'."));
                    continue;
                }
                try
                {
                    var plan = await handler.PlanAsync(resource, operation, planning, cancellationToken);
                    resources.Add(new(profile.Summary.Name, source.Location, resource.Kind, resource.Metadata.Name,
                        plan.Disposition, Details: plan.Details));
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    resources.Add(new(profile.Summary.Name, source.Location, resource.Kind, resource.Metadata.Name,
                        BootstrapResourceDisposition.Invalid, exception.Message));
                }
            }
        }

        return new(
            profiles.Select(profile => profile.Summary).ToArray(),
            scope,
            selection.Target,
            bindings,
            ComputeDigest(profiles, scope, selection.Target, bindings, loadedSelection.SourceProvenance, fingerprints),
            resources,
            loadedSelection.SourceProvenance);
    }

    public Task<BootstrapExecutionResult> ExecuteAsync(BootstrapProfileSelection selection, CancellationToken cancellationToken) =>
        ExecutePhaseAsync(selection, BootstrapStartupPhase.All, cancellationToken);

    internal async Task<BootstrapExecutionResult> ExecutePhaseAsync(
        BootstrapProfileSelection selection, BootstrapStartupPhase phase, CancellationToken cancellationToken,
        IReadOnlyList<string>? expectedProfileDigests = null)
    {
        var preview = await PreviewPhaseAsync(selection, phase, cancellationToken);
        if (expectedProfileDigests is not null && !preview.Profiles.Select(profile => profile.Digest)
            .SequenceEqual(expectedProfileDigests, StringComparer.Ordinal))
            return new(preview, [], "The Bootstrap catalog changed after structural preparation.");
        var invalid = preview.Resources.FirstOrDefault(resource => resource.Disposition == BootstrapResourceDisposition.Invalid);
        if (invalid is not null)
            return new(preview, [], $"Bootstrap resource '{invalid.Kind}/{invalid.Name}' from '{invalid.Location}' is invalid: {invalid.Message}");

        await using var loadedSelection = await LoadAsync(selection, cancellationToken, phase);
        var loaded = loadedSelection.Profiles;
        var fingerprints = new List<string>();
        var bindings = await ResolveBindingsAsync(loaded, selection, fingerprints, cancellationToken);
        var prepared = new Dictionary<string, BootstrapResourceDocument>(StringComparer.Ordinal);
        foreach (var profile in loaded)
        {
            var operation = new BootstrapResourceOperationContext(profile.Summary.Name, profile.DirectoryPath, profile.Summary.Scope, selection.Target);
            foreach (var source in profile.Resources)
            {
                var resource = ResolveResource(source, profile, bindings);
                prepared.Add(source.Location, await extensionPreparation.ResolveDocumentAsync(resource,
                    handlers[resource.Kind].ExtensionReferencePaths,
                    BootstrapExtensionReferencePreparation.ConsumerScope(operation), fingerprints, cancellationToken));
            }
        }
        var executionDigest = ComputeDigest(loaded, preview.Scope, selection.Target, bindings, loadedSelection.SourceProvenance, fingerprints);
        if (!string.Equals(executionDigest, preview.Digest, StringComparison.Ordinal))
            return new(preview, [], "The bootstrap catalog changed while the application was being prepared. Preview the application again.");
        var resources = new List<BootstrapAppliedResource>();
        foreach (var profile in loaded)
        {
            var operation = new BootstrapResourceOperationContext(profile.Summary.Name, profile.DirectoryPath, profile.Summary.Scope, selection.Target, WaitForDependencies: phase == BootstrapStartupPhase.Resources);
            foreach (var source in profile.Resources)
            {
                var resource = source.Resource;
                try
                {
                    resource = prepared[source.Location];
                    var result = await handlers[resource.Kind].ApplyAsync(resource, operation, cancellationToken);
                    var disposition = result switch
                    {
                        BootstrapResourceApplyResult.Created => BootstrapResourceDisposition.Create,
                        BootstrapResourceApplyResult.Skipped => BootstrapResourceDisposition.Skip,
                        BootstrapResourceApplyResult.Conflict => BootstrapResourceDisposition.Conflict,
                        _ => throw new InvalidOperationException($"Unsupported bootstrap result '{result}'.")
                    };
                    resources.Add(new(profile.Summary.Name, source.Location, resource.Kind, resource.Metadata.Name, disposition));
                    LogResult(result, resource, source.Location);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    var message = $"Bootstrap resource '{resource.Kind}/{resource.Metadata.Name}' from '{source.Location}' failed: {exception.Message}";
                    resources.Add(new(profile.Summary.Name, source.Location, resource.Kind, resource.Metadata.Name,
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
        BootstrapApplicationTarget? target,
        IReadOnlyList<BootstrapBindingSelection> bindings,
        BootstrapSourceProvenance? source,
        IReadOnlyList<string>? fingerprints = null)
    {
        var lines = new List<string>
        {
            scope.ToString(),
            target?.TenantId?.ToString("D") ?? string.Empty,
            target?.WorkspaceId?.ToString("D") ?? string.Empty
        };
        if (fingerprints is not null) lines.AddRange(fingerprints.Select(value => $"resolved:{value}"));
        lines.AddRange(profiles.Select(profile => $"{profile.Summary.Name}:{profile.Summary.Digest}"));
        lines.AddRange(bindings.Select(binding =>
            $"{binding.Profile}:{binding.Name}:{binding.Target.Name}:{binding.Target.Namespace?.Value ?? string.Empty}:{binding.Target.ScopeRef?.Value ?? string.Empty}"));
        if (source is not null)
        {
            lines.AddRange([
                source.ScopeRef.ToString(), source.SourceUid.ToString("D"), source.Publisher, source.SourceName,
                source.SourceVersionUid.ToString("D"), source.SourceVersion, source.SourceVersionDigest,
                source.Channel, source.ProviderRevision, source.SnapshotUid.ToString("D"), source.SnapshotDigest,
                source.CatalogKind, source.CatalogName, source.CatalogPath, source.EntryName, source.Locale, source.Path
            ]);
            if (source.Registry is { } registry)
            {
                lines.AddRange([
                    registry.Selection.RegistrationUid.ToString("D"), registry.Selection.ObservationId.ToString("D"),
                    registry.Selection.CatalogName, registry.IndexDigest, registry.CatalogDigest,
                    registry.ExpectedManifestDigest, registry.FinalManifestUrl.AbsoluteUri
                ]);
            }
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', lines)))).ToLowerInvariant();
    }

    private async Task<LoadedBootstrapSelection> LoadAsync(
        BootstrapProfileSelection selection, CancellationToken cancellationToken,
        BootstrapStartupPhase phase = BootstrapStartupPhase.All)
    {
        if (phase != BootstrapStartupPhase.All && selection.Source is not null)
            throw new DeclarativeBootstrapException("Phased initial Bootstrap uses local catalog profiles.");
        if (selection.Source is { } source)
            return await (sourceProfiles ?? throw new DeclarativeBootstrapException("Source Bootstrap profiles are not configured."))
                .LoadAsync(source, cancellationToken);
        var loaded = await catalog.LoadAsync(selection.Profiles, cancellationToken);
        return new(phase == BootstrapStartupPhase.All ? loaded : loaded.Select(profile => profile with
        {
            Resources = profile.Resources.Where(item => Includes(phase, item.Resource.Kind)).ToArray()
        }).ToArray());
    }

    private async Task<IReadOnlyList<BootstrapBindingSelection>> ResolveBindingsAsync(
        IReadOnlyList<LoadedBootstrapProfile> profiles,
        BootstrapProfileSelection application,
        List<string> fingerprints,
        CancellationToken cancellationToken)
    {
        var selections = application.Bindings ?? [];
        var selected = new Dictionary<(string Profile, string Name), BootstrapBindingSelection>();
        foreach (var selection in selections)
        {
            if (!selected.TryAdd((selection.Profile, selection.Name), selection))
                throw new DeclarativeBootstrapException($"Bootstrap binding '{selection.Profile}/{selection.Name}' was selected more than once.");
        }

        var resolved = new List<BootstrapBindingSelection>();
        foreach (var profile in profiles)
        {
            var usedBindings = BootstrapExtensionReferencePreparation.UsedBindings(profile);
            foreach (var binding in profile.Summary.Bindings)
            {
                selected.Remove((profile.Summary.Name, binding.Name), out var explicitSelection);
                if (explicitSelection is not null && binding.ExtensionTarget is not null)
                    throw new DeclarativeBootstrapException($"Bootstrap binding '{profile.Summary.Name}/{binding.Name}' selects a public contribution; change extensionTarget rather than supplying a local-name override.");
                JsonElement? targetValue = null;
                var target = explicitSelection?.Target ?? binding.DefaultTarget;
                if (target is null && binding.ExtensionTarget is { } extensionTarget)
                {
                    if (!usedBindings.Contains(binding.Name)) continue;
                    try
                    {
                        var operation = new BootstrapResourceOperationContext(profile.Summary.Name, profile.DirectoryPath,
                            profile.Summary.Scope, application.Target);
                        var resolvedTarget = await extensionPreparation.ResolveAsync(extensionTarget,
                            BootstrapExtensionReferencePreparation.ConsumerScope(operation), cancellationToken);
                        target = resolvedTarget.Reference;
                        targetValue = resolvedTarget.TargetValue;
                        fingerprints.Add(resolvedTarget.Fingerprint);
                    }
                    catch (BootstrapDependencyException exception)
                    {
                        var consumers = profile.Resources.Where(source => BootstrapExtensionReferencePreparation
                            .UsedBindings(profile with { Resources = [source] }).Contains(binding.Name))
                            .Select(source => $"{source.Location}: {source.Resource.Kind}/{source.Resource.Metadata.Name}");
                        throw new DeclarativeBootstrapException(
                            $"Bootstrap binding '{profile.Summary.Name}/{binding.Name}' used by {string.Join(", ", consumers)} requires extension '{extensionTarget.ExtensionId}' installation '{extensionTarget.InstanceId}', {extensionTarget.Kind}/{extensionTarget.ContributionId} version '{extensionTarget.Version}' [{exception.Code}]: {exception.Message}", exception);
                    }
                }
                if (target is null)
                {
                    if (binding.Required)
                        throw new DeclarativeBootstrapException($"Bootstrap binding '{profile.Summary.Name}/{binding.Name}' requires a target.");
                    continue;
                }
                if (string.IsNullOrWhiteSpace(target.Name))
                    throw new DeclarativeBootstrapException($"Bootstrap binding '{profile.Summary.Name}/{binding.Name}' requires a target name.");
                resolved.Add(new(
                    profile.Summary.Name,
                    binding.Name,
                    new ResourceReference(target.Name, target.ScopeRef, target.Namespace ?? ResourceNamespace.Default), targetValue));
            }
        }
        if (selected.Count > 0)
        {
            var unknown = selected.Keys.OrderBy(value => value.Profile, StringComparer.Ordinal)
                .ThenBy(value => value.Name, StringComparer.Ordinal).First();
            throw new DeclarativeBootstrapException($"Bootstrap binding '{unknown.Profile}/{unknown.Name}' is not declared by the selected profiles.");
        }
        return resolved;
    }

    internal async Task<IReadOnlyList<BootstrapExtensionRequirement>> GetExtensionRequirementsAsync(
        BootstrapProfileSelection selection, CancellationToken cancellationToken)
    {
        await using var loaded = await LoadAsync(selection, cancellationToken);
        var requirements = new List<BootstrapExtensionRequirement>();
        foreach (var profile in loaded.Profiles)
        {
            ValidateTarget(profile.Summary.Scope, selection.Target);
            var operation = new BootstrapResourceOperationContext(profile.Summary.Name, profile.DirectoryPath,
                profile.Summary.Scope, selection.Target);
            requirements.AddRange(BootstrapExtensionReferencePreparation.Requirements(profile, operation, handlers, selection.Bindings ?? []));
        }
        return requirements;
    }

    private static BootstrapResourceDocument ResolveResource(
        BootstrapResourceSource source,
        LoadedBootstrapProfile profile,
        IReadOnlyList<BootstrapBindingSelection> bindings)
    {
        var definition = JsonNode.Parse(source.Resource.Definition.GetRawText())
            ?? throw new DeclarativeBootstrapException($"Bootstrap resource '{source.Location}' requires a definition.");
        var resolved = ResolveNode(definition, profile, bindings, source.Location);
        return source.Resource with { Definition = JsonSerializer.SerializeToElement(resolved, JsonOptions) };
    }

    private static JsonNode? ResolveNode(
        JsonNode? node,
        LoadedBootstrapProfile profile,
        IReadOnlyList<BootstrapBindingSelection> bindings,
        string location)
    {
        if (node is JsonObject value)
        {
            if (value.Count == 1 && value.TryGetPropertyValue("binding", out var bindingNode))
            {
                if (bindingNode is not JsonValue bindingValue || !bindingValue.TryGetValue<string>(out var bindingName))
                    throw new DeclarativeBootstrapException($"Bootstrap binding reference in '{location}' must contain only a string 'binding' property.");
                if (!profile.Summary.Bindings.Any(binding => string.Equals(binding.Name, bindingName, StringComparison.Ordinal)))
                    throw new DeclarativeBootstrapException($"Bootstrap resource '{location}' references undeclared binding '{bindingName}'.");
                var selection = bindings.FirstOrDefault(binding =>
                    string.Equals(binding.Profile, profile.Summary.Name, StringComparison.Ordinal)
                    && string.Equals(binding.Name, bindingName, StringComparison.Ordinal));
                if (selection is null)
                    throw new DeclarativeBootstrapException($"Bootstrap resource '{location}' references unresolved optional binding '{bindingName}'.");
                var declaration = profile.Summary.Bindings.Single(binding => string.Equals(binding.Name, bindingName, StringComparison.Ordinal));
                if (declaration.TargetKind is BootstrapBindingTargetKind.Parameter or BootstrapBindingTargetKind.Secret)
                {
                    if (selection.Target.ScopeRef is not { } scopeRef || scopeRef == default)
                        throw new DeclarativeBootstrapException($"Bootstrap binding '{profile.Summary.Name}/{bindingName}' requires an exact scoped target.");
                    var kind = declaration.TargetKind == BootstrapBindingTargetKind.Parameter
                        ? "Parameter"
                        : "Secret";
                    return new JsonObject
                    {
                        ["address"] = new JsonObject
                        {
                            ["namespace"] = (selection.Target.Namespace ?? ResourceNamespace.Default).Value,
                            ["kind"] = kind,
                            ["name"] = selection.Target.Name
                        },
                        ["scopeRef"] = scopeRef.Value
                    };
                }
                return selection.TargetValue is { } valueTarget
                    ? JsonNode.Parse(valueTarget.GetRawText())
                    : JsonSerializer.SerializeToNode(selection.Target, JsonOptions);
            }
            foreach (var property in value.ToArray())
            {
                var resolved = ResolveNode(property.Value, profile, bindings, location);
                if (!ReferenceEquals(resolved, property.Value)) value[property.Key] = resolved;
            }
            return value;
        }
        if (node is JsonArray array)
        {
            for (var index = 0; index < array.Count; index++)
            {
                var current = array[index];
                var resolved = ResolveNode(current, profile, bindings, location);
                if (!ReferenceEquals(resolved, current)) array[index] = resolved;
            }
        }
        return node;
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
    public bool PhasedStartupEnabled { get; set; }
    public TimeSpan ExtensionDependencyTimeout { get; set; } = TimeSpan.FromMinutes(10);
    public TimeSpan ModelDependencyTimeout { get; set; } = TimeSpan.FromMinutes(2);
    public List<BootstrapInitialApplication> InitialApplications { get; set; } = [];
}

public static class DeclarativeBootstrapServiceProviderExtensions
{
    public static async Task ApplyDeclarativeBootstrapAsync(this IServiceProvider services, CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<DeclarativeBootstrapService>().ApplyAsync(cancellationToken);
    }
}
