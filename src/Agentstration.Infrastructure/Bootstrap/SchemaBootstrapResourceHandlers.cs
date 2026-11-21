using System.Text.Json;
using Agentstration.Functions;
using Agentstration.Functions.Contracts;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.Schemas;
using Agentstration.Schemas.Contracts;

namespace Agentstration.Infrastructure.Bootstrap;

/// <summary>publishVersion is a Bootstrap instruction, not part of the persisted Schema definition.</summary>
public sealed class SchemaBootstrapResourceHandler(SchemaManagementService schemas) : IBootstrapResourceHandler
{
    public string Kind => SchemaResourceKinds.Schema;
    public BootstrapProfileScope Scope => BootstrapProfileScope.Instance;
    public bool SupportsProfileScope(BootstrapProfileScope scope) => true;
    internal static SchemaResource Parse(BootstrapResourceDocument document, BootstrapResourceOperationContext operation)
    {
        var resource = WorkspaceBootstrapResource.Parse<SchemaResource>(document);
        return resource with { ScopeRef = DataSourceProfileBootstrapResourceHandler.TargetScope(operation) };
    }
    internal static string? Publication(BootstrapResourceDocument document) => document.Definition.TryGetProperty("publishVersion", out var value) ? value.GetString() : null;

    public async Task<BootstrapResourcePlanResult> PlanAsync(BootstrapResourceDocument document, BootstrapResourceOperationContext operation, BootstrapPlanningContext planning, CancellationToken ct)
    {
        var desired = Parse(document, operation);
        await schemas.ValidateAsync(desired, ct);
        var existing = await schemas.GetExactAsync(desired.ScopeRef!.Value, desired.Namespace, desired.Name, ct);
        if (existing is not null && FunctionDigests.Compute(existing.Value.Definition) != FunctionDigests.Compute(desired.Definition)) return new(BootstrapResourceDisposition.Conflict);
        if (Publication(document) is { } version)
        {
            if (string.IsNullOrWhiteSpace(version) || version.Length > 64 || version.Any(c => !char.IsLetterOrDigit(c) && c is not ('.' or '-')))
                throw new SchemaValidationException("schema_version_invalid", "An exact bounded publication version is required.");
            if (await schemas.GetVersionAsync(desired.ScopeRef.Value, desired.Namespace, desired.Name, version, ct) is { } published
                && published.Value.DefinitionHash != JsonSchemaSelection.DefinitionHash(desired.Definition.Format, desired.Definition.Dialect, desired.Definition.Content, published: true))
                return new(BootstrapResourceDisposition.Conflict);
            planning.Register(Kind, desired.Name, WorkspaceBootstrapResource.PlanningParent(desired.Namespace), document);
            if (existing is not null && await schemas.GetVersionAsync(desired.ScopeRef.Value, desired.Namespace, desired.Name, version, ct) is not null) return new(BootstrapResourceDisposition.Skip);
        }
        else if (existing is not null) return new(BootstrapResourceDisposition.Skip);
        return WorkspaceBootstrapResource.Created(planning, Kind, desired.Name, desired.Namespace, document);
    }

    public async Task<BootstrapResourceApplyResult> ApplyAsync(BootstrapResourceDocument document, BootstrapResourceOperationContext operation, CancellationToken ct)
    {
        var plan = await PlanAsync(document, operation, new(), ct);
        if (plan.Disposition == BootstrapResourceDisposition.Conflict) return BootstrapResourceApplyResult.Conflict;
        if (plan.Disposition == BootstrapResourceDisposition.Skip) return BootstrapResourceApplyResult.Skipped;
        var desired = Parse(document, operation);
        if (await schemas.GetExactAsync(desired.ScopeRef!.Value, desired.Namespace, desired.Name, ct) is null) await schemas.CreateAsync(desired, ct);
        if (Publication(document) is { } version) await schemas.PublishAsync(desired.ScopeRef.Value, desired.Namespace, desired.Name, version, ct);
        return BootstrapResourceApplyResult.Created;
    }
}

public sealed class TransformationSchemaExposureBootstrapResourceHandler(
    TransformationFunctionService functions, ISchemaBindingResolver bindings) : IBootstrapResourceHandler
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public const string ResourceKind = "TransformationFunctionExposure";
    public string Kind => ResourceKind;
    public BootstrapProfileScope Scope => BootstrapProfileScope.Instance;
    public bool SupportsProfileScope(BootstrapProfileScope scope) => true;
    private static ExposeTransformationSchemaRequest Parse(BootstrapResourceDocument document, BootstrapResourceOperationContext operation)
    {
        var value = document.Definition.Deserialize<Exposure>(Json)
            ?? throw new ArgumentException("An exposure definition is required.");
        return new(document.Metadata.Name, value.DisplayName, value.Version, value.Schema,
            DataSourceProfileBootstrapResourceHandler.TargetScope(operation), document.Metadata.Namespace);
    }

    public async Task<BootstrapResourcePlanResult> PlanAsync(BootstrapResourceDocument document, BootstrapResourceOperationContext operation, BootstrapPlanningContext planning, CancellationToken ct)
    {
        var request = Parse(document, operation);
        var mapping = await functions.ValidateExposureAsync(request, ct, new PlannedBindings(bindings, planning, operation));
        var existing = await functions.GetAsync(request.ScopeRef, request.Namespace, request.Name, ct);
        if (existing?.Value.Definition.Versions.SingleOrDefault(v => v.Version == request.Version) is { } version)
            return new(version.TransformationSnapshot is { } accepted
                && FunctionDigests.Compute(accepted) == FunctionDigests.Compute(mapping) ? BootstrapResourceDisposition.Skip : BootstrapResourceDisposition.Conflict);
        return WorkspaceBootstrapResource.Created(planning, Kind, request.Name, request.Namespace, document);
    }

    public async Task<BootstrapResourceApplyResult> ApplyAsync(BootstrapResourceDocument document, BootstrapResourceOperationContext operation, CancellationToken ct)
    {
        var request = Parse(document, operation);
        var previous = await functions.GetAsync(request.ScopeRef, request.Namespace, request.Name, ct);
        await functions.ExposeAsync(request, ct);
        return previous?.Value.Definition.Versions.Any(v => v.Version == request.Version) == true ? BootstrapResourceApplyResult.Skipped : BootstrapResourceApplyResult.Created;
    }

    private sealed record Exposure(string DisplayName, string Version, SchemaVersionReference Schema);

    private sealed class PlannedBindings(ISchemaBindingResolver current, BootstrapPlanningContext planning,
        BootstrapResourceOperationContext operation) : ISchemaBindingResolver
    {
        public async ValueTask<SchemaBindingSnapshot> ResolveAsync(string path, SchemaBinding binding, SchemaBindingResolutionContext context, CancellationToken ct)
        {
            try { return await current.ResolveAsync(path, binding, context, ct); }
            catch (SchemaBindingResolutionException error) when (error.Code is "schema_reference_not_found" or "schema_version_not_found") { }
            var reference = binding.Reference!;
            if ((reference.ScopeRef ?? context.ConsumerScopeRef) == DataSourceProfileBootstrapResourceHandler.TargetScope(operation)
                && planning.TryGetDocument(SchemaResourceKinds.Schema, reference.Name,
                    WorkspaceBootstrapResource.PlanningParent(reference.Namespace ?? context.OwnerNamespace), out var document)
                && SchemaBootstrapResourceHandler.Publication(document) == reference.Version)
            {
                var value = SchemaBootstrapResourceHandler.Parse(document, operation);
                if (value.Definition.Format != context.RequiredFormat) throw new SchemaBindingResolutionException("schema_format_incompatible", "The planned Schema format is incompatible.");
                if (!string.IsNullOrEmpty(reference.SchemaPath)) throw new SchemaBindingResolutionException("schema_selection_format_unsupported", "Bootstrap mapping contracts select whole schemas.");
                return new(path, value.Definition.Format, value.Definition.Dialect, value.Definition.Content,
                    JsonSchemaSelection.DefinitionHash(value.Definition.Format, value.Definition.Dialect, value.Definition.Content, published: true),
                    reference with { ScopeRef = value.ScopeRef, Namespace = value.Namespace });
            }
            return await current.ResolveAsync(path, binding, context, ct);
        }
    }
}
