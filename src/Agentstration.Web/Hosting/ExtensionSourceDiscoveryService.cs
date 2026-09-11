using System.Data.Common;
using System.Text;
using System.Text.Json;
using Agentstration.Aep.Abstractions;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Contracts;
using Agentstration.Management.Core;
using Agentstration.Resources;
using Agentstration.Secrets;
using Agentstration.Secrets.Abstractions;
using Agentstration.Secrets.Local;

namespace Agentstration.Web.Hosting;

public sealed class ExtensionSourceDiscoveryService(
    IConfiguration configuration,
    ExtensionRegistrationManagementService registrations,
    SecretManagementService secrets,
    IIdentityStore identities,
    AepEnrollmentSettingsService enrollmentSettings,
    IRequestContextScopeFactory requestScopes,
    TimeProvider timeProvider) : IAepEnrollmentAnnouncementProvisioner
{
    public AepEnrollmentMethod Method => AepEnrollmentMethod.SharedKeyFile;

    public async Task ValidateAsync(
        AepEnrollmentAnnouncement announcement,
        AepEnrollmentProof? proof,
        CancellationToken cancellationToken)
    {
        var section = configuration.GetSection($"Agentstration:Extensions:{announcement.Extension.Id}");
        if (EnrollmentMode(section["EnrollmentMode"]) != AepEnrollmentMode.SharedKeyFile)
            throw new AepEnrollmentException("shared_key_not_configured", $"SharedKeyFile enrollment is not configured for extension '{announcement.Extension.Id}'.", 404);
        var configuredPath = section["SharedKeyFile:Path"];
        if (string.IsNullOrWhiteSpace(configuredPath))
            throw new AepEnrollmentException("shared_key_not_configured", $"SharedKeyFile enrollment for extension '{announcement.Extension.Id}' has no configured key.", 404);
        var path = Path.GetFullPath(configuredPath);
        SharedKeyFileSecretVaultProvider.Validate(path);
        await ValidateSharedKeyProofAsync(announcement, proof, path, cancellationToken);
    }

    public async Task<string> ProvisionAsync(
        AepEnrollmentAnnouncement announcement,
        ResourceScopeRef targetScopeRef,
        CancellationToken cancellationToken)
    {
        var section = configuration.GetSection($"Agentstration:Extensions:{announcement.Extension.Id}");
        if (EnrollmentMode(section["EnrollmentMode"]) != AepEnrollmentMode.SharedKeyFile)
            throw new AepEnrollmentException("shared_key_not_configured", $"SharedKeyFile enrollment is not configured for extension '{announcement.Extension.Id}'.", 404);
        var configuredPath = section["SharedKeyFile:Path"];
        if (string.IsNullOrWhiteSpace(configuredPath))
            throw new AepEnrollmentException("shared_key_not_configured", $"SharedKeyFile enrollment for extension '{announcement.Extension.Id}' has no configured key.", 404);
        var path = Path.GetFullPath(configuredPath);
        SharedKeyFileSecretVaultProvider.Validate(path);
        var name = RegistrationName(section, announcement.Extension.Id);
        var source = new ExtensionSource(
            name,
            section["DisplayName"] ?? announcement.Extension.Name,
            announcement.Endpoint,
            announcement.Extension.Id,
            ExtensionRegistrationSource.Configuration,
            AepTransportAuthenticationMode.StaticBearer,
            AepEnrollmentMode.SharedKeyFile,
            new ResourceReference(SharedKeySecretName(name), targetScopeRef, ResourceNamespace.Default),
            path);
        await EnsureSharedKeyResourcesAsync(source, targetScopeRef, cancellationToken);
        _ = await registrations.SynchronizeAsync(name, new ExtensionRegistrationProperties
        {
            DisplayName = source.DisplayName,
            Endpoint = source.Endpoint,
            ExpectedExtensionId = source.ExpectedExtensionId,
            Source = source.Source,
            AuthenticationMode = source.AuthenticationMode,
            EnrollmentMode = source.EnrollmentMode,
            Credential = source.Credential
        }, targetScopeRef, cancellationToken);
        return name;
    }

    public async Task DiscoverForActiveWorkspacesAsync(CancellationToken cancellationToken)
    {
        _ = ReadSources();
        if (!(await identities.ListTenantsAsync(cancellationToken)).Any(value => value.Status == TenantStatus.Active)) return;
        using var scope = requestScopes.PushSystem();
        _ = await DiscoverAsync(cancellationToken);
    }

    public async Task<ExtensionDiscoveryResponse> DiscoverAsync(CancellationToken cancellationToken)
    {
        var sources = ReadSources();
        var created = 0;
        var updated = 0;
        var unchanged = 0;

        foreach (var source in sources.Values)
        {
            if (!await enrollmentSettings.IsEnabledAsync(source.EnrollmentMode, cancellationToken))
                throw new ExtensionRegistrationValidationException($"The '{source.EnrollmentMode}' AEP enrollment mode is disabled by the Agentstration enrollment policy.");
            var existing = await registrations.GetExactAsync(
                ResourceScopeRef.Instance,
                ResourceNamespace.Default,
                source.Name,
                cancellationToken);
            var definition = new ExtensionRegistrationProperties
            {
                DisplayName = source.DisplayName,
                Endpoint = source.Endpoint,
                ExpectedExtensionId = source.ExpectedExtensionId,
                Source = source.Source,
                AuthenticationMode = source.AuthenticationMode,
                EnrollmentMode = source.EnrollmentMode,
                Credential = source.Credential
            };

            if (source.EnrollmentMode == AepEnrollmentMode.SharedKeyFile)
                await EnsureSharedKeyResourcesAsync(source, ResourceScopeRef.Instance, cancellationToken);

            if (existing is null)
            {
                created++;
            }
            else if (existing.Value.Definition == definition)
            {
                unchanged++;
            }
            else
            {
                updated++;
            }

            _ = await registrations.SynchronizeAsync(source.Name, definition, cancellationToken);
        }

        return new(sources.Count, created, updated, unchanged);
    }

    private Dictionary<string, ExtensionSource> ReadSources()
    {
        var sources = new Dictionary<string, ExtensionSource>(StringComparer.Ordinal);
        foreach (var section in configuration.GetSection("Agentstration:Extensions").GetChildren())
        {
            if (!TryEndpoint(section["Endpoint"], out var endpoint)) continue;
            var name = RegistrationName(section, section.Key);
            var enrollmentMode = EnrollmentMode(section["EnrollmentMode"]);
            var sharedKeyPath = section["SharedKeyFile:Path"];
            if (enrollmentMode == AepEnrollmentMode.SharedKeyFile)
            {
                if (string.IsNullOrWhiteSpace(sharedKeyPath))
                    throw new InvalidOperationException($"SharedKeyFile enrollment for extension '{section.Key}' requires SharedKeyFile:Path.");
                sharedKeyPath = Path.GetFullPath(sharedKeyPath);
                SharedKeyFileSecretVaultProvider.Validate(sharedKeyPath);
            }
            var authenticationMode = AuthenticationMode(section["AuthenticationMode"]);
            if (enrollmentMode == AepEnrollmentMode.SharedKeyFile && authenticationMode == AepTransportAuthenticationMode.None)
                authenticationMode = AepTransportAuthenticationMode.StaticBearer;
            var credential = enrollmentMode == AepEnrollmentMode.SharedKeyFile
                ? new ResourceReference(SharedKeySecretName(name), ResourceScopeRef.Instance, ResourceNamespace.Default)
                : Credential(section.GetSection("Credential"));
            sources[name] = new(
                name,
                section["DisplayName"] ?? section.Key,
                endpoint,
                section.Key,
                ExtensionRegistrationSource.Configuration,
                authenticationMode,
                enrollmentMode,
                credential,
                sharedKeyPath);
        }

        foreach (var section in configuration.GetSection("ConnectionStrings").GetChildren())
        {
            if (!section.Key.EndsWith("-extension", StringComparison.OrdinalIgnoreCase)
                || !TryConnectionStringEndpoint(section.Value, out var endpoint)) continue;

            sources.TryGetValue(section.Key, out var configured);
            sources[section.Key] = new(
                section.Key,
                configured?.DisplayName ?? section.Key,
                endpoint,
                configured?.ExpectedExtensionId,
                ExtensionRegistrationSource.Aspire,
                configured?.AuthenticationMode ?? AepTransportAuthenticationMode.None,
                configured?.EnrollmentMode ?? AepEnrollmentMode.Disabled,
                configured?.Credential,
                configured?.SharedKeyFilePath);
        }

        return sources;
    }

    private static bool TryEndpoint(string? value, out Uri endpoint)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out endpoint!) && endpoint.Scheme is "http" or "https") return true;
        endpoint = null!;
        return false;
    }

    private static bool TryConnectionStringEndpoint(string? connectionString, out Uri endpoint)
    {
        if (TryEndpoint(connectionString, out endpoint)) return true;
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            try
            {
                var values = new DbConnectionStringBuilder { ConnectionString = connectionString };
                if (values.TryGetValue("Endpoint", out var value) && TryEndpoint(value?.ToString(), out endpoint)) return true;
            }
            catch (ArgumentException)
            {
                // Invalid connection strings are ignored as non-discoverable sources.
            }
        }

        endpoint = null!;
        return false;
    }

    private static string RegistrationName(IConfigurationSection section, string extensionId) =>
        string.IsNullOrWhiteSpace(section["RegistrationName"])
            ? Slug(extensionId)
            : section["RegistrationName"]!;

    private static AepTransportAuthenticationMode AuthenticationMode(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? AepTransportAuthenticationMode.None
            : Enum.Parse<AepTransportAuthenticationMode>(value, ignoreCase: true);

    private static AepEnrollmentMode EnrollmentMode(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? AepEnrollmentMode.Disabled
            : Enum.Parse<AepEnrollmentMode>(value, ignoreCase: true);

    private static ResourceReference? Credential(IConfigurationSection section)
    {
        var name = section["Name"];
        if (string.IsNullOrWhiteSpace(name)) return null;
        var scopeValue = section["ScopeRef"];
        ResourceScopeRef? scopeRef = string.IsNullOrWhiteSpace(scopeValue) ? null : ResourceScopeRef.Parse(scopeValue);
        return new ResourceReference(name, scopeRef, ResourceNamespace.Parse(section["Namespace"]));
    }

    private async Task EnsureSharedKeyResourcesAsync(
        ExtensionSource source,
        ResourceScopeRef targetScopeRef,
        CancellationToken cancellationToken)
    {
        var path = source.SharedKeyFilePath
            ?? throw new InvalidOperationException($"SharedKeyFile enrollment for extension '{source.Name}' has no file path.");
        var vaultName = SharedKeyVaultName(source.Name);
        var vaultOptions = new Dictionary<string, JsonElement> { ["path"] = JsonSerializer.SerializeToElement(path) };
        var vault = await secrets.GetVaultExactAsync(targetScopeRef, vaultName, cancellationToken);
        if (vault is null)
        {
            _ = await secrets.CreateVaultAsync(new VaultResource
            {
                ApiVersion = ManagementApiVersions.CoreV1,
                Kind = ResourceKinds.Vault,
                Metadata = new ResourceMetadata { Name = vaultName },
                ScopeRef = targetScopeRef,
                Definition = new VaultProperties
                {
                    DisplayName = $"{source.DisplayName} shared key file",
                    ProviderType = SharedKeyFileSecretVaultProvider.Type,
                    ProviderOptions = vaultOptions
                }
            }, cancellationToken);
        }
        else if (!string.Equals(vault.Value.Definition.ProviderType, SharedKeyFileSecretVaultProvider.Type, StringComparison.Ordinal)
            || !vault.Value.Definition.ProviderOptions.TryGetValue("path", out var configuredPath)
            || !string.Equals(configuredPath.GetString(), path, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"The managed shared-key vault '{vaultName}' conflicts with the configured extension path.");
        }

        var secretName = SharedKeySecretName(source.Name);
        var secret = await secrets.GetSecretExactAsync(targetScopeRef, secretName, cancellationToken);
        if (secret is null)
        {
            _ = await secrets.CreateSecretAsync(new SecretResource
            {
                ApiVersion = ManagementApiVersions.CoreV1,
                Kind = ResourceKinds.Secret,
                Metadata = new ResourceMetadata { Name = secretName },
                ScopeRef = targetScopeRef,
                Definition = new SecretProperties
                {
                    DisplayName = $"{source.DisplayName} shared key",
                    Vault = new ResourceReference(vaultName, targetScopeRef, ResourceNamespace.Default),
                    Key = "token",
                    SecretType = SecretType.Opaque
                }
            }, cancellationToken);
        }
    }

    private static string SharedKeyVaultName(string registrationName) => $"{registrationName}-shared-key-file";
    private static string SharedKeySecretName(string registrationName) => $"{registrationName}-shared-key";

    private async Task ValidateSharedKeyProofAsync(
        AepEnrollmentAnnouncement announcement,
        AepEnrollmentProof? proof,
        string path,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        using var value = await new SharedKeyFileSecretVaultProvider().GetAsync(
            new SecretVaultContext(default, default, new Dictionary<string, JsonElement>
            {
                ["path"] = JsonSerializer.SerializeToElement(path)
            }),
            "token",
            cancellationToken);
        var sharedKey = value is null ? null : Encoding.UTF8.GetString(value.AccessValue().Span);
        if (proof is null || proof.Timestamp < now - 300 || proof.Timestamp > now + 300 || sharedKey is null
            || !AepEnrollmentProofs.Verify(announcement, proof, sharedKey))
            throw new AepEnrollmentException("shared_key_proof_invalid", "The SharedKeyFile announcement proof is missing, expired, or invalid.", 401);
    }

    private static string Slug(string value)
    {
        var slug = string.Concat(value.ToLowerInvariant().Select(character => char.IsAsciiLetterOrDigit(character) ? character : '-')).Trim('-');
        while (slug.Contains("--", StringComparison.Ordinal)) slug = slug.Replace("--", "-", StringComparison.Ordinal);
        return slug.Length <= 128 ? slug : slug[..128].TrimEnd('-');
    }

    private sealed record ExtensionSource(
        string Name,
        string DisplayName,
        Uri Endpoint,
        string? ExpectedExtensionId,
        ExtensionRegistrationSource Source,
        AepTransportAuthenticationMode AuthenticationMode,
        AepEnrollmentMode EnrollmentMode,
        ResourceReference? Credential,
        string? SharedKeyFilePath);
}
