using System.Text.Json;
using Agentstration.Application.Work;
using Agentstration.Coding.Contracts;
using Agentstration.Flows;
using Agentstration.Flows.Application;
using Agentstration.Resources;
using Agentstration.Work;

namespace Agentstration.Coding;

public interface ICodingRuntimeProfileResolver
{
    Task<Result<CodingRuntimeProfileReference>> ResolveAsync(WorkspaceId workspaceId, CodingRuntimeProfileReference reference, CancellationToken token);
}

public sealed class CodingFlowConfigurationService(HarnessProfileService profiles, ICodingRuntimeProfileResolver runtimes,
    IWorkplaceContext context) : IFlowRuntimeDefinitionResolver
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public string RuntimeCapability => CodingExecutionProtocol.RuntimeCapability;

    public async Task<RuntimeFlowDefinition> PinAsync(ResourceScopeRef ownerScopeRef, ResourceNamespace ownerNamespace,
        RuntimeFlowDefinition definition, CancellationToken cancellationToken)
    {
        if (ownerScopeRef != ResourceScopeRef.Workspace(context.WorkspaceId.Value)) throw Invalid("coding_flow_workspace_required");
        var result = await ResolveAsync(context.WorkspaceId, definition, false, cancellationToken);
        if (!result.IsSuccess) throw Invalid(result.ErrorCode!);
        return definition with { Configuration = JsonSerializer.SerializeToElement(result.Value!.Configuration, Json) };
    }

    public async Task<Result<CodingResolvedConfiguration>> ResolveAsync(WorkspaceId workspaceId, RuntimeFlowDefinition definition,
        bool requirePins, CancellationToken token)
    {
        if (workspaceId.Value == Guid.Empty || context.WorkspaceId != workspaceId || context.PrincipalId == Guid.Empty)
            return Failure("coding_flow_denied");
        if (definition.RuntimeCapability != RuntimeCapability || definition.CapabilityVersion != CodingExecutionProtocol.CapabilityVersion
            || definition.MaterialVersion != CodingExecutionProtocol.MaterialVersion) return Failure("coding_material_version_unsupported");
        CodingExecutionConfiguration? configuration;
        try { configuration = definition.Configuration.Deserialize<CodingExecutionConfiguration>(Json); }
        catch (JsonException) { return Failure("coding_flow_configuration_invalid"); }
        if (!CodingExecutionValidation.Configuration(configuration, requirePins)) return Failure("coding_flow_configuration_invalid");
        var native = configuration!.Harness;
        var harness = await profiles.GetVersionAsync(workspaceId, native.Namespace, native.Name, native.Version, token);
        if (!harness.IsSuccess) return Failure(harness.ErrorCode!);
        var version = harness.Value!.Value;
        if (version.ContentDigest != native.Digest || native.Uid is { } uid && uid != version.Uid
            || native.Generation is { } generation && generation != version.Generation) return Failure("coding_harness_pin_changed");
        var runtime = await runtimes.ResolveAsync(workspaceId, configuration.RuntimeProfile, token);
        if (!runtime.IsSuccess) return Failure(runtime.ErrorCode!);
        return Result<CodingResolvedConfiguration>.Success(new(configuration with
        { RuntimeProfile = runtime.Value!, Harness = native with { Uid = version.Uid, Generation = version.Generation } }, version));
    }

    public async Task<Result<CodingTaskInput>> ValidateInputAsync(WorkspaceId workspaceId, CodingResolvedConfiguration resolved,
        JsonElement input, CancellationToken token)
    {
        CodingTaskInput? command;
        try { command = input.Deserialize<CodingTaskInput>(Json); }
        catch (JsonException) { return Result<CodingTaskInput>.Failure("coding_input_invalid"); }
        if (!CodingExecutionValidation.Input(command)) return Result<CodingTaskInput>.Failure("coding_input_invalid");
        var profile = resolved.Harness;
        var preview = await profiles.PreviewAsync(workspaceId, new(profile.Namespace, profile.ProfileName, profile.Version, profile.ContentDigest, command!.Bindings, []), token);
        return preview.IsSuccess ? Result<CodingTaskInput>.Success(command) : Result<CodingTaskInput>.Failure(preview.ErrorCode!);
    }

    private static FlowValidationException Invalid(string code) => new(code, "The pinned Coding runtime configuration is unavailable or invalid.");
    private static Result<CodingResolvedConfiguration> Failure(string code) => Result<CodingResolvedConfiguration>.Failure(code);
}

public sealed record CodingResolvedConfiguration(CodingExecutionConfiguration Configuration, HarnessProfileVersionResource Harness);
