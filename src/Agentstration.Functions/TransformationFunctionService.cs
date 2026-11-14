using System.Text.Json;
using Agentstration.Functions.Abstractions;
using Agentstration.Functions.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.Schemas;

namespace Agentstration.Functions;

public sealed class TransformationFunctionService(IResourceStore store, IResourceScopeOperations scopes,
    IResourceAccessPolicyService access, NativeFunctionRegistry registry, NativeFunctionExecutionContext execution) : ITransformationFunctionResolver
{
    public const int MaximumValueBytes = 1024 * 1024;
    public static JsonElement NativeDefinitionSchema => NativeFunctionKindContract.DefinitionSchema;

    public Task<IReadOnlyList<StoredResource<TransformationFunctionResource>>> ListAsync(ResourceScopeRef scope, int skip, int take, CancellationToken ct) =>
        store.ListVisibleAsync<TransformationFunctionResource>(scope, FunctionResourceKinds.TransformationFunction, skip, take, ct);
    public Task<IReadOnlyList<StoredResource<FunctionKindResource>>> ListKindsAsync(ResourceScopeRef scope, int skip, int take, CancellationToken ct) =>
        store.ListVisibleAsync<FunctionKindResource>(scope, FunctionResourceKinds.FunctionKind, skip, take, ct);
    public Task<StoredResource<TransformationFunctionResource>?> GetAsync(ResourceScopeRef scope, ResourceNamespace ns, string name, CancellationToken ct) =>
        store.GetExactAsync<TransformationFunctionResource>(new(scope, ns, FunctionResourceKinds.TransformationFunction, name), ct);
    public Task<StoredResource<FunctionKindResource>?> GetKindAsync(ResourceScopeRef scope, ResourceNamespace ns, string name, CancellationToken ct) =>
        store.GetExactAsync<FunctionKindResource>(new(scope, ns, FunctionResourceKinds.FunctionKind, name), ct);

    public async Task ValidateKindAsync(FunctionKindResource value, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (value.Definition?.Versions is null || value.Definition.Versions.Any(version => version is null)) throw Error("function_definition_invalid", "A function kind definition and versions are required.");
        ValidateResource(value, FunctionResourceKinds.FunctionKind, value.Definition.DisplayName);
        Versions(value.Definition.Versions.Select(v => v.Version));
        foreach (var version in value.Definition.Versions)
            if (version.Executor != "native" || !JsonElement.DeepEquals(version.DefinitionSchema, NativeDefinitionSchema))
                throw Error("function_kind_unavailable", "Only the implemented native kind contract is supported.");
        await Task.CompletedTask;
    }

    public async Task ValidateAsync(TransformationFunctionResource value, CancellationToken ct, FunctionKindResource? plannedKind = null)
    {
        if (value.Definition?.Versions is null || value.Definition.Versions.Any(version => version is null)) throw Error("function_definition_invalid", "A function definition and versions are required.");
        ValidateResource(value, FunctionResourceKinds.TransformationFunction, value.Definition.DisplayName);
        Versions(value.Definition.Versions.Select(v => v.Version));
        foreach (var version in value.Definition.Versions) _ = await ResolveVersionAsync(value, version, value.ScopeRef!.Value, ct, plannedKind);
    }

    public async Task<StoredResource<FunctionKindResource>> PutKindAsync(FunctionKindResource value, string? etag, bool create, CancellationToken ct)
    {
        var scope = value.ScopeRef ?? scopes.DefaultScopeRef(value.Kind);
        value = value with { ScopeRef = scope };
        return await scopes.WriteAsync(value, scope, AuthorizationPermissions.ResourcesWrite, async token =>
        {
            await ValidateKindAsync(value, token);
            var existing = await GetKindAsync(scope, value.Namespace, value.Name, token);
            if (existing is not null) EnsureVersions(existing.Value.Definition.Versions, value.Definition.Versions, v => v.Version);
            return await store.PutExactAsync(scope, value with { Generation = existing is null ? 1 : checked(existing.Value.Generation + 1) }, etag, create, token);
        }, ct);
    }

    public async Task<StoredResource<TransformationFunctionResource>> PutAsync(TransformationFunctionResource value, string? etag, bool create, CancellationToken ct)
    {
        var scope = value.ScopeRef ?? scopes.DefaultScopeRef(value.Kind);
        value = value with { ScopeRef = scope };
        return await scopes.WriteAsync(value, scope, AuthorizationPermissions.ResourcesWrite, async token =>
        {
            await ValidateAsync(value, token);
            var versions = new List<TransformationFunctionVersion>();
            foreach (var version in value.Definition.Versions)
            {
                var resolved = await ResolveVersionAsync(value, version, scope, token);
                versions.Add(version with { FunctionKind = resolved.FunctionKind,
                    NativeContractDigest = FunctionDigests.Compute(registry.Resolve(resolved.ImplementationId, resolved.ImplementationVersion).Descriptor) });
            }
            value = value with { Definition = value.Definition with { Versions = versions } };
            var existing = await GetAsync(scope, value.Namespace, value.Name, token);
            if (existing is not null) EnsureVersions(existing.Value.Definition.Versions, value.Definition.Versions, v => v.Version);
            return await store.PutExactAsync(scope, value with { Generation = existing is null ? 1 : checked(existing.Value.Generation + 1) }, etag, create, token);
        }, ct);
    }

    public async Task<ResolvedTransformationFunction> ResolveAsync(FunctionReference reference, ResourceScopeRef consumer, CancellationToken ct)
    {
        var resource = (await GetAsync(reference.ScopeRef ?? consumer, reference.Namespace ?? ResourceNamespace.Default, reference.Name, ct))?.Value
            ?? throw Error("function_unavailable", "The exact function is missing or inaccessible.");
        if (!resource.Definition.Enabled) throw Error("function_disabled", "The function is disabled.");
        await EnsureUseAsync(resource, consumer, ct);
        var version = resource.Definition.Versions.SingleOrDefault(v => v.Version == reference.Version)
            ?? throw Error("function_version_unavailable", "The exact function version is unavailable.");
        CheckReference(reference, resource, version);
        return await ResolveVersionAsync(resource, version, consumer, ct);
    }

    private async Task<ResolvedTransformationFunction> ResolveVersionAsync(TransformationFunctionResource resource, TransformationFunctionVersion version, ResourceScopeRef consumer, CancellationToken ct, FunctionKindResource? plannedKind = null)
    {
        var reference = version.FunctionKind;
        var kind = plannedKind ?? (await GetKindAsync(reference.ScopeRef ?? resource.ScopeRef!.Value, reference.Namespace ?? resource.Namespace, reference.Name, ct))?.Value
            ?? throw Error("function_kind_unavailable", "The selected function kind is missing or inaccessible.");
        if (!kind.Definition.Enabled) throw Error("function_kind_disabled", "The function kind is disabled.");
        if (plannedKind is null) await EnsureUseAsync(kind, consumer, ct);
        else
        {
            await ValidateKindAsync(kind, ct);
            if (kind.ScopeRef != resource.ScopeRef || kind.ScopeRef != consumer || kind.Name != reference.Name
                || kind.Namespace != (reference.Namespace ?? resource.Namespace) || reference.ScopeRef is { } requestedScope && requestedScope != kind.ScopeRef)
                throw Error("function_use_denied", "A planned kind must belong to the function's exact owner scope.");
        }
        var kindVersion = kind.Definition.Versions.SingleOrDefault(v => v.Version == reference.Version)
            ?? throw Error("function_kind_unavailable", "The exact function kind version is unavailable.");
        CheckReference(reference, kind, kindVersion);
        if (kindVersion.Executor != "native") throw Error("function_kind_unavailable", "The selected execution kind is not implemented.");
        ValidateValue(kindVersion.DefinitionSchema, version.Configuration, "function-definition");
        var implementation = version.Configuration.GetProperty("implementation");
        var handler = registry.Resolve(implementation.GetProperty("id").GetString()!, implementation.GetProperty("version").GetString()!);
        if (version.NativeContractDigest is { } nativeDigest && nativeDigest != FunctionDigests.Compute(handler.Descriptor))
            throw Error("function_implementation_changed", "The exact native implementation contract has changed.");
        return new(new(resource.Name, version.Version, resource.ScopeRef!.Value, resource.Namespace, resource.Uid, FunctionDigests.Compute(version)),
            new(kind.Name, kindVersion.Version, kind.ScopeRef!.Value, kind.Namespace, kind.Uid, FunctionDigests.Compute(kindVersion)),
            handler.Descriptor.Id, handler.Descriptor.Version, handler.Descriptor.InputSchema.Clone(), handler.Descriptor.OutputSchema.Clone());
    }

    public async Task<JsonElement> InvokeAsync(ResolvedTransformationFunction accepted, ResourceScopeRef consumer, JsonElement input, CancellationToken ct, string? invocationId = null)
    {
        using var invocation = execution.Enter(invocationId ?? Guid.NewGuid().ToString("N"));
        var resolved = await ResolveAsync(accepted.Reference, consumer, ct);
        if (FunctionDigests.Compute(resolved) != FunctionDigests.Compute(accepted)) throw Error("function_contract_changed", "The accepted function contract has changed.");
        ValidateValue(resolved.InputSchema, input, "function-input");
        var output = await registry.Resolve(resolved.ImplementationId, resolved.ImplementationVersion).ExecuteAsync(input, ct);
        ValidateValue(resolved.OutputSchema, output, "function-output");
        return output.Clone();
    }

    public static void ValidateValue(JsonElement schema, JsonElement value, string boundary)
    {
        if (value.ValueKind == JsonValueKind.Undefined || JsonSerializer.SerializeToUtf8Bytes(value).Length > MaximumValueBytes)
            throw Error("function_value_too_large", "The function value exceeds the bounded JSON invocation limit.");
        DataContracts.Validate(DataContracts.Declared(schema, boundary), value, boundary);
    }

    private async Task EnsureUseAsync(Resource resource, ResourceScopeRef consumer, CancellationToken ct)
    {
        var decision = await access.EvaluateAsync(new(resource.Uid, resource.ScopeRef!.Value, resource.Namespace, resource.Kind, resource.Generation), consumer, ResourceAccessAction.Use, ct);
        if (!decision.Allowed) throw Error("function_use_denied", "Use of the selected function or kind is not authorized.");
    }

    private static void CheckReference<T>(FunctionReference reference, Resource resource, T version)
    {
        if (reference.Uid is { } uid && uid != resource.Uid || reference.Digest is { } digest && digest != FunctionDigests.Compute(version))
            throw Error("function_reference_stale", "The exact function identity or version contract has changed.");
    }

    private static void ValidateResource(Resource value, string kind, string display)
    {
        if (value.Kind != kind || value.ApiVersion != ResourceApiVersions.CoreV1 || string.IsNullOrWhiteSpace(display) || display.Length > 200)
            throw Error("function_definition_invalid", "The function resource envelope or display name is invalid.");
        ResourceScopePolicy.EnsureAllowed(value, value.ScopeRef ?? throw Error("function_scope_required", "An exact owner scope is required."));
    }

    private static void Versions(IEnumerable<string> versions)
    {
        var values = versions.ToArray();
        if (values.Length is < 1 or > 64 || values.Any(v => string.IsNullOrWhiteSpace(v) || v.Length > 64) || values.Distinct(StringComparer.Ordinal).Count() != values.Length)
            throw Error("function_versions_invalid", "Function versions must have unique bounded identities.");
    }

    private static void EnsureVersions<T>(IReadOnlyList<T> previous, IReadOnlyList<T> next, Func<T, string> identity)
    {
        foreach (var version in previous)
            if (!next.Any(candidate => identity(candidate) == identity(version) && FunctionDigests.Compute(candidate) == FunctionDigests.Compute(version)))
                throw Error("function_version_immutable", "Published function versions cannot be removed or rewritten.");
    }
    private static TransformationFunctionException Error(string code, string message) => new(code, message);
}
