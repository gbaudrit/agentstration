using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Agentstration.Management.Abstractions;
using Agentstration.Resources;

namespace Agentstration.Management.Core;

public sealed partial class PackCompositionService(
    IControlPlaneStore store,
    IPackArtifactStore artifacts,
    IPackArchiveReader archiveReader,
    IPackWorkspaceResourceCatalog catalog,
    TimeProvider timeProvider)
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public Task<IReadOnlyList<PackCompositionCatalogItem>> ListResourcesAsync(CancellationToken cancellationToken) =>
        catalog.ListAsync(cancellationToken);

    public async Task<PackCompositionPreview> PreviewAsync(
        PreviewPackCompositionCommand command,
        CancellationToken cancellationToken)
    {
        var issues = new List<PackCompositionIssue>();
        var included = new Dictionary<ResourceAddress, (PackCompositionResourceSnapshot Snapshot, bool Explicit)>();
        var bindingUsages = new Dictionary<ResourceAddress, BindingUsage>();
        var visiting = new HashSet<ResourceAddress>();
        var selected = command.Resources
            .DistinctBy(resource => resource.Address)
            .ToArray();
        var selectedAddresses = selected.Select(resource => resource.Address).ToHashSet();

        if (selected.Length == 0)
            issues.Add(new("pack_composition_empty", "Select at least one resource for the Pack.", PackCompositionIssueSeverity.Error));

        foreach (var resource in selected)
            await IncludeAsync(resource, true, selectedAddresses, included, bindingUsages, visiting, issues, cancellationToken);

        var bindingNames = CreateBindingNames(bindingUsages);
        var resources = included.Values
            .OrderBy(value => KindOrder(value.Snapshot.Resource.Resource.Kind))
            .ThenBy(value => value.Snapshot.Resource.Resource.Name, StringComparer.Ordinal)
            .Select(value => new PackCompositionPreviewResource(
                value.Snapshot.Resource.Resource,
                value.Snapshot.Resource.DisplayName,
                ResourcePath(value.Snapshot.Resource.Resource),
                value.Explicit,
                value.Snapshot.Dependencies))
            .ToArray();
        var bindings = bindingUsages
            .OrderBy(pair => bindingNames[pair.Key], StringComparer.Ordinal)
            .Select(pair => new PackCompositionPreviewBinding(
                bindingNames[pair.Key],
                pair.Value.TargetKind,
                pair.Value.DisplayName,
                pair.Value.Resource,
                pair.Value.UsedBy.OrderBy(value => value.Kind, StringComparer.Ordinal).ThenBy(value => value.Name, StringComparer.Ordinal).ToArray(),
                pair.Value.Required))
            .ToArray();
        return new(resources, bindings, issues);
    }

    public async Task<StoredResource<PackProjectResource>> CreateProjectAsync(
        CreatePackProjectFromWorkspaceCommand command,
        CancellationToken cancellationToken)
    {
        PackAuthoringService.ValidateCoordinate(command.Publisher, command.Name, command.Version);
        var preview = await PreviewAsync(new(command.Resources), cancellationToken);
        var blocking = preview.Issues.FirstOrDefault(issue => issue.Severity == PackCompositionIssueSeverity.Error);
        if (blocking is not null) throw new PackValidationException(blocking.Code, blocking.Message);

        var duplicate = (await store.ListAllAsync<PackProjectResource>(PackAuthoringKinds.PackProject, cancellationToken)).Any(value =>
            string.Equals(value.Value.Definition.Publisher, command.Publisher, StringComparison.Ordinal)
            && string.Equals(value.Value.Definition.PackName, command.Name, StringComparison.Ordinal));
        if (duplicate) throw new PackValidationException("pack_project_identity_conflict", $"Pack Project '{command.Publisher}/{command.Name}' already exists.");

        var bindingMap = preview.Bindings.ToDictionary(binding => binding.WorkspaceResource.Address, binding => binding.Name);
        var manifests = new List<(string Path, JsonElement Manifest)>();
        foreach (var resource in preview.Resources)
            manifests.Add((resource.Path, await catalog.ExportAsync(resource.Resource, bindingMap, cancellationToken)));

        var manifest = new PackManifest
        {
            ApiVersion = ManagementApiVersions.CoreV1,
            Kind = PackKinds.Pack,
            Metadata = new PackMetadata
            {
                Publisher = command.Publisher,
                Name = command.Name,
                Version = command.Version,
                DisplayName = EmptyToNull(command.DisplayName) ?? command.Name,
                Description = EmptyToNull(command.Description),
                Audience = command.Audience,
                Purpose = command.Purpose,
                Categories = Clean(command.Categories),
                Tags = Clean(command.Tags)
            },
            Definition = new PackDefinition
            {
                Resources = manifests.Select(value => value.Path).ToArray(),
                Bindings = preview.Bindings.Select(binding => new PackBindingRequirement
                {
                    Name = binding.Name,
                    TargetKind = binding.TargetKind,
                    DisplayName = binding.DisplayName,
                    Description = $"Workspace {BindingLabel(binding.TargetKind)} used by {string.Join(", ", binding.UsedBy.Select(value => $"{value.Kind}/{value.Name}"))}.",
                    Required = binding.Required
                }).ToArray()
            }
        };
        var archive = BuildArchive(manifest, manifests);
        var fileName = $"{command.Publisher}-{command.Name}-{command.Version}.pack.zip";
        await using (var validationStream = new MemoryStream(archive, writable: false))
            _ = await archiveReader.ReadAsync(validationStream, fileName, cancellationToken);
        var sourceArtifact = await artifacts.SaveAsync(archive, fileName, cancellationToken);
        var now = timeProvider.GetUtcNow();
        var id = Guid.NewGuid();
        return await store.PutAsync(new PackProjectResource
        {
            Uid = id,
            ApiVersion = ManagementApiVersions.CoreV1,
            Kind = PackAuthoringKinds.PackProject,
            Metadata = new ResourceMetadata { Name = id.ToString("N") },
            Generation = 1,
            Status = new ResourceStatus { ProvisioningState = ProvisioningState.Succeeded },
            Definition = new PackProjectProperties
            {
                Publisher = command.Publisher,
                PackName = command.Name,
                Version = command.Version,
                Audience = command.Audience,
                Purpose = command.Purpose,
                DisplayName = manifest.Metadata.DisplayName,
                Description = manifest.Metadata.Description,
                Categories = manifest.Metadata.Categories,
                Tags = manifest.Metadata.Tags,
                SourceKind = PackProjectSourceKind.WorkspaceSnapshot,
                SourceResources = preview.Resources.Select(resource => new PackProjectSourceResource(
                    resource.Resource.Kind,
                    resource.Resource.Name,
                    resource.Resource.Namespace,
                    resource.Path,
                    resource.ExplicitlySelected)).ToArray(),
                SourceArtifact = sourceArtifact,
                CreatedAt = now,
                UpdatedAt = now
            }
        }, null, true, cancellationToken);
    }

    private async Task IncludeAsync(
        PackCompositionResourceKey resource,
        bool explicitlySelected,
        IReadOnlySet<ResourceAddress> explicitlySelectedResources,
        IDictionary<ResourceAddress, (PackCompositionResourceSnapshot Snapshot, bool Explicit)> included,
        IDictionary<ResourceAddress, BindingUsage> bindings,
        ISet<ResourceAddress> visiting,
        ICollection<PackCompositionIssue> issues,
        CancellationToken cancellationToken)
    {
        if (!resource.NamespaceValue.IsDefault)
        {
            issues.Add(new("pack_composition_namespace_unsupported", "The first Pack Composer increment accepts resources from the default workspace namespace only.", PackCompositionIssueSeverity.Error, resource));
            return;
        }
        if (included.TryGetValue(resource.Address, out var existing))
        {
            if (explicitlySelected && !existing.Explicit) included[resource.Address] = (existing.Snapshot, true);
            return;
        }
        if (!visiting.Add(resource.Address)) return;
        var snapshot = await catalog.GetAsync(resource, cancellationToken);
        if (snapshot is null)
        {
            issues.Add(new("pack_composition_resource_missing", $"Resource '{resource.Kind}/{resource.Name}' was not found.", PackCompositionIssueSeverity.Error, resource));
            visiting.Remove(resource.Address);
            return;
        }
        if (snapshot.Resource.Availability != PackCompositionAvailability.Selectable)
        {
            issues.Add(new("pack_composition_resource_not_selectable", snapshot.Resource.AvailabilityReason ?? $"Resource '{resource.Kind}/{resource.Name}' cannot be included in a Pack.", PackCompositionIssueSeverity.Error, resource));
            visiting.Remove(resource.Address);
            return;
        }

        included[resource.Address] = (snapshot, explicitlySelected);
        foreach (var dependency in snapshot.Dependencies)
        {
            if (dependency.Mode == PackCompositionDependencyMode.Include)
            {
                await IncludeAsync(dependency.Target, false, explicitlySelectedResources, included, bindings, visiting, issues, cancellationToken);
                continue;
            }
            if (dependency.Mode == PackCompositionDependencyMode.Binding && dependency.BindingTargetKind is { } targetKind)
            {
                if (explicitlySelectedResources.Contains(dependency.Target.Address))
                {
                    await IncludeAsync(dependency.Target, false, explicitlySelectedResources, included, bindings, visiting, issues, cancellationToken);
                    continue;
                }
                var target = await catalog.GetAsync(dependency.Target, cancellationToken);
                if (target is null)
                {
                    issues.Add(new("pack_composition_binding_target_missing", $"Required {BindingLabel(targetKind)} '{dependency.Target.Name}' was not found.", PackCompositionIssueSeverity.Error, resource));
                    continue;
                }
                if (!bindings.TryGetValue(dependency.Target.Address, out var usage))
                {
                    usage = new(dependency.Target, targetKind, target.Resource.DisplayName, dependency.Required);
                    bindings[dependency.Target.Address] = usage;
                }
                usage.UsedBy.Add(resource);
                usage.Required |= dependency.Required;
                continue;
            }
            issues.Add(new("pack_composition_dependency_unsupported", $"Dependency '{dependency.Target.Kind}/{dependency.Target.Name}' used by '{resource.Kind}/{resource.Name}' cannot yet be packaged or bound.", PackCompositionIssueSeverity.Error, resource));
        }
        visiting.Remove(resource.Address);
    }

    private static IReadOnlyDictionary<ResourceAddress, string> CreateBindingNames(IReadOnlyDictionary<ResourceAddress, BindingUsage> bindings)
    {
        var result = new Dictionary<ResourceAddress, string>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pair in bindings.OrderBy(value => value.Key.Kind, StringComparer.Ordinal).ThenBy(value => value.Key.Name, StringComparer.Ordinal))
        {
            var prefix = pair.Value.TargetKind switch
            {
                PackBindingTargetKind.Secret => "secret",
                PackBindingTargetKind.ModelProvider => "provider",
                PackBindingTargetKind.ExtensionRegistration => "extension",
                _ => "model"
            };
            var baseName = Slug($"{prefix}-{pair.Value.Resource.Name}");
            var name = baseName;
            var suffix = 1;
            while (!used.Add(name)) name = $"{baseName}-{++suffix}";
            result[pair.Key] = name;
        }
        return result;
    }

    private static byte[] BuildArchive(PackManifest manifest, IReadOnlyList<(string Path, JsonElement Manifest)> resources)
    {
        using var outputStream = new MemoryStream();
        using (var archive = new ZipArchive(outputStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(archive, "pack.json", JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions));
            foreach (var resource in resources.OrderBy(value => value.Path, StringComparer.Ordinal))
                Write(archive, resource.Path, Encoding.UTF8.GetBytes(resource.Manifest.GetRawText()));
        }
        return outputStream.ToArray();
    }

    private static void Write(ZipArchive archive, string path, byte[] content)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.NoCompression);
        entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using var stream = entry.Open();
        stream.Write(content);
    }

    private static string ResourcePath(PackCompositionResourceKey resource)
    {
        var directory = resource.Kind switch
        {
            ResourceKinds.Agent => "agents",
            ResourceKinds.Flow => "flows",
            ResourceKinds.Entry => "entries",
            ResourceKinds.ModelProfile => "model-profiles",
            ResourceKinds.ModelProvider => "model-providers",
            ResourceKinds.RuntimeProfile => "runtime-profiles",
            _ => $"{resource.Kind.ToLowerInvariant()}s"
        };
        return $"{directory}/{resource.Name}.json";
    }
    private static int KindOrder(string kind) => kind switch { ResourceKinds.ModelProvider => 10, ResourceKinds.RuntimeProfile => 20, ResourceKinds.ModelProfile => 30, ResourceKinds.Agent => 40, ResourceKinds.Flow => 50, ResourceKinds.Entry => 60, _ => 100 };
    private static string BindingLabel(PackBindingTargetKind kind) => kind switch { PackBindingTargetKind.Secret => "Secret", PackBindingTargetKind.ModelProvider => "Model Provider", PackBindingTargetKind.ExtensionRegistration => "Extension registration", _ => "Model Profile" };
    private static string? EmptyToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static IReadOnlyList<string> Clean(IEnumerable<string> values) => values.Select(value => value.Trim()).Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    private static string Slug(string value)
    {
        var result = InvalidSlugCharacters().Replace(value.Trim().ToLowerInvariant(), "-").Trim('-');
        return string.IsNullOrWhiteSpace(result) ? "resource" : result[..Math.Min(result.Length, 60)];
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    private sealed class BindingUsage(PackCompositionResourceKey resource, PackBindingTargetKind targetKind, string displayName, bool required)
    {
        public PackCompositionResourceKey Resource { get; } = resource;
        public PackBindingTargetKind TargetKind { get; } = targetKind;
        public string DisplayName { get; } = displayName;
        public bool Required { get; set; } = required;
        public HashSet<PackCompositionResourceKey> UsedBy { get; } = [];
    }

    [GeneratedRegex("[^a-z0-9-]+", RegexOptions.CultureInvariant)]
    private static partial Regex InvalidSlugCharacters();
}
