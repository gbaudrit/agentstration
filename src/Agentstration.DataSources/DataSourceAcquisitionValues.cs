using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentstration.DataSources.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.Parameters;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Secrets.Abstractions;
using Agentstration.Tools;

namespace Agentstration.DataSources;

/// <summary>Prepares saved operation inputs without materializing Secrets across durable boundaries.</summary>
public sealed class DataSourceAcquisitionValues(IResourceStore store, IParameterResolver parameters,
    ISecretAccessAuthorizer secrets, IRequestContextScopeFactory requestScopes)
{
    public async Task<IReadOnlyList<DataSourceParameterBinding>> PinAsync(DataSourceResource source, CancellationToken token)
    {
        var result = new List<DataSourceParameterBinding>();
        foreach (var binding in Bindings(source))
        {
            var address = Address(binding);
            try
            {
                if (binding.Parameter is { } parameter)
                {
                    if (await parameters.ResolveAsync(parameter, new(source.ScopeRef!.Value, source.Address), token) is null) throw Invalid();
                }
                else if (binding.Secret is { } secret
                    && await secrets.GetAuthorizedStatusAsync(secret, new(source.ScopeRef!.Value, source.Address), token) != SecretValueStatus.Configured)
                    throw Invalid();
            }
            catch (Exception exception) when (exception is ParameterAccessDeniedException or SecretResolutionException
                or ResourceScopeAccessDeniedException or ResourceReferenceOutsideScopeException)
            { throw Invalid(); }
            var identity = await IdentityAsync(address, token);
            if (binding.ResourceUid is { } expected && expected != identity) throw Invalid();
            result.Add(binding with { ResourceUid = identity });
        }
        return result;
    }

    public async Task<(JsonElement Parameters, IReadOnlyList<SecretInputReference> Secrets)> PrepareAsync(
        DataSourceResource source, JsonElement schema, CancellationToken token, JsonElement? acceptedParameters = null)
    {
        var acquisition = source.Definition.Dataset!.Acquisition;
        var bindings = Bindings(source);
        if (bindings.Count == 0) return (acquisition.Parameters.Clone(), []);
        if (acquisition.Parameters.ValueKind != JsonValueKind.Object || schema.ValueKind != JsonValueKind.Object || !schema.TryGetProperty("properties", out var properties)
            || properties.ValueKind != JsonValueKind.Object) throw Invalid();
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in acquisition.Parameters.EnumerateObject())
            if (!values.TryAdd(property.Name, property.Value.Clone())) throw Invalid();
        var references = new List<SecretInputReference>();
        try
        {
            foreach (var binding in bindings)
            {
                if (values.ContainsKey(binding.Name) || !properties.TryGetProperty(binding.Name, out var input)) throw Invalid();
                var address = Address(binding);
                if (binding.ResourceUid is not { } uid || uid != await IdentityAsync(address, token)) throw Invalid();
                if (binding.Parameter is { } parameter)
                {
                    var resolved = await parameters.ResolveAsync(parameter, new(source.ScopeRef!.Value, source.Address), token)
                        ?? throw Invalid();
                    values.Add(binding.Name, acceptedParameters is { ValueKind: JsonValueKind.Object } accepted
                        && accepted.TryGetProperty(binding.Name, out var pinned) ? pinned.Clone() : resolved.Value.Clone());
                }
                else
                {
                    if (!input.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "string"
                        || await secrets.GetAuthorizedStatusAsync(binding.Secret!, new(source.ScopeRef!.Value, source.Address), token)
                            != SecretValueStatus.Configured) throw Invalid();
                    var id = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"datasource:{source.Uid:D}:{binding.Name}:{uid:D}")));
                    references.Add(new(id, binding.Secret!, uid, new(source.ScopeRef.Value, source.Address)));
                    values.Add(binding.Name, SecretInputs.Marker(id));
                }
            }
            if (SecretInputs.ContainsReference(acquisition.Parameters, references)) throw Invalid();
            var prepared = JsonSerializer.SerializeToElement(values);
            ToolInputSchemaValidator.Validate(schema, prepared, references);
            return (prepared, references);
        }
        catch (Exception exception) when (exception is ParameterAccessDeniedException or SecretResolutionException
            or ResourceScopeAccessDeniedException or ResourceReferenceOutsideScopeException or ToolInputValidationException)
        { throw Invalid(); }
    }

    private static IReadOnlyList<DataSourceParameterBinding> Bindings(DataSourceResource source)
    {
        var bindings = source.Definition.Dataset!.Acquisition.ParameterBindings;
        if (bindings.Count > 64 || bindings.Any(b => string.IsNullOrWhiteSpace(b.Name) || b.Name.Length > 128)
            || bindings.Select(b => b.Name).Distinct(StringComparer.Ordinal).Count() != bindings.Count) throw Invalid();
        return bindings;
    }

    private static ScopedResourceAddress Address(DataSourceParameterBinding binding)
    {
        if (binding.Parameter is { } parameter && binding.Secret is null && parameter.Address.Kind == ParameterResourceKinds.Parameter)
            return new(parameter.ScopeRef, parameter.Address.Namespace, parameter.Address.Kind, parameter.Address.Name);
        if (binding.Secret is { ScopeRef: { } scope } secret && binding.Parameter is null && secret.Address.Kind == "Secret")
            return new(scope, secret.Address.Namespace, secret.Address.Kind, secret.Address.Name);
        throw Invalid();
    }

    private async Task<Guid> IdentityAsync(ScopedResourceAddress address, CancellationToken token)
    {
        // Value authorization stays in the caller context; only identity metadata uses the owner inventory.
        using var system = requestScopes.PushSystem();
        for (var skip = 0; skip < 4096; skip += 128)
        {
            var page = await store.ListExactInventoryAsync(address.ScopeRef, skip, 128, token);
            var found = page.FirstOrDefault(r => r.Kind == address.Kind && r.Namespace == address.Namespace && r.Name == address.Name);
            if (found is not null) return found.Uid;
            if (page.Count < 128) break;
        }
        throw Invalid();
    }

    private static DataSourceValidationException Invalid() => new("data_source_parameter_binding_invalid",
        "An operation input binding is invalid, unavailable, replaced or unauthorized.");
}
