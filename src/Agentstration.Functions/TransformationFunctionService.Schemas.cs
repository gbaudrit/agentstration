using System.Text.Json;
using Agentstration.Functions.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Schemas;
using Agentstration.Schemas.Contracts;

namespace Agentstration.Functions;

public sealed partial class TransformationFunctionService
{
    private static JsonElement XmlInvocationSchema => FunctionInvocationSchemas.XmlContent;

    private static TransformationSchemaDeclaration ParseDeclaration(JsonElement value) => TransformationSchemaFormatHandler.Parse(value);

    private async Task<TransformationSchemaSnapshot> ResolveTransformationAsync(SchemaVersionReference reference,
        ResourceScopeRef owner, ResourceNamespace ns, ResourceScopeRef consumer, CancellationToken ct, ISchemaBindingResolver? resolver = null)
    {
        if (!string.IsNullOrEmpty(reference.SchemaPath)) throw Error("transformation_reference_invalid", "A whole published transformation Schema is required.");
        // Omitted references belong to the declaration's owner, never to a downstream consumer.
        var declaration = await Bind(reference with { ScopeRef = reference.ScopeRef ?? owner, Namespace = reference.Namespace ?? ns },
            TransformationSchemaFormatHandler.Format, owner, ns, ct, resolver);
        _ = await Bind(declaration.Source!, TransformationSchemaFormatHandler.Format, consumer, ns, ct, resolver);
        var parsed = ParseDeclaration(declaration.Content);
        var declarationOwner = declaration.Source!.ScopeRef!.Value;
        var declarationNamespace = declaration.Source.Namespace!.Value;
        var inputReference = parsed.Input with { ScopeRef = parsed.Input.ScopeRef ?? declarationOwner, Namespace = parsed.Input.Namespace ?? declarationNamespace };
        var outputReference = parsed.Output with { ScopeRef = parsed.Output.ScopeRef ?? declarationOwner, Namespace = parsed.Output.Namespace ?? declarationNamespace };
        var input = await Bind(inputReference, BuiltinXmlSchemaFormatHandler.Format, owner, ns, ct, resolver);
        var output = await Bind(outputReference, SchemaFormatIds.JsonSchema, owner, ns, ct, resolver);
        _ = await Bind(inputReference, BuiltinXmlSchemaFormatHandler.Format, consumer, ns, ct, resolver);
        _ = await Bind(outputReference, SchemaFormatIds.JsonSchema, consumer, ns, ct, resolver);
        var engineReference = parsed.Engine with { ScopeRef = parsed.Engine.ScopeRef ?? declarationOwner, Namespace = parsed.Engine.Namespace ?? declarationNamespace };
        var engineResource = (await GetAsync(engineReference.ScopeRef!.Value, engineReference.Namespace!.Value, engineReference.Name, ct))?.Value;
        var engineVersion = engineResource?.Definition.Versions.SingleOrDefault(v => v.Version == engineReference.Version);
        if (engineVersion is null || engineVersion.TransformationSchema is not null)
            throw Error("transformation_engine_invalid", "An exact native engine function is required; declaration chains are unsupported.");
        var engine = await ResolveAsync(engineReference, owner, ct);
        _ = await ResolveAsync(engine.Reference, consumer, ct);
        if (engine.ImplementationId != "xml.to_json" || engine.ImplementationVersion != "1.0.0")
            throw Error("transformation_engine_invalid", "The supported engine is xml.to_json@1.0.0.");
        return new(declaration, input, output, engine.Reference, FunctionDigests.Compute(engine));
    }

    private async Task<SchemaBindingSnapshot> Bind(SchemaVersionReference reference, string format,
        ResourceScopeRef consumer, ResourceNamespace ns, CancellationToken ct, ISchemaBindingResolver? resolver = null)
    {
        try { return await (resolver ?? schemaResolver).ResolveAsync("$.transformation", new() { Reference = reference }, new(consumer, ns, format), ct); }
        catch (SchemaBindingResolutionException error) { throw Error(error.Code, error.Message); }
    }

    public Task<TransformationSchemaSnapshot> ValidateExposureAsync(ExposeTransformationSchemaRequest request, CancellationToken ct, ISchemaBindingResolver? resolver = null)
    {
        if (request.Schema is null) throw Error("transformation_reference_invalid", "An exact transformation Schema reference is required.");
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 128 || !char.IsLetterOrDigit(request.Name[0])
            || request.Name.Any(c => !char.IsLower(c) && !char.IsDigit(c) && c is not ('-' or '.')))
            throw Error("function_definition_invalid", "A bounded lowercase function name is required.");
        Versions([request.Version]);
        ValidateResource(new TransformationFunctionResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = FunctionResourceKinds.TransformationFunction,
            ScopeRef = request.ScopeRef,
            Metadata = new() { Name = request.Name, Namespace = request.Namespace },
            Definition = new(request.DisplayName, [])
        }, FunctionResourceKinds.TransformationFunction, request.DisplayName);
        return ResolveTransformationAsync(request.Schema, request.ScopeRef, request.Namespace, request.ScopeRef, ct, resolver);
    }

    public async Task<StoredResource<TransformationFunctionResource>> ExposeAsync(ExposeTransformationSchemaRequest request, CancellationToken ct)
    {
        var mapping = await ValidateExposureAsync(request, ct);
        var engine = await ResolveAsync(mapping.Engine, request.ScopeRef, ct);
        var version = new TransformationFunctionVersion(request.Version, engine.FunctionKind,
            JsonSerializer.SerializeToElement(new { implementation = new { id = engine.ImplementationId, version = engine.ImplementationVersion } }))
        {
            TransformationSchema = mapping.Declaration.Source,
            TransformationSnapshot = mapping,
            NativeContractDigest = FunctionDigests.Compute(registry.Resolve(engine.ImplementationId, engine.ImplementationVersion).Descriptor)
        };
        var existing = await GetAsync(request.ScopeRef, request.Namespace, request.Name, ct);
        if (existing is not null && existing.Value.Definition.Versions.SingleOrDefault(v => v.Version == request.Version) is { } previous)
        {
            if (FunctionDigests.Compute(previous) != FunctionDigests.Compute(version)) throw Error("transformation_exposure_conflict", "This function version already exposes a different contract.");
            // A replay still requires current write permission, even if it does not mutate storage.
            return await scopes.WriteAsync(existing.Value, request.ScopeRef, Agentstration.Identity.Contracts.AuthorizationPermissions.ResourcesWrite,
                _ => Task.FromResult(existing), ct);
        }
        var desired = existing?.Value ?? new TransformationFunctionResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = FunctionResourceKinds.TransformationFunction,
            ScopeRef = request.ScopeRef,
            Metadata = new() { Name = request.Name, Namespace = request.Namespace },
            Definition = new(request.DisplayName, [])
        };
        try { return await PutAsync(desired with { Definition = desired.Definition with { Versions = [.. desired.Definition.Versions, version] } }, existing?.ETag, existing is null, ct); }
        catch (ResourceConcurrencyException)
        {
            // Concurrent equivalent exposure has one winner. A retry compares exact immutable evidence.
            var winner = await GetAsync(request.ScopeRef, request.Namespace, request.Name, ct);
            if (winner?.Value.Definition.Versions.SingleOrDefault(v => v.Version == request.Version) is { } won
                && FunctionDigests.Compute(won) == FunctionDigests.Compute(version)) return winner;
            throw;
        }
    }
}
