using Agentstration.Management.Abstractions;
using Agentstration.ModelProviders;
using Agentstration.Resources;

namespace Agentstration.Management.Core;

public sealed record SourceBindingIssue(string Code, string Message, string? Channel = null);

public sealed record SourceBindingStatus(
    string Name,
    string TargetKind,
    ResourceReference? Target,
    string Status,
    IReadOnlyList<string> Channels,
    IReadOnlyList<SourceBindingIssue> Issues);

public sealed record SourceBindingStatusView(
    Guid SourceVersionUid,
    string SourceVersion,
    string ConfigurationETag,
    bool Ready,
    IReadOnlyList<SourceBindingStatus> Bindings,
    IReadOnlyList<SourceBindingSelection> StaleSelections);

public sealed record SourceBindingConfigurationResult(
    SourceConfigurationResource Configuration,
    SourceBindingStatusView Status);

public sealed class SourceBindingManagementService(
    SourceManagementService sources,
    IControlPlaneStore store,
    IResourceReferenceResolver references,
    IEnumerable<IExtensionInspector> inspectors,
    ResourceScopeOperationService scopeOperations)
{
    private const string Available = "available";
    private const string SourceProviderContribution = "source-provider";

    public async Task<SourceBindingStatusView> GetStatusAsync(
        string publisher,
        string name,
        Guid versionUid,
        CancellationToken cancellationToken)
    {
        var source = (await sources.GetAsync(publisher, name, cancellationToken))?.Source
            ?? throw NotFound(ResourceKinds.Source, name, publisher);
        var version = await sources.GetVersionAsync(publisher, name, versionUid, cancellationToken)
            ?? throw new ControlPlaneResourceNotFoundException(new(ResourceKinds.SourceVersion, versionUid.ToString("D")));
        return await GetStatusAsync(source, version, cancellationToken);
    }

    public async Task<SourceBindingStatusView> GetStatusExactAsync(
        ResourceScopeRef scopeRef,
        string publisher,
        string name,
        Guid versionUid,
        CancellationToken cancellationToken)
    {
        var source = (await sources.GetExactAsync(scopeRef, publisher, name, cancellationToken))?.Source
            ?? throw NotFound(ResourceKinds.Source, name, publisher);
        var version = await sources.GetVersionExactAsync(scopeRef, publisher, name, versionUid, cancellationToken)
            ?? throw new ControlPlaneResourceNotFoundException(new(ResourceKinds.SourceVersion, versionUid.ToString("D")));
        return await GetStatusAsync(source, version, cancellationToken);
    }

    public async Task<SourceBindingConfigurationResult> ConfigureAsync(
        string publisher,
        string name,
        Guid versionUid,
        IReadOnlyList<SourceBindingSelection> selections,
        string ifMatch,
        CancellationToken cancellationToken)
    {
        var source = (await sources.GetAsync(publisher, name, cancellationToken))?.Source
            ?? throw NotFound(ResourceKinds.Source, name, publisher);
        var version = await sources.GetVersionAsync(publisher, name, versionUid, cancellationToken)
            ?? throw new ControlPlaneResourceNotFoundException(new(ResourceKinds.SourceVersion, versionUid.ToString("D")));
        return await ConfigureAsync(source, version, selections, ifMatch, cancellationToken);
    }

    public async Task<SourceBindingConfigurationResult> ConfigureExactAsync(
        ResourceScopeRef scopeRef,
        string publisher,
        string name,
        Guid versionUid,
        IReadOnlyList<SourceBindingSelection> selections,
        string ifMatch,
        CancellationToken cancellationToken)
    {
        var source = (await sources.GetExactAsync(scopeRef, publisher, name, cancellationToken))?.Source
            ?? throw NotFound(ResourceKinds.Source, name, publisher);
        var version = await sources.GetVersionExactAsync(scopeRef, publisher, name, versionUid, cancellationToken)
            ?? throw new ControlPlaneResourceNotFoundException(new(ResourceKinds.SourceVersion, versionUid.ToString("D")));
        return await ConfigureAsync(source, version, selections, ifMatch, cancellationToken);
    }

    private async Task<SourceBindingConfigurationResult> ConfigureAsync(
        SourceResource source,
        SourceVersionResource version,
        IReadOnlyList<SourceBindingSelection> selections,
        string ifMatch,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selections);
        var duplicate = selections.GroupBy(value => value.Name, StringComparer.Ordinal).FirstOrDefault(value => value.Count() > 1);
        if (duplicate is not null)
            throw Invalid("source_binding_selection_duplicate", $"Source binding '{duplicate.Key}' was selected more than once.");

        var declarations = version.Definition.PublishedDefinition.Bindings.ToDictionary(value => value.Name, StringComparer.Ordinal);
        var normalized = new List<SourceBindingSelection>(selections.Count);
        foreach (var selection in selections)
        {
            if (!declarations.TryGetValue(selection.Name, out var declaration))
                throw Invalid("source_binding_selection_unknown", $"Source binding '{selection.Name}' is not declared by Source Version '{version.Definition.Version}'.");
            if (!string.Equals(selection.TargetKind, declaration.TargetKind, StringComparison.Ordinal))
                throw Invalid("source_binding_selection_kind_invalid", $"Source binding '{selection.Name}' must target '{declaration.TargetKind}'.");
            if (selection.Target is null || string.IsNullOrWhiteSpace(selection.Target.Name))
                throw Invalid("source_binding_selection_target_invalid", $"Source binding '{selection.Name}' requires a Source Provider target.");

            var target = new ResourceReference(
                selection.Target.Name.Trim(),
                ResourceScopeRef.Instance,
                selection.Target.Namespace ?? ResourceNamespace.Default);
            var resolved = await references.ResolveAsync<SourceProviderResource>(
                target,
                source.Namespace,
                ResourceKinds.SourceProvider,
                RequireScope(source),
                cancellationToken);
            if (resolved is null)
                throw Invalid("source_binding_provider_missing", $"Source Provider '{target.Namespace}/{target.Name}' selected for binding '{selection.Name}' was not found.");
            normalized.Add(selection with { TargetKind = declaration.TargetKind, Target = target });
        }

        var scopeRef = RequireScope(source);
        var updated = await scopeOperations.WriteAsync(
            ResourceKinds.SourceConfiguration,
            scopeRef,
            AuthorizationPermissions.ResourcesWrite,
            async token =>
            {
                var address = ScopedResourceAddress.Create(scopeRef, source.Namespace, ResourceKinds.SourceConfiguration, source.Name);
                var current = await store.GetExactAsync<SourceConfigurationResource>(address, token)
                    ?? throw NotFound(ResourceKinds.SourceConfiguration, source.Name, source.Namespace.Value);
                var merged = current.Value.Definition.Bindings.ToDictionary(value => value.Name, StringComparer.Ordinal);
                foreach (var declaration in declarations.Values)
                {
                    if (merged.TryGetValue(declaration.Name, out var existing)
                        && string.Equals(existing.TargetKind, declaration.TargetKind, StringComparison.Ordinal))
                        merged.Remove(declaration.Name);
                }
                foreach (var selection in normalized) merged[selection.Name] = selection;
                return await store.PutExactAsync(scopeRef, current.Value with
                {
                    Generation = checked(current.Value.Generation + 1),
                    Definition = current.Value.Definition with
                    {
                        Bindings = merged.Values.OrderBy(value => value.Name, StringComparer.Ordinal).ToArray()
                    }
                }, ifMatch, false, token);
            },
            cancellationToken);
        return new(updated.Value, await GetStatusAsync(source, version, cancellationToken));
    }

    private async Task<SourceBindingStatusView> GetStatusAsync(
        SourceResource source,
        SourceVersionResource version,
        CancellationToken cancellationToken)
    {
        var scopeRef = RequireScope(source);
        var configuration = await store.GetExactAsync<SourceConfigurationResource>(
            ScopedResourceAddress.Create(scopeRef, source.Namespace, ResourceKinds.SourceConfiguration, source.Name),
            cancellationToken)
            ?? throw NotFound(ResourceKinds.SourceConfiguration, source.Name, source.Namespace.Value);
        var declarations = version.Definition.PublishedDefinition.Bindings;
        var statuses = new List<SourceBindingStatus>(declarations.Count);
        foreach (var declaration in declarations)
        {
            var channels = version.Definition.PublishedDefinition.Channels
                .Where(value => string.Equals(value.Provider.Binding, declaration.Name, StringComparison.Ordinal))
                .Select(value => value.Name)
                .ToArray();
            var selection = configuration.Value.Definition.Bindings.SingleOrDefault(value =>
                string.Equals(value.Name, declaration.Name, StringComparison.Ordinal)
                && string.Equals(value.TargetKind, declaration.TargetKind, StringComparison.Ordinal));
            statuses.Add(await InspectAsync(source, version, declaration, selection, channels, cancellationToken));
        }
        var stale = configuration.Value.Definition.Bindings.Where(selection => !declarations.Any(declaration =>
                string.Equals(declaration.Name, selection.Name, StringComparison.Ordinal)
                && string.Equals(declaration.TargetKind, selection.TargetKind, StringComparison.Ordinal)))
            .OrderBy(value => value.Name, StringComparer.Ordinal)
            .ToArray();
        return new(
            version.Uid,
            version.Definition.Version,
            configuration.ETag,
            statuses.All(value => string.Equals(value.Status, "ready", StringComparison.Ordinal)),
            statuses,
            stale);
    }

    private async Task<SourceBindingStatus> InspectAsync(
        SourceResource source,
        SourceVersionResource version,
        SourceBindingDefinition declaration,
        SourceBindingSelection? selection,
        IReadOnlyList<string> channels,
        CancellationToken cancellationToken)
    {
        if (selection is null)
            return Status(declaration, null, "unresolved", channels,
                new SourceBindingIssue("source_binding_unresolved", $"Select a local Source Provider for binding '{declaration.Name}'."));

        var provider = await references.ResolveAsync<SourceProviderResource>(
            selection.Target,
            source.Namespace,
            ResourceKinds.SourceProvider,
            RequireScope(source),
            cancellationToken);
        if (provider is null)
            return Status(declaration, selection.Target, "missing", channels,
                new SourceBindingIssue("source_binding_provider_missing", $"Selected Source Provider '{selection.Target.Namespace}/{selection.Target.Name}' was not found."));

        var extension = await references.ResolveAsync<ExtensionRegistrationResource>(
            provider.Value.Definition.Extension,
            provider.Value.Namespace,
            ResourceKinds.ExtensionRegistration,
            RequireScope(provider.Value),
            cancellationToken);
        if (extension is null)
            return Status(declaration, selection.Target, "unavailable", channels,
                new SourceBindingIssue("source_binding_extension_missing", $"Extension registration for Source Provider '{provider.Value.Name}' was not found."));
        if (!extension.Value.Definition.Enabled)
            return Status(declaration, selection.Target, "unavailable", channels,
                new SourceBindingIssue("source_binding_extension_disabled", $"Extension registration '{extension.Value.Name}' is disabled."));

        var inspector = inspectors.SingleOrDefault(value => value.CanInspectEndpoint(extension.Value.Definition.Endpoint));
        if (inspector is null)
            return Status(declaration, selection.Target, "unavailable", channels,
                new SourceBindingIssue("source_binding_inspector_unavailable", $"No inspector can validate extension '{extension.Value.Name}'."));

        ExtensionInspection inspection;
        try
        {
            inspection = await inspector.InspectAsync(extension.Value.Name, extension.Value.Definition.Endpoint, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Status(declaration, selection.Target, "unavailable", channels,
                new SourceBindingIssue("source_binding_extension_unavailable", $"Extension '{extension.Value.Name}' could not be inspected: {exception.Message}"));
        }
        if (!string.Equals(inspection.Status, Available, StringComparison.Ordinal))
            return Status(declaration, selection.Target, "unavailable", channels,
                new SourceBindingIssue("source_binding_extension_unavailable", inspection.Details ?? $"Extension '{extension.Value.Name}' is {inspection.Status}."));
        if (extension.Value.Definition.ExpectedExtensionId is { Length: > 0 } expectedId
            && !string.Equals(inspection.Extension?.Id, expectedId, StringComparison.Ordinal))
            return Status(declaration, selection.Target, "incompatible", channels,
                new SourceBindingIssue("source_binding_extension_identity_mismatch", $"Extension '{extension.Value.Name}' does not report expected identity '{expectedId}'."));
        if (!inspection.Contributions.Any(value =>
                string.Equals(value.Kind, SourceProviderContribution, StringComparison.Ordinal)
                && string.Equals(value.Id, provider.Value.Definition.ContributionId, StringComparison.OrdinalIgnoreCase)))
            return Status(declaration, selection.Target, "incompatible", channels,
                new SourceBindingIssue("source_binding_contribution_missing", $"Extension '{extension.Value.Name}' does not contribute Source Provider '{provider.Value.Definition.ContributionId}'."));

        var issues = new List<SourceBindingIssue>();
        foreach (var channel in version.Definition.PublishedDefinition.Channels.Where(value =>
                     string.Equals(value.Provider.Binding, declaration.Name, StringComparison.Ordinal)))
        {
            var optionSet = inspection.OptionSets.SingleOrDefault(value =>
                string.Equals(value.Id, channel.Configuration.OptionSet, StringComparison.Ordinal)
                && string.Equals(value.ContributionKind, SourceProviderContribution, StringComparison.Ordinal)
                && string.Equals(value.ContributionId, provider.Value.Definition.ContributionId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(value.Scope, ExtensionOptionScopes.SourceChannel, StringComparison.Ordinal));
            var optionVersion = optionSet?.Versions.SingleOrDefault(value =>
                string.Equals(value.Version, channel.Configuration.Version, StringComparison.Ordinal));
            if (optionSet is null)
                issues.Add(new("source_channel_option_set_unsupported", $"Option set '{channel.Configuration.OptionSet}' is not supported by Source Provider '{provider.Value.Name}'.", channel.Name));
            else if (optionVersion is null)
                issues.Add(new("source_channel_option_version_unsupported", $"Option set '{channel.Configuration.OptionSet}' version '{channel.Configuration.Version}' is not supported.", channel.Name));
            else if (!string.Equals(optionVersion.SchemaDigest, channel.Configuration.SchemaDigest, StringComparison.Ordinal))
                issues.Add(new("source_channel_schema_digest_mismatch", $"Channel schema digest does not match option set '{channel.Configuration.OptionSet}' version '{channel.Configuration.Version}'.", channel.Name));
            else
                issues.AddRange(ExtensionOptionSchemaValidator.Validate(channel.Configuration.Values, optionVersion.Schema)
                    .Select(value => new SourceBindingIssue("source_channel_options_invalid", $"{value.Path}: {value.Message}", channel.Name)));
        }
        return new(
            declaration.Name,
            declaration.TargetKind,
            selection.Target,
            issues.Count == 0 ? "ready" : "incompatible",
            channels,
            issues);
    }

    private static SourceBindingStatus Status(
        SourceBindingDefinition declaration,
        ResourceReference? target,
        string status,
        IReadOnlyList<string> channels,
        params SourceBindingIssue[] issues) =>
        new(declaration.Name, declaration.TargetKind, target, status, channels, issues);

    private static ResourceScopeRef RequireScope(Resource resource) =>
        resource.ScopeRef ?? throw new InvalidOperationException($"Resource '{resource.Address}' has no ownership scope.");

    private static SourceValidationException Invalid(string code, string message) => new(code, message);

    private static ControlPlaneResourceNotFoundException NotFound(string kind, string name, string @namespace) =>
        new(new(kind, name, new ResourceNamespace(@namespace)));
}
