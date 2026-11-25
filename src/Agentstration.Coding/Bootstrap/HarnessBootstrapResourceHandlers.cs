using System.Text.Json;
using System.Text.Json.Serialization;
using Agentstration.Coding.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;

namespace Agentstration.Coding.Bootstrap;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DeclarativeHarnessNativeConfiguration(string Version, string Materializer, string MaterializerVersion, CodexHarnessSettings Settings);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DeclarativeHarnessProfile(string Version, HarnessProfileDefinition Profile);

internal static class HarnessBootstrap
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { MaxDepth = 16, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    internal static bool Allowed(BootstrapResourceDocument resource, BootstrapResourceOperationContext operation, ICurrentRequestContext current, string kind) =>
        resource.Kind == kind && resource.ApiVersion == ResourceApiVersions.CoreV1 && current.AccessMode == ControlPlaneAccessMode.Workspace
        && operation.ProfileScope == BootstrapProfileScope.Workspace && operation.Target is { } target && target.WorkspaceId == current.Current.WorkspaceId
        && (target.TenantId is null || target.TenantId == current.Current.TenantId)
        && (operation.ActorPrincipalId is null || operation.ActorPrincipalId == current.Current.PrincipalId);
    internal static BootstrapResourcePlanResult Invalid(string kind, string name, string code) => new(BootstrapResourceDisposition.Invalid,
        [new(kind, name, BootstrapResourceDisposition.Invalid, code)]);
}

public sealed class HarnessNativeConfigurationBootstrapHandler(HarnessProfileService profiles, ICurrentRequestContext current) : IBootstrapResourceHandler
{
    public string Kind => HarnessResourceKinds.NativeConfiguration;
    public BootstrapProfileScope Scope => BootstrapProfileScope.Workspace;
    public async Task<BootstrapResourcePlanResult> PlanAsync(BootstrapResourceDocument resource, BootstrapResourceOperationContext operation, BootstrapPlanningContext planning, CancellationToken token)
    {
        if (!HarnessBootstrap.Allowed(resource, operation, current, Kind)) return HarnessBootstrap.Invalid(Kind, resource.Metadata.Name, "harness_profile_denied");
        DeclarativeHarnessNativeConfiguration? value;
        try { value = resource.Definition.Deserialize<DeclarativeHarnessNativeConfiguration>(HarnessBootstrap.Json); }
        catch (JsonException) { return HarnessBootstrap.Invalid(Kind, resource.Metadata.Name, "harness_configuration_invalid"); }
        if (value is null) return HarnessBootstrap.Invalid(Kind, resource.Metadata.Name, "harness_configuration_invalid");
        var request = Request(resource, value);
        var valid = await profiles.ValidateConfigurationForCreateAsync(new(current.Current.WorkspaceId), request, token);
        if (!valid.IsSuccess) return HarnessBootstrap.Invalid(Kind, resource.Metadata.Name, valid.ErrorCode!);
        var existing = await profiles.GetConfigurationAsync(new(current.Current.WorkspaceId), request.Namespace, request.Name, request.Version, token);
        if (existing.IsSuccess && existing.Value!.Value.ContentDigest != CodexHarnessMaterializer.ContentDigest(request.Definition)) return new(BootstrapResourceDisposition.Conflict);
        planning.Register(Kind, HarnessProfileService.VersionName(request.Name, request.Version), resource.Metadata.Namespace.Value, resource);
        return new(existing.IsSuccess ? BootstrapResourceDisposition.Skip : BootstrapResourceDisposition.Create);
    }
    public async Task<BootstrapResourceApplyResult> ApplyAsync(BootstrapResourceDocument resource, BootstrapResourceOperationContext operation, CancellationToken token)
    {
        if (!HarnessBootstrap.Allowed(resource, operation, current, Kind)) return BootstrapResourceApplyResult.Conflict;
        DeclarativeHarnessNativeConfiguration? value;
        try { value = resource.Definition.Deserialize<DeclarativeHarnessNativeConfiguration>(HarnessBootstrap.Json); }
        catch (JsonException) { return BootstrapResourceApplyResult.Conflict; }
        if (value is null) return BootstrapResourceApplyResult.Conflict;
        var request = Request(resource, value);
        var prior = await profiles.GetConfigurationAsync(new(current.Current.WorkspaceId), request.Namespace, request.Name, request.Version, token);
        var created = await profiles.CreateConfigurationAsync(new(current.Current.WorkspaceId), request, token);
        return !created.IsSuccess ? BootstrapResourceApplyResult.Conflict : prior.IsSuccess ? BootstrapResourceApplyResult.Skipped : BootstrapResourceApplyResult.Created;
    }
    private static CreateHarnessNativeConfigurationRequest Request(BootstrapResourceDocument resource, DeclarativeHarnessNativeConfiguration value) =>
        new(resource.Metadata.Name, resource.Metadata.Namespace, value.Version, new(value.Materializer, value.MaterializerVersion, value.Settings));
}

public sealed class HarnessProfileBootstrapHandler(HarnessProfileService profiles, ICurrentRequestContext current) : IBootstrapResourceHandler
{
    public string Kind => HarnessResourceKinds.Profile;
    public BootstrapProfileScope Scope => BootstrapProfileScope.Workspace;
    public async Task<BootstrapResourcePlanResult> PlanAsync(BootstrapResourceDocument resource, BootstrapResourceOperationContext operation, BootstrapPlanningContext planning, CancellationToken token)
    {
        if (!HarnessBootstrap.Allowed(resource, operation, current, Kind)) return HarnessBootstrap.Invalid(Kind, resource.Metadata.Name, "harness_profile_denied");
        DeclarativeHarnessProfile? value;
        try { value = resource.Definition.Deserialize<DeclarativeHarnessProfile>(HarnessBootstrap.Json); }
        catch (JsonException) { return HarnessBootstrap.Invalid(Kind, resource.Metadata.Name, "harness_profile_invalid"); }
        if (value is null || !HarnessProfileService.IsPublicationVersion(value.Version) || value.Profile?.NativeConfiguration is not { } reference) return HarnessBootstrap.Invalid(Kind, resource.Metadata.Name, "harness_profile_invalid");
        var published = await profiles.GetVersionAsync(new(current.Current.WorkspaceId), resource.Metadata.Namespace, resource.Metadata.Name, value.Version, token);
        if (published.IsSuccess) return new(Same(published.Value!.Value.Definition, value.Profile) ? BootstrapResourceDisposition.Skip : BootstrapResourceDisposition.Conflict);
        CreateHarnessNativeConfigurationRequest? planned = null;
        if (planning.TryGetDocument(HarnessResourceKinds.NativeConfiguration, HarnessProfileService.VersionName(reference.Name, reference.Version), reference.Namespace.Value, out var document))
        {
            try
            {
                var native = document.Definition.Deserialize<DeclarativeHarnessNativeConfiguration>(HarnessBootstrap.Json);
                if (native is not null) planned = new(document.Metadata.Name, document.Metadata.Namespace, native.Version, new(native.Materializer, native.MaterializerVersion, native.Settings));
            }
            catch (JsonException) { return HarnessBootstrap.Invalid(Kind, resource.Metadata.Name, "harness_configuration_invalid"); }
        }
        var valid = await profiles.ValidateForCreateAsync(new(current.Current.WorkspaceId), resource.Metadata.Namespace, resource.Metadata.Name, value.Profile, planned, token);
        if (!valid.IsSuccess) return HarnessBootstrap.Invalid(Kind, resource.Metadata.Name, valid.ErrorCode!);
        var draft = await profiles.GetAsync(new(current.Current.WorkspaceId), resource.Metadata.Namespace, resource.Metadata.Name, token);
        if (draft.IsSuccess && !Same(draft.Value!.Value.Definition, value.Profile)) return new(BootstrapResourceDisposition.Conflict);
        planning.Register(Kind, HarnessProfileService.VersionName(resource.Metadata.Name, value.Version), resource.Metadata.Namespace.Value, resource);
        return new(BootstrapResourceDisposition.Create);
    }
    public async Task<BootstrapResourceApplyResult> ApplyAsync(BootstrapResourceDocument resource, BootstrapResourceOperationContext operation, CancellationToken token)
    {
        if (!HarnessBootstrap.Allowed(resource, operation, current, Kind)) return BootstrapResourceApplyResult.Conflict;
        DeclarativeHarnessProfile? value;
        try { value = resource.Definition.Deserialize<DeclarativeHarnessProfile>(HarnessBootstrap.Json); }
        catch (JsonException) { return BootstrapResourceApplyResult.Conflict; }
        if (value is null) return BootstrapResourceApplyResult.Conflict;
        var workspace = new WorkspaceId(current.Current.WorkspaceId);
        var published = await profiles.GetVersionAsync(workspace, resource.Metadata.Namespace, resource.Metadata.Name, value.Version, token);
        if (published.IsSuccess) return Same(published.Value!.Value.Definition, value.Profile) ? BootstrapResourceApplyResult.Skipped : BootstrapResourceApplyResult.Conflict;
        var draft = await profiles.GetAsync(workspace, resource.Metadata.Namespace, resource.Metadata.Name, token);
        if (draft.IsSuccess && !Same(draft.Value!.Value.Definition, value.Profile)) return BootstrapResourceApplyResult.Conflict;
        if (!draft.IsSuccess)
            draft = await profiles.CreateAsync(workspace, new() { ApiVersion = ResourceApiVersions.CoreV1, Kind = Kind, Metadata = resource.Metadata, Definition = value.Profile }, token);
        if (!draft.IsSuccess) return BootstrapResourceApplyResult.Conflict;
        return (await profiles.PublishAsync(workspace, resource.Metadata.Namespace, resource.Metadata.Name, new(value.Version, draft.Value!.ETag), token)).IsSuccess
            ? BootstrapResourceApplyResult.Created : BootstrapResourceApplyResult.Conflict;
    }
    private static bool Same(HarnessProfileDefinition first, HarnessProfileDefinition second) => CodexHarnessMaterializer.ContentDigest(HarnessProfileService.CanonicalDefinition(first)) == CodexHarnessMaterializer.ContentDigest(HarnessProfileService.CanonicalDefinition(second));
}
