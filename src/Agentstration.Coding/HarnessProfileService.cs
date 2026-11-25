using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Agentstration.Coding.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Schemas;
using Agentstration.Security.Contracts;
using Agentstration.Work;

namespace Agentstration.Coding;

public sealed partial class HarnessProfileService(IResourceStore store, IResourceScopeOperations scopes, ICurrentRequestContext current,
    IAuthorizationService authorization, IResourceGovernanceContext governance, ISchemaFormatRegistry schemas,
    CodexHarnessMaterializer materializer, ISecurityAuditWriter audit, TimeProvider clock)
{
    private static readonly ActivitySource Activities = new("Agentstration.Coding.Harness");

    public async Task<Result<StoredResource<HarnessNativeConfigurationResource>>> CreateConfigurationAsync(WorkspaceId workspaceId,
        CreateHarnessNativeConfigurationRequest command, CancellationToken token)
    {
        if (!await AllowedAsync(workspaceId, true, token)) return ConfigurationFailure("harness_profile_denied");
        if (!Name(command.Name) || !IsPublicationVersion(command.Version) || !CodexHarnessMaterializer.ValidNative(command.Definition)) return ConfigurationFailure("harness_configuration_invalid");
        var scope = Scope(workspaceId); var digest = CodexHarnessMaterializer.ContentDigest(command.Definition);
        var address = Address(scope, command.Namespace, HarnessResourceKinds.NativeConfiguration, VersionName(command.Name, command.Version));
        var existing = await store.GetExactAsync<HarnessNativeConfigurationResource>(address, token);
        if (existing is not null) return existing.Value.ContentDigest == digest ? Result<StoredResource<HarnessNativeConfigurationResource>>.Success(existing)
            : ConfigurationFailure("harness_configuration_version_conflict");
        var resource = new HarnessNativeConfigurationResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = HarnessResourceKinds.NativeConfiguration,
            ScopeRef = scope,
            Metadata = new() { Name = address.Name, Namespace = command.Namespace },
            ConfigurationName = command.Name,
            Version = command.Version,
            Definition = command.Definition,
            ContentDigest = digest,
            PublishedAt = clock.GetUtcNow(),
            Generation = 1,
            Status = Succeeded()
        };
        try
        {
            return Result<StoredResource<HarnessNativeConfigurationResource>>.Success(await scopes.WriteAsync(resource, scope,
                AuthorizationPermissions.ResourcesWrite, cancellation => store.CreateImmutableAsync(resource, cancellation), token));
        }
        catch (ResourceConcurrencyException) { return ConfigurationFailure("harness_configuration_version_conflict"); }
        catch (ResourceGovernanceViolationException exception) { return ConfigurationFailure(exception.Code); }
    }
    public async Task<Result<StoredResource<HarnessNativeConfigurationResource>>> GetConfigurationAsync(WorkspaceId workspaceId,
        ResourceNamespace resourceNamespace, string name, string version, CancellationToken token)
    {
        if (!await AllowedAsync(workspaceId, false, token) || !Name(name) || !IsPublicationVersion(version)) return ConfigurationFailure("harness_configuration_not_found");
        var value = await store.GetExactAsync<HarnessNativeConfigurationResource>(Address(Scope(workspaceId), resourceNamespace,
            HarnessResourceKinds.NativeConfiguration, VersionName(name, version)), token);
        return value is null ? ConfigurationFailure("harness_configuration_not_found") : Result<StoredResource<HarnessNativeConfigurationResource>>.Success(value);
    }
    public async Task<Result<StoredResource<HarnessProfileResource>>> CreateAsync(WorkspaceId workspaceId, HarnessProfileResource resource, CancellationToken token)
    {
        if (!await AllowedAsync(workspaceId, true, token)) return ProfileFailure("harness_profile_denied");
        if (resource.ApiVersion != ResourceApiVersions.CoreV1 || resource.Kind != HarnessResourceKinds.Profile || !Name(resource.Name)
            || resource.Uid != Guid.Empty || resource.Generation != 0 || resource.ScopeRef is { } supplied && supplied != Scope(workspaceId)) return ProfileFailure("harness_profile_invalid");
        var desired = resource with { ScopeRef = Scope(workspaceId), Generation = 1, Status = Succeeded() };
        var valid = await ValidateAsync(workspaceId, desired.Definition, token);
        if (!valid.IsSuccess) return ProfileFailure(valid.ErrorCode!);
        desired = desired with { Definition = CanonicalDefinition(desired.Definition) };
        try
        {
            return Result<StoredResource<HarnessProfileResource>>.Success(await scopes.WriteAsync(desired, Scope(workspaceId), AuthorizationPermissions.ResourcesWrite,
                cancellation => store.PutExactAsync(Scope(workspaceId), desired, null, true, cancellation), token));
        }
        catch (ResourceConcurrencyException) { return ProfileFailure("harness_profile_conflict"); }
        catch (ResourceGovernanceViolationException exception) { return ProfileFailure(exception.Code); }
    }
    public async Task<Result<StoredResource<HarnessProfileResource>>> GetAsync(WorkspaceId workspaceId, ResourceNamespace resourceNamespace, string name, CancellationToken token)
    {
        if (!await AllowedAsync(workspaceId, false, token) || !Name(name)) return ProfileFailure("harness_profile_not_found");
        var value = await store.GetExactAsync<HarnessProfileResource>(Address(Scope(workspaceId), resourceNamespace, HarnessResourceKinds.Profile, name), token);
        return value is null ? ProfileFailure("harness_profile_not_found") : Result<StoredResource<HarnessProfileResource>>.Success(value);
    }
    public async Task<Result<StoredResource<HarnessProfileResource>>> UpdateAsync(WorkspaceId workspaceId, ResourceNamespace resourceNamespace, string name,
        HarnessProfileDefinition definition, string expectedETag, CancellationToken token)
    {
        if (!await AllowedAsync(workspaceId, true, token)) return ProfileFailure("harness_profile_denied");
        var existing = await GetAsync(workspaceId, resourceNamespace, name, token);
        if (!existing.IsSuccess) return existing;
        if (string.IsNullOrWhiteSpace(expectedETag) || expectedETag != existing.Value!.ETag) return ProfileFailure("harness_profile_conflict");
        var valid = await ValidateAsync(workspaceId, definition, token);
        if (!valid.IsSuccess) return ProfileFailure(valid.ErrorCode!);
        var updated = existing.Value!.Value with { Definition = CanonicalDefinition(definition), Generation = existing.Value.Value.Generation + 1, Status = Succeeded() };
        try
        {
            return Result<StoredResource<HarnessProfileResource>>.Success(await scopes.WriteAsync(existing.Value.Value, Scope(workspaceId), AuthorizationPermissions.ResourcesWrite,
                cancellation => store.PutExactAsync(Scope(workspaceId), updated, expectedETag, false, cancellation), token));
        }
        catch (ResourceConcurrencyException) { return ProfileFailure("harness_profile_conflict"); }
        catch (ResourceGovernanceViolationException exception) { return ProfileFailure(exception.Code); }
    }
    public async Task<Result<StoredResource<HarnessProfileVersionResource>>> PublishAsync(WorkspaceId workspaceId, ResourceNamespace resourceNamespace, string name,
        PublishHarnessProfileRequest command, CancellationToken token)
    {
        if (!await AllowedAsync(workspaceId, true, token)) return VersionFailure("harness_profile_denied");
        if (!IsPublicationVersion(command.Version)) return VersionFailure("harness_profile_version_invalid");
        var draft = await GetAsync(workspaceId, resourceNamespace, name, token);
        if (!draft.IsSuccess) return VersionFailure(draft.ErrorCode!);
        var source = draft.Value!.Value;
        try { ResourceGovernanceEnforcement.EnsureManager(source.Governance, governance.Current); }
        catch (ResourceGovernanceViolationException exception)
        {
            await audit.WriteAsync(new(SecurityAuditActions.ManagedResourceMutationDenied, SecurityAuditOutcome.Failed,
                WorkspaceId: workspaceId.Value, ReasonCode: exception.Code), token);
            return VersionFailure(exception.Code);
        }
        var valid = await ValidateAsync(workspaceId, source.Definition, token);
        if (!valid.IsSuccess) return VersionFailure(valid.ErrorCode!);
        var native = valid.Value!.Value;
        var pin = new HarnessNativeConfigurationPin(Scope(workspaceId), native.Namespace, native.ConfigurationName, native.Version,
            native.Uid, native.Generation, native.ContentDigest, native.Definition);
        var digest = CodexHarnessMaterializer.ProfileDigest(source.Definition, pin);
        var address = Address(Scope(workspaceId), resourceNamespace, HarnessResourceKinds.ProfileVersion, VersionName(name, command.Version));
        var existing = await store.GetExactAsync<HarnessProfileVersionResource>(address, token);
        if (existing is not null) return existing.Value.ContentDigest == digest ? Result<StoredResource<HarnessProfileVersionResource>>.Success(existing)
            : VersionFailure("harness_profile_version_conflict");
        if (string.IsNullOrWhiteSpace(command.ExpectedETag) || command.ExpectedETag != draft.Value.ETag) return VersionFailure("harness_profile_conflict");
        using var activity = Activities.StartActivity("harness.publish");
        activity?.SetTag("workspace.id", workspaceId.ToString()); activity?.SetTag("resource.uid", source.Uid); activity?.SetTag("harness.version", command.Version);
        var published = new HarnessProfileVersionResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = HarnessResourceKinds.ProfileVersion,
            ScopeRef = Scope(workspaceId),
            Metadata = source.Metadata with { Name = address.Name },
            Governance = source.Governance,
            ProfileName = name,
            ProfileUid = source.Uid,
            ProfileGeneration = source.Generation,
            Version = command.Version,
            Definition = source.Definition,
            NativeConfiguration = pin,
            ContentDigest = digest,
            PublishedAt = clock.GetUtcNow(),
            Generation = 1,
            Status = Succeeded()
        };
        try
        {
            var saved = await scopes.WriteAsync(source, Scope(workspaceId), AuthorizationPermissions.ResourcesWrite,
                cancellation => store.CreateImmutableAsync(published, cancellation), token);
            await audit.WriteAsync(new("harness.profile.published", WorkspaceId: workspaceId.Value), token);
            return Result<StoredResource<HarnessProfileVersionResource>>.Success(saved);
        }
        catch (ResourceConcurrencyException) { return VersionFailure("harness_profile_version_conflict"); }
        catch (ResourceGovernanceViolationException exception) { return VersionFailure(exception.Code); }
    }
    public async Task<Result<StoredResource<HarnessProfileVersionResource>>> GetVersionAsync(WorkspaceId workspaceId, ResourceNamespace resourceNamespace,
        string name, string version, CancellationToken token)
    {
        if (!await AllowedAsync(workspaceId, false, token) || !Name(name) || !IsPublicationVersion(version)) return VersionFailure("harness_profile_version_not_found");
        var value = await store.GetExactAsync<HarnessProfileVersionResource>(Address(Scope(workspaceId), resourceNamespace,
            HarnessResourceKinds.ProfileVersion, VersionName(name, version)), token);
        return value is null ? VersionFailure("harness_profile_version_not_found") : Result<StoredResource<HarnessProfileVersionResource>>.Success(value);
    }
    public async Task<Result<HarnessExportPreview>> PreviewAsync(WorkspaceId workspaceId, HarnessExportRequest command, CancellationToken token)
    {
        var profile = await GetVersionAsync(workspaceId, command.Namespace, command.Name, command.Version, token);
        if (!profile.IsSuccess) return Result<HarnessExportPreview>.Failure(profile.ErrorCode!);
        if (profile.Value!.Value.ContentDigest != command.ExpectedDigest) return Result<HarnessExportPreview>.Failure("harness_profile_version_conflict");
        var version = profile.Value.Value;
        var native = await GetConfigurationAsync(workspaceId, version.NativeConfiguration.Namespace, version.NativeConfiguration.Name, version.NativeConfiguration.Version, token);
        if (!native.IsSuccess || native.Value!.Value.Uid != version.NativeConfiguration.Uid || native.Value.Value.Generation != version.NativeConfiguration.Generation
            || native.Value.Value.ContentDigest != version.NativeConfiguration.ContentDigest) return Result<HarnessExportPreview>.Failure("harness_configuration_unavailable");
        if (command.Bindings is null || command.Bindings.Count > 32) return Result<HarnessExportPreview>.Failure("harness_bindings_invalid");
        using var activity = Activities.StartActivity("harness.preview");
        activity?.SetTag("workspace.id", workspaceId.ToString()); activity?.SetTag("resource.uid", version.Uid); activity?.SetTag("harness.version", version.Version);
        ISchemaFormatHandler schema;
        try { schema = Handler(); }
        catch (SchemaFormatResolutionException exception) { return Result<HarnessExportPreview>.Failure(exception.Code); }
        var result = await schema.ValidateInstanceAsync(version.Definition.BindingsSchema, JsonSerializer.SerializeToElement(command.Bindings), BuiltinJsonSchemaFormatHandler.Draft202012, token);
        return result.IsValid ? materializer.Build(version, command) : Result<HarnessExportPreview>.Failure("harness_bindings_invalid");
    }
    public async Task<Result<CreateHarnessNativeConfigurationRequest>> ValidateConfigurationForCreateAsync(WorkspaceId workspaceId, CreateHarnessNativeConfigurationRequest command, CancellationToken token)
    {
        if (!await AllowedAsync(workspaceId, true, token)) return Result<CreateHarnessNativeConfigurationRequest>.Failure("harness_profile_denied");
        return Name(command.Name) && IsPublicationVersion(command.Version) && CodexHarnessMaterializer.ValidNative(command.Definition)
            ? Result<CreateHarnessNativeConfigurationRequest>.Success(command) : Result<CreateHarnessNativeConfigurationRequest>.Failure("harness_configuration_invalid");
    }
    public async Task<Result<HarnessProfileDefinition>> ValidateForCreateAsync(WorkspaceId workspaceId, ResourceNamespace resourceNamespace, string name, HarnessProfileDefinition definition,
        CreateHarnessNativeConfigurationRequest? plannedConfiguration, CancellationToken token)
    {
        if (!await AllowedAsync(workspaceId, true, token)) return Result<HarnessProfileDefinition>.Failure("harness_profile_denied");
        if (!Name(name)) return Result<HarnessProfileDefinition>.Failure("harness_profile_invalid");
        var draft = await GetAsync(workspaceId, resourceNamespace, name, token);
        if (draft.IsSuccess)
        {
            try { ResourceGovernanceEnforcement.EnsureManager(draft.Value!.Value.Governance, governance.Current); }
            catch (ResourceGovernanceViolationException exception) { return Result<HarnessProfileDefinition>.Failure(exception.Code); }
        }
        var error = await ValidateContentAsync(workspaceId, definition, token);
        if (error is not null) return Result<HarnessProfileDefinition>.Failure(error);
        var reference = definition.NativeConfiguration;
        var existing = await GetConfigurationAsync(workspaceId, reference.Namespace, reference.Name, reference.Version, token);
        if (existing.IsSuccess) return Result<HarnessProfileDefinition>.Success(definition);
        if (existing.ErrorCode == "harness_configuration_not_found" && plannedConfiguration is not null
            && plannedConfiguration.Name == reference.Name && plannedConfiguration.Namespace == reference.Namespace && plannedConfiguration.Version == reference.Version
            && (await ValidateConfigurationForCreateAsync(workspaceId, plannedConfiguration, token)).IsSuccess)
            return Result<HarnessProfileDefinition>.Success(definition);
        return Result<HarnessProfileDefinition>.Failure(existing.ErrorCode!);
    }
    private async Task<Result<StoredResource<HarnessNativeConfigurationResource>>> ValidateAsync(WorkspaceId workspaceId, HarnessProfileDefinition? definition, CancellationToken token)
    {
        var error = await ValidateContentAsync(workspaceId, definition, token);
        return error is not null ? ConfigurationFailure(error) : await GetConfigurationAsync(workspaceId, definition!.NativeConfiguration.Namespace,
            definition.NativeConfiguration.Name, definition.NativeConfiguration.Version, token);
    }
    private async Task<string?> ValidateContentAsync(WorkspaceId workspaceId, HarnessProfileDefinition? definition, CancellationToken token)
    {
        if (definition is null || definition.DisplayName is not { Length: > 0 and <= 200 } || string.IsNullOrWhiteSpace(definition.Instructions)
            || Encoding.UTF8.GetByteCount(definition.Instructions) > CodexHarnessMaterializer.MaximumInstructionsBytes
            || definition.Instructions.Any(character => char.IsControl(character) && character is not ('\r' or '\n' or '\t'))
            || !CodexHarnessMaterializer.SafePath(definition.InstructionsPath) || definition.InstructionsPath != "AGENTS.md"
            || definition.BindingsSchema.ValueKind != JsonValueKind.Object || definition.BindingsSchema.GetRawText().Length > 16 * 1024
            || !StringSchema(definition.BindingsSchema) || definition.NativeConfiguration is null
            || definition.NativeConfiguration.ScopeRef is { } supplied && supplied != Scope(workspaceId)) return "harness_profile_invalid";
        ISchemaFormatHandler handler;
        try { handler = Handler(); }
        catch (SchemaFormatResolutionException exception) { return exception.Code; }
        var schema = await handler.ValidateDefinitionAsync(definition.BindingsSchema, BuiltinJsonSchemaFormatHandler.Draft202012, token);
        if (!schema.IsValid) return "harness_schema_invalid";
        var properties = definition.BindingsSchema.GetProperty("properties");
        var required = definition.BindingsSchema.TryGetProperty("required", out var requiredValue) ? requiredValue.EnumerateArray().Select(value => value.GetString()).ToHashSet(StringComparer.Ordinal) : [];
        foreach (var name in CodexHarnessMaterializer.Placeholders(definition.Instructions))
            if (!properties.TryGetProperty(name, out _) || !required.Contains(name)) return "harness_binding_schema_missing";
        var scrubbed = TemplatePlaceholder().Replace(definition.Instructions, "");
        if (scrubbed.Contains("{{", StringComparison.Ordinal) || scrubbed.Contains("}}", StringComparison.Ordinal)) return "harness_template_invalid";
        return null;
    }
    private ISchemaFormatHandler Handler() => schemas.Resolve(SchemaFormatIds.JsonSchema, SchemaFormatCapabilities.ValidateDefinition | SchemaFormatCapabilities.ValidateInstance,
        new(BuiltinJsonSchemaFormatHandler.ProviderId, BuiltinJsonSchemaFormatHandler.ProviderVersion));
    private static bool StringSchema(JsonElement schema) => schema.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "object"
        && schema.TryGetProperty("additionalProperties", out var extra) && extra.ValueKind == JsonValueKind.False
        && schema.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object && properties.EnumerateObject().Count() <= 32
        && properties.EnumerateObject().All(property => WorkProjectDefinition.ValidName(property.Name) && !SecretName(property.Name)
            && property.Value.ValueKind == JsonValueKind.Object && property.Value.TryGetProperty("type", out var kind) && kind.ValueKind == JsonValueKind.String && kind.GetString() == "string"
            && property.Value.TryGetProperty("maxLength", out var maximum) && maximum.ValueKind == JsonValueKind.Number && maximum.TryGetInt32(out var bound) && bound is >= 1 and <= 8_000);
    private static bool SecretName(string name) => name.Replace("-", "", StringComparison.Ordinal).ToLowerInvariant() is "apikey" or "credential" or "secret" or "password" or "accesstoken" or "authorization";
    private async Task<bool> AllowedAsync(WorkspaceId workspaceId, bool write, CancellationToken token) => current.AccessMode == ControlPlaneAccessMode.Workspace
        && current.Current.WorkspaceId == workspaceId.Value && workspaceId.Value != Guid.Empty && current.Current.PrincipalId != Guid.Empty
        && await authorization.HasPermissionAsync(current.Current, write ? AuthorizationPermissions.ResourcesWrite : AuthorizationPermissions.ResourcesRead, token);
    public static HarnessProfileDefinition CanonicalDefinition(HarnessProfileDefinition value) => value with { NativeConfiguration = value.NativeConfiguration with { ScopeRef = null } };
    private static bool Name(string? value) => value is { Length: <= 64 } && WorkProjectDefinition.ValidName(value);
    public static bool IsPublicationVersion(string? value) => value is { Length: > 0 and <= 32 } && SemanticVersion().IsMatch(value);
    public static string VersionName(string name, string version) => name + ".v" + version;
    private static ResourceScopeRef Scope(WorkspaceId workspaceId) => ResourceScopeRef.Workspace(workspaceId.Value);
    private static ScopedResourceAddress Address(ResourceScopeRef scope, ResourceNamespace resourceNamespace, string kind, string name) => ScopedResourceAddress.Create(scope, resourceNamespace, kind, name);
    private static ResourceStatus Succeeded() => new() { ProvisioningState = ProvisioningState.Succeeded };
    private static Result<StoredResource<HarnessProfileResource>> ProfileFailure(string code) => Result<StoredResource<HarnessProfileResource>>.Failure(code);
    private static Result<StoredResource<HarnessProfileVersionResource>> VersionFailure(string code) => Result<StoredResource<HarnessProfileVersionResource>>.Failure(code);
    private static Result<StoredResource<HarnessNativeConfigurationResource>> ConfigurationFailure(string code) => Result<StoredResource<HarnessNativeConfigurationResource>>.Failure(code);
    [GeneratedRegex(@"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$", RegexOptions.CultureInvariant)] private static partial Regex SemanticVersion();
    [GeneratedRegex(@"\{\{([a-z][a-z0-9-]{0,63})\}\}", RegexOptions.CultureInvariant)] private static partial Regex TemplatePlaceholder();
}
