using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentstration.ResourceManagement;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Agentstration.Web.Hosting;

internal sealed record BootstrapResourceSource(
    BootstrapResourceDocument Resource,
    string Location);

internal sealed record LoadedBootstrapProfile(
    BootstrapProfileSummary Summary,
    string DirectoryPath,
    IReadOnlyList<BootstrapResourceSource> Resources);

internal sealed record BootstrapProfileBindingDefinition
{
    public string Name { get; init; } = string.Empty;
    public BootstrapBindingTargetKind? TargetKind { get; init; }
    public string? DisplayName { get; init; }
    public string? Description { get; init; }
    public bool Required { get; init; } = true;
    public ResourceReference? DefaultTarget { get; init; }
    public BootstrapContributionSelector? ExtensionTarget { get; init; }
}

internal sealed record BootstrapProfileDefinition
{
    public string? DisplayName { get; init; }
    public string? Description { get; init; }
    public string? Version { get; init; }
    public string TargetScope { get; init; } = "workspace";
    public IReadOnlyList<BootstrapProfileBindingDefinition> Bindings { get; init; } = [];
}

public sealed class BootstrapProfileCatalog(
    IConfiguration configuration,
    IHostEnvironment environment)
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private const string ConfigurationSection = "Agentstration:Bootstrap";
    private const string DescriptorFileName = "profile.yaml";
    private const int MaximumCatalogProfiles = 128;
    private const int MaximumProfilesPerApplication = 16;
    private const int MaximumFilesPerProfile = 256;
    private const int MaximumResourceFilesPerProfile = 128;
    private const int MaximumDocumentsPerProfile = 512;
    private const int MaximumBindingsPerProfile = 64;
    private const int MaximumManifestBytes = 1024 * 1024;
    private const long MaximumProfileBytes = 32L * 1024 * 1024;

    public int MaximumSelectedProfiles => MaximumProfilesPerApplication;

    public async Task<BootstrapCatalogSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        var options = configuration.GetSection(ConfigurationSection).Get<DeclarativeBootstrapOptions>() ?? new();
        string? rootPath;
        try { rootPath = ResolveRootPath(required: false); }
        catch (DeclarativeBootstrapException exception)
        {
            return new(null, options.InitialBootstrapEnabled, options.InitialProfiles, [], exception.Message);
        }
        if (rootPath is null)
            return new(null, options.InitialBootstrapEnabled, options.InitialProfiles, []);
        if (!Directory.Exists(rootPath))
            return new(rootPath, options.InitialBootstrapEnabled, options.InitialProfiles, [], $"Bootstrap root directory '{rootPath}' does not exist.");

        var directories = Directory.EnumerateDirectories(rootPath, "*", SearchOption.TopDirectoryOnly)
            .OrderBy(Path.GetFileName, StringComparer.Ordinal)
            .Take(MaximumCatalogProfiles + 1)
            .ToArray();
        if (directories.Length > MaximumCatalogProfiles)
            return new(
                rootPath,
                options.InitialBootstrapEnabled,
                options.InitialProfiles,
                [],
                $"Bootstrap catalog contains more than {MaximumCatalogProfiles} profiles.");

        var profiles = new List<BootstrapProfileSummary>();
        foreach (var directory in directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(directory);
            try { profiles.Add((await LoadProfileAsync(rootPath, name, cancellationToken)).Summary); }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                profiles.Add(new(name, name, null, BootstrapProfileScope.Workspace, 0, 0, string.Empty, [], false, exception.Message));
            }
        }
        return new(rootPath, options.InitialBootstrapEnabled, options.InitialProfiles, profiles);
    }

    internal async Task<IReadOnlyList<LoadedBootstrapProfile>> LoadAsync(
        IReadOnlyList<string> profiles,
        CancellationToken cancellationToken)
    {
        if (profiles.Count > MaximumProfilesPerApplication)
            throw new DeclarativeBootstrapException($"At most {MaximumProfilesPerApplication} bootstrap profiles can be applied together.");
        var duplicate = profiles.GroupBy(value => value, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new DeclarativeBootstrapException($"Bootstrap profile '{duplicate.Key}' is configured more than once.");
        var rootPath = ResolveRootPath(required: true)!;
        var loaded = new List<LoadedBootstrapProfile>(profiles.Count);
        foreach (var profile in profiles)
            loaded.Add(await LoadProfileAsync(rootPath, profile, cancellationToken));
        return loaded;
    }

    internal Task<LoadedBootstrapProfile> LoadFromRootAsync(
        string rootPath,
        string profile,
        CancellationToken cancellationToken) =>
        LoadProfileAsync(rootPath, profile, cancellationToken);

    private async Task<LoadedBootstrapProfile> LoadProfileAsync(
        string rootPath,
        string profile,
        CancellationToken cancellationToken)
    {
        ValidateProfileName(profile);
        if (!Directory.Exists(rootPath))
            throw new DeclarativeBootstrapException($"Bootstrap root directory '{rootPath}' does not exist.");
        var profilePath = Path.GetFullPath(Path.Combine(rootPath, profile));
        if (!Directory.Exists(profilePath))
            throw new DeclarativeBootstrapException($"Bootstrap profile '{profile}' does not exist under root directory '{rootPath}'.");
        RejectReparsePoint(new DirectoryInfo(profilePath), $"Bootstrap profile '{profile}'");
        foreach (var directory in Directory.EnumerateDirectories(profilePath, "*", SearchOption.AllDirectories))
            RejectReparsePoint(new DirectoryInfo(directory), $"Bootstrap profile directory '{Path.GetRelativePath(profilePath, directory)}'");

        var allFiles = Directory.EnumerateFiles(profilePath, "*", SearchOption.AllDirectories)
            .OrderBy(path => Path.GetRelativePath(profilePath, path), StringComparer.Ordinal)
            .ToArray();
        if (allFiles.Length > MaximumFilesPerProfile)
            throw new DeclarativeBootstrapException($"Bootstrap profile '{profile}' contains more than {MaximumFilesPerProfile} files.");
        long totalBytes = 0;
        var manifests = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in allFiles)
        {
            var info = new FileInfo(file);
            RejectReparsePoint(info, $"Bootstrap profile file '{Path.GetRelativePath(profilePath, file)}'");
            totalBytes = checked(totalBytes + info.Length);
            if (totalBytes > MaximumProfileBytes)
                throw new DeclarativeBootstrapException($"Bootstrap profile '{profile}' exceeds {MaximumProfileBytes} bytes.");
            var relativePath = Path.GetRelativePath(profilePath, file).Replace('\\', '/');
            digest.AppendData(Encoding.UTF8.GetBytes(relativePath));
            digest.AppendData([0]);
            var bytes = await File.ReadAllBytesAsync(file, cancellationToken);
            if (bytes.LongLength != info.Length)
                throw new DeclarativeBootstrapException($"Bootstrap profile '{profile}' changed while it was being read.");
            digest.AppendData(bytes);
            if (!relativePath.Contains('/') && IsManifest(file))
            {
                if (bytes.Length > MaximumManifestBytes)
                    throw new DeclarativeBootstrapException($"Bootstrap file '{profile}/{relativePath}' exceeds {MaximumManifestBytes} bytes.");
                manifests.Add(relativePath, bytes);
            }
        }

        var displayName = profile;
        string? description = null;
        var version = "1.0.0";
        var scope = BootstrapProfileScope.Workspace;
        IReadOnlyList<BootstrapProfileBinding> bindings = [];
        if (manifests.TryGetValue(DescriptorFileName, out var descriptorBytes))
        {
            var descriptor = ReadDocuments(descriptorBytes, DescriptorFileName, profile);
            if (descriptor.Count != 1)
                throw new DeclarativeBootstrapException($"Bootstrap profile descriptor '{profile}/{DescriptorFileName}' must contain exactly one document.");
            var document = descriptor[0];
            ValidateEnvelope(document, $"{profile}/{DescriptorFileName}");
            if (!string.Equals(document.Kind, BootstrapKinds.BootstrapProfile, StringComparison.Ordinal))
                throw new DeclarativeBootstrapException($"Bootstrap profile descriptor '{profile}/{DescriptorFileName}' must use kind '{BootstrapKinds.BootstrapProfile}'.");
            if (!string.Equals(document.Metadata.Name, profile, StringComparison.Ordinal))
                throw new DeclarativeBootstrapException($"Bootstrap profile descriptor metadata.name must match directory '{profile}'.");
            BootstrapProfileDefinition definition;
            try
            {
                definition = document.Definition.Deserialize<BootstrapProfileDefinition>(SerializerOptions)
                    ?? throw new DeclarativeBootstrapException($"Bootstrap profile descriptor '{profile}/{DescriptorFileName}' requires a definition.");
            }
            catch (JsonException exception)
            {
                throw new DeclarativeBootstrapException($"Bootstrap profile descriptor '{profile}/{DescriptorFileName}' has an invalid definition.", exception);
            }
            if (!Enum.TryParse<BootstrapProfileScope>(definition.TargetScope, true, out scope))
                throw new DeclarativeBootstrapException($"Bootstrap profile '{profile}' uses unsupported targetScope '{definition.TargetScope}'.");
            if (definition.Version is { } declaredVersion)
            {
                if (!IsVersion(declaredVersion))
                    throw new DeclarativeBootstrapException($"Bootstrap profile '{profile}' requires a version in X.Y.Z format.");
                version = declaredVersion;
            }
            displayName = string.IsNullOrWhiteSpace(definition.DisplayName) ? profile : definition.DisplayName.Trim();
            description = string.IsNullOrWhiteSpace(definition.Description) ? null : definition.Description.Trim();
            bindings = ValidateBindings(profile, scope, definition.Bindings);
        }

        var resourceFiles = manifests.Keys.Where(name => !string.Equals(name, DescriptorFileName, StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal).ToArray();
        if (resourceFiles.Length > MaximumResourceFilesPerProfile)
            throw new DeclarativeBootstrapException($"Bootstrap profile '{profile}' contains more than {MaximumResourceFilesPerProfile} resource files.");
        var resources = new List<BootstrapResourceSource>();
        foreach (var file in resourceFiles)
        {
            var documents = ReadDocuments(manifests[file], file, profile);
            for (var index = 0; index < documents.Count; index++)
            {
                var location = $"{profile}/{Path.GetFileName(file)} document {index + 1}";
                ValidateEnvelope(documents[index], location);
                resources.Add(new(documents[index], location));
                if (resources.Count > MaximumDocumentsPerProfile)
                    throw new DeclarativeBootstrapException($"Bootstrap profile '{profile}' contains more than {MaximumDocumentsPerProfile} resources.");
            }
        }

        var hash = Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant();
        return new(
            new(profile, displayName, description, scope, resourceFiles.Length, resources.Count, hash, bindings)
            {
                Version = version,
                Topology = resources.Where(item => item.Resource.Kind is "Tenant" or "Workspace")
                    .Select(item => new BootstrapTopologyResource(item.Resource.Kind, item.Resource.Metadata.Name,
                        item.Resource.Definition.TryGetProperty("tenantRef", out var tenant) && tenant.TryGetProperty("name", out var name) ? name.GetString() : null)).ToArray()
            },
            profilePath,
            resources);
    }

    internal static bool IsVersion(string value)
    {
        var parts = value.Split('.');
        return value.Length <= 64 && parts.Length == 3 && parts.All(part =>
            part.Length > 0 && (part.Length == 1 || part[0] != '0') && part.All(char.IsAsciiDigit));
    }

    internal async Task<byte[]> ArchiveAsync(LoadedBootstrapProfile profile, CancellationToken cancellationToken)
    {
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var stream = new MemoryStream();
        using (var archive = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            var directories = Directory.EnumerateDirectories(profile.DirectoryPath, "*", SearchOption.AllDirectories);
            foreach (var directory in directories)
                RejectReparsePoint(new DirectoryInfo(directory), profile.Summary.Name);
            var files = Directory.EnumerateFiles(profile.DirectoryPath, "*", SearchOption.AllDirectories)
                .OrderBy(path => Path.GetRelativePath(profile.DirectoryPath, path), StringComparer.Ordinal)
                .Take(MaximumFilesPerProfile + 1).ToArray();
            if (files.Length > MaximumFilesPerProfile)
                throw new DeclarativeBootstrapException("The Bootstrap profile exceeds its archive limits.");
            long size = 0;
            foreach (var file in files)
            {
                var info = new FileInfo(file);
                RejectReparsePoint(info, profile.Summary.Name);
                size += info.Length;
                if (size > MaximumProfileBytes)
                    throw new DeclarativeBootstrapException("The Bootstrap profile exceeds its archive limits.");
                var path = Path.GetRelativePath(profile.DirectoryPath, file).Replace('\\', '/');
                var bytes = await File.ReadAllBytesAsync(file, cancellationToken);
                if (bytes.LongLength != info.Length)
                    throw new DeclarativeBootstrapException("The Bootstrap profile changed while its version was being recorded.");
                digest.AppendData(Encoding.UTF8.GetBytes(path));
                digest.AppendData([0]);
                digest.AppendData(bytes);
                await using var output = archive.CreateEntry(path).Open();
                await output.WriteAsync(bytes, cancellationToken);
            }
        }
        if (!string.Equals(Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant(), profile.Summary.Digest, StringComparison.Ordinal))
            throw new DeclarativeBootstrapException("The Bootstrap profile changed while its version was being recorded.");
        return stream.ToArray();
    }

    private static IReadOnlyList<BootstrapResourceDocument> ReadDocuments(byte[] bytes, string name, string profile)
    {
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var reader = new StreamReader(stream);
            return ResourceManifestSerializer.FromYamlDocuments<BootstrapResourceDocument>(reader.ReadToEnd());
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not DeclarativeBootstrapException)
        {
            throw new DeclarativeBootstrapException($"Bootstrap file '{profile}/{name}' contains invalid YAML or an invalid resource envelope.", exception);
        }
    }

    private string? ResolveRootPath(bool required)
    {
        var configuredPath = configuration[$"{ConfigurationSection}:Path"];
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            if (required)
                throw new DeclarativeBootstrapException($"{ConfigurationSection}:Path is required when bootstrap profiles are selected.");
            return null;
        }
        return Path.IsPathRooted(configuredPath)
            ? Path.GetFullPath(configuredPath)
            : Path.GetFullPath(configuredPath, environment.ContentRootPath);
    }

    internal static void ValidateProfileName(string profile)
    {
        if (string.IsNullOrWhiteSpace(profile)
            || !string.Equals(profile, profile.Trim(), StringComparison.Ordinal)
            || string.Equals(profile, ".", StringComparison.Ordinal)
            || string.Equals(profile, "..", StringComparison.Ordinal)
            || profile.Contains('/')
            || profile.Contains('\\')
            || !string.Equals(profile, Path.GetFileName(profile), StringComparison.Ordinal)
            || profile.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new DeclarativeBootstrapException($"Bootstrap profile name '{profile}' must be a single valid directory name.");
    }

    internal static void ValidateEnvelope(BootstrapResourceDocument resource, string location)
    {
        if (string.IsNullOrWhiteSpace(resource.ApiVersion))
            throw new DeclarativeBootstrapException($"Bootstrap resource '{location}' is missing apiVersion.");
        if (!string.Equals(resource.ApiVersion, ResourceApiVersions.CoreV1, StringComparison.Ordinal))
            throw new DeclarativeBootstrapException($"Bootstrap resource '{location}' uses unsupported apiVersion '{resource.ApiVersion}'. Expected '{ResourceApiVersions.CoreV1}'.");
        if (string.IsNullOrWhiteSpace(resource.Kind))
            throw new DeclarativeBootstrapException($"Bootstrap resource '{location}' is missing kind.");
        if (string.IsNullOrWhiteSpace(resource.Metadata.Name))
            throw new DeclarativeBootstrapException($"Bootstrap resource '{location}' is missing metadata.name.");
        if (resource.Definition.ValueKind != JsonValueKind.Object)
            throw new DeclarativeBootstrapException($"Bootstrap resource '{location}' requires an object definition.");
    }

    private static IReadOnlyList<BootstrapProfileBinding> ValidateBindings(
        string profile,
        BootstrapProfileScope scope,
        IReadOnlyList<BootstrapProfileBindingDefinition> definitions)
    {
        if (definitions.Count == 0) return [];
        if (scope != BootstrapProfileScope.Workspace && definitions.Any(binding => binding.ExtensionTarget is null))
            throw new DeclarativeBootstrapException($"Bootstrap profile '{profile}' can declare bindings only with Workspace scope.");
        if (definitions.Count > MaximumBindingsPerProfile)
            throw new DeclarativeBootstrapException($"Bootstrap profile '{profile}' declares more than {MaximumBindingsPerProfile} bindings.");
        var result = new List<BootstrapProfileBinding>(definitions.Count);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var binding in definitions)
        {
            if (string.IsNullOrWhiteSpace(binding.Name) || !string.Equals(binding.Name, binding.Name.Trim(), StringComparison.Ordinal))
                throw new DeclarativeBootstrapException($"Bootstrap profile '{profile}' contains a binding with an invalid name.");
            if (!names.Add(binding.Name))
                throw new DeclarativeBootstrapException($"Bootstrap profile '{profile}' declares binding '{binding.Name}' more than once.");
            if (binding.TargetKind is null)
                throw new DeclarativeBootstrapException($"Bootstrap profile binding '{profile}/{binding.Name}' requires targetKind.");
            if (binding.ExtensionTarget is not null && binding.DefaultTarget is not null)
                throw new DeclarativeBootstrapException($"Bootstrap binding '{profile}/{binding.Name}' cannot declare both defaultTarget and extensionTarget.");
            if (binding.ExtensionTarget is { } extensionTarget)
            {
                BootstrapExtensionReferencePreparation.Validate(extensionTarget);
                if (BootstrapExtensionReferencePreparation.BindingKind(binding.TargetKind.Value) != extensionTarget.Kind)
                    throw new DeclarativeBootstrapException($"Bootstrap binding '{profile}/{binding.Name}' has a contribution kind incompatible with targetKind.");
            }
            result.Add(new(
                binding.Name,
                binding.TargetKind.Value,
                string.IsNullOrWhiteSpace(binding.DisplayName) ? binding.Name : binding.DisplayName.Trim(),
                string.IsNullOrWhiteSpace(binding.Description) ? null : binding.Description.Trim(),
                binding.Required,
                binding.DefaultTarget,
                binding.ExtensionTarget));
        }
        return result;
    }

    private static bool IsManifest(string path) =>
        string.Equals(Path.GetExtension(path), ".yaml", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Path.GetExtension(path), ".yml", StringComparison.OrdinalIgnoreCase);

    private static void RejectReparsePoint(FileSystemInfo info, string label)
    {
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new DeclarativeBootstrapException($"{label} cannot be a symbolic link or reparse point.");
    }
}
