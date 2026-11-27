using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agentstration.Application.Work;
using Agentstration.Bootstrap.Contracts;
using Agentstration.ConnectedServices;
using Agentstration.ConnectedServices.Contracts;
using Agentstration.Parameters;
using Agentstration.ResourceManagement;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.Secrets;
using Agentstration.Secrets.Abstractions;
using Agentstration.Storage;
using Agentstration.Storage.Contracts;
using Agentstration.Tools;
using Microsoft.Extensions.Configuration;

namespace Agentstration.Infrastructure.Bootstrap;

/// <summary>Resolves only the typed Bootstrap initializer/reference fields; native resources never retain selectors.</summary>
public sealed partial class BootstrapResourcePreparation(
    IConfiguration configuration,
    ConnectedServiceManagementService services,
    ConnectedServiceTypeManagementService serviceTypes,
    ConnectedServiceOperationDiscoveryService discoveries,
    ConnectedServiceOperationService operations,
    StorageManagementService storages,
    StorageTypeManagementService storageTypes,
    SecretManagementService secrets,
    IParameterResolver parameters,
    IResourceStore resources,
    WorkProjectContextService projectContexts)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string ParametersRoot = "Agentstration:Bootstrap:Parameters:";
    private const string SecretsRoot = "Agentstration:Bootstrap:Secrets:";

    public async Task<(BootstrapResourceDocument Document, bool Deferred)> PrepareAsync(
        BootstrapResourceDocument resource, BootstrapResourceOperationContext operation,
        BootstrapPlanningContext planning, bool applying, List<BootstrapConfigurationInput> inputs,
        List<string> fingerprints, CancellationToken cancellationToken)
    {
        var root = JsonNode.Parse(resource.Definition.GetRawText())!.AsObject();
        var scope = DataSourceProfileBootstrapResourceHandler.TargetScope(operation);
        if (resource.Kind is "Secret" or "Vault" && resource.Metadata.Namespace != ResourceNamespace.Default)
            throw new InvalidOperationException("bootstrap_secret_namespace_unsupported");
        var deferred = false;
        var deferredInputs = new HashSet<string>(StringComparer.Ordinal);
        var plannedSecrets = new HashSet<ScopedResourceAddress>();
        IReadOnlyList<ConnectedServiceExportDescriptor>? selectedExports = null;
        if (resource.Kind == "Secret" && root["valueFrom"] is JsonObject initializer)
        {
            var existing = await secrets.GetSecretExactAsync(scope, resource.Metadata.Name, cancellationToken);
            var status = existing is null ? SecretValueStatus.Missing
                : (await secrets.GetSecretViewExactAsync(scope, resource.Metadata.Name, cancellationToken)).ValueStatus;
            if (status is not (SecretValueStatus.Configured or SecretValueStatus.Missing)
                && !operation.Inputs.Any(input => input.Profile == operation.ProfileName && input.Configuration == Text(initializer, "configuration")))
                throw new InvalidOperationException("bootstrap_secret_unavailable");
            var configured = status == SecretValueStatus.Configured;
            _ = await InputAsync(initializer, secured: true, configured, null);
        }
        else if (resource.Kind is "Parameter" or "DataSource" or "ProjectSharing")
            await InitializeAsync(root);

        if (resource.Kind is "ConnectedService" or "Storage")
        {
            var kind = resource.Kind == "Storage" ? "StorageType" : "ConnectedServiceType";
            if (Selector(root["type"]) is { } selector)
            {
                var address = Address(selector, kind);
                if (kind == "StorageType")
                {
                    var type = (await storageTypes.GetExactAsync(address.ScopeRef, address.Namespace, address.Name, cancellationToken))?.Value
                        ?? throw Missing(kind, address.Name);
                    var version = type.Definition.Versions.SingleOrDefault(item => item.Version == Text(selector, "version"))
                        ?? throw Missing(kind, address.Name);
                    root["type"] = Node(new StorageTypeReference
                    {
                        Type = new(type.Name, type.ScopeRef, type.Namespace),
                        TypeUid = type.Uid,
                        Version = version.Version,
                        SchemaDigest = version.SchemaDigest,
                        ContractDigest = version.ContractDigest
                    });
                }
                else
                {
                    var type = (await serviceTypes.GetExactAsync(address.ScopeRef, address.Namespace, address.Name, cancellationToken))?.Value
                        ?? throw Missing(kind, address.Name);
                    var version = type.Definition.Versions.SingleOrDefault(item => item.Version == Text(selector, "version"))
                        ?? throw Missing(kind, address.Name);
                    root["type"] = Node(new ConnectedServiceTypeReference
                    {
                        Type = new(type.Name, type.ScopeRef, type.Namespace),
                        TypeUid = type.Uid,
                        Version = version.Version,
                        SchemaDigest = version.SchemaDigest
                    });
                }
                fingerprints.Add(root["type"]!.ToJsonString());
            }
        }
        if (resource.Kind is "ConnectedService" or "ToolDefinition" && root["bindings"] is JsonArray bindings)
            foreach (var binding in bindings.OfType<JsonObject>())
                if (Selector(binding["secret"]) is { } secretSelector)
                {
                    var address = Address(secretSelector, "Secret");
                    if (planning.TryGetDocument("Secret", address.Name, address.Namespace.Value, out var declared)
                        && declared.Definition.TryGetProperty("valueFrom", out var from)
                        && from.TryGetProperty("configuration", out var key)
                        && operation.Inputs.SingleOrDefault(input => input.Profile == operation.ProfileName && input.Configuration == key.GetString()) is { Resource: { } chosen })
                        address = chosen;
                    if (await secrets.GetSecretExactAsync(address.ScopeRef, address.Name, cancellationToken) is null)
                    {
                        Defer("Secret", address);
                        plannedSecrets.Add(address);
                    }
                    binding["secret"] = Node(new SecretReference(address.Address, address.ScopeRef));
                }
        if (resource.Kind == "ConnectedService")
        {
            var initializedInputs = (root["bindings"] as JsonArray ?? []).OfType<JsonObject>()
                .Where(binding => binding["literal"] is JsonObject literal && literal["valueFrom"] is JsonObject)
                .Select(binding => Text(binding, "inputId")).ToHashSet(StringComparer.Ordinal);
            if (initializedInputs.Count > 0)
            {
                // Check native protection before reading ordinary configuration or fingerprinting it.
                // A wrongly authored secured input must never enter the ordinary initializer path.
                var uninitialized = resource with { Definition = JsonSerializer.SerializeToElement(root, Json) };
                await services.ValidateForCreateAsync(WorkspaceBootstrapResource.Parse<ConnectedServiceResource>(uninitialized) with { ScopeRef = scope },
                    cancellationToken, initializedInputs, plannedSecrets);
            }
            await InitializeAsync(root);
        }
        if (resource.Kind == "Storage" && Selector(root["connectedService"]) is { } serviceSelector)
        {
            var address = Address(serviceSelector, "ConnectedService");
            var service = (await services.GetExactAsync(address.ScopeRef, address.Namespace, address.Name, cancellationToken))?.Value;
            if (service is null)
            {
                Defer("ConnectedService", address);
                if (planning.TryGetDocument("ConnectedService", address.Name, address.Namespace.Value, out var declaration))
                {
                    var (configured, _) = await PrepareAsync(declaration, operation, planning, false, [], [], cancellationToken);
                    var typeReference = configured.Definition.GetProperty("type").Deserialize<ConnectedServiceTypeReference>(Json)!;
                    var type = (await serviceTypes.GetExactAsync(typeReference.Type.ScopeRef!.Value,
                        typeReference.Type.Namespace ?? address.Namespace, typeReference.Type.Name, cancellationToken))!.Value;
                    selectedExports = type.Definition.Versions.Single(version => version.Version == typeReference.Version).Exports;
                    ResolveExports(selectedExports);
                }
            }
            else
            {
                root["connectedService"] = Node(new ConnectedServiceRevisionReference
                {
                    Service = new(service.Address, address.ScopeRef),
                    ServiceUid = service.Uid,
                    Generation = service.Generation
                });
                root["connectedServiceType"] = Node(service.Definition.Type);
                var exports = await services.InspectAsync(new()
                {
                    Service = new(service.Address, address.ScopeRef),
                    ServiceUid = service.Uid,
                    Generation = service.Generation
                }, new(scope, new(resource.Metadata.Namespace, resource.Kind, resource.Metadata.Name)), cancellationToken);
                selectedExports = exports.Exports;
                ResolveExports(selectedExports);
                fingerprints.Add(root["connectedService"]!.ToJsonString());
            }
        }
        if (resource.Kind is "DataSource" or "KnowledgeSource")
        {
            var output = root[resource.Kind == "DataSource" ? "dataset" : "output"] as JsonObject;
            if (output is not null && Selector(output["storage"]) is { } storageSelector)
            {
                var address = Address(storageSelector, "Storage");
                var storage = (await storages.GetExactAsync(address.ScopeRef, address.Namespace, address.Name, cancellationToken))?.Value;
                if (storage is null)
                {
                    Defer("Storage", address);
                    if (output["documentCollection"] is JsonValue name
                        && planning.TryGetDocument("Storage", address.Name, address.Namespace.Value, out var plannedStorage)
                        && (!plannedStorage.Definition.TryGetProperty("collections", out var collections)
                            || collections.EnumerateArray().Count(item => item.GetProperty("name").GetString() == name.GetValue<string>()) != 1))
                        throw new InvalidOperationException("bootstrap_collection_not_unique");
                }
                else
                {
                    output["storage"] = Node(new StorageRevisionReference(new(address.ScopeRef, storage.Namespace, storage.Kind, storage.Name), storage.Uid,
                        storage.Generation, StorageDigests.Binding(storage.Definition)));
                    if (output["documentCollection"] is JsonValue collection)
                    {
                        var matches = storage.Definition.Collections.Where(item => item.Name == collection.GetValue<string>()).ToArray();
                        if (matches.Length != 1) throw new InvalidOperationException("bootstrap_collection_not_unique");
                        output.Remove("documentCollection");
                        output["documentCollectionUid"] = JsonValue.Create(matches[0].Uid);
                    }
                    fingerprints.Add(output["storage"]!.ToJsonString());
                }
            }
            if (resource.Kind == "DataSource" && output?["acquisition"] is JsonObject acquisition
                && Selector(acquisition["operation"]) is { } operationSelector)
            {
                var address = Address(operationSelector, "ConnectedServiceOperationDiscovery");
                if (address.ScopeRef != scope || !planning.TryGetDocument(address.Kind, address.Name, address.Namespace.Value, out var declaration))
                    throw Missing(address.Kind, address.Name);
                var selected = await ResolveOutputAsync(declaration, operation, Text(operationSelector, "output"), cancellationToken);
                if (selected is null || !selected.Enabled && selected.Generation == 1)
                {
                    if (applying) throw new InvalidOperationException("bootstrap_discovery_output_unavailable");
                    deferred = true;
                }
                else
                {
                    if (!selected.Enabled) throw new InvalidOperationException("bootstrap_operation_disabled_by_operator");
                    acquisition["operation"] = Node(Revision(selected));
                    fingerprints.Add(acquisition["operation"]!.ToJsonString());
                }
            }
        }
        if (resource.Kind == "ProjectSharing") deferred |= await PrepareProjectConnectionsAsync(root, resource, operation, planning, applying, fingerprints, cancellationToken);
        ValidateLocalShape(resource.Kind, root);
        var result = resource with { Definition = JsonSerializer.SerializeToElement(root, Json) };
        if (deferred && resource.Kind == "ConnectedService")
            await services.ValidateForCreateAsync(WorkspaceBootstrapResource.Parse<ConnectedServiceResource>(result) with { ScopeRef = scope },
                cancellationToken, deferredInputs, plannedSecrets);
        if (deferred && resource.Kind == "Storage")
        {
            var version = await storages.ValidateConfigurationAsync(scope, resource.Metadata.Namespace,
                root["type"]!.Deserialize<StorageTypeReference>(Json)!, JsonSerializer.SerializeToElement(root["configuration"]),
                JsonSerializer.SerializeToElement(root["envelope"]), cancellationToken);
            await storages.ValidateCollectionsAsync(version, root["collections"]?.Deserialize<StorageDocumentCollection[]>(Json) ?? [],
                cancellationToken, allowUnresolvedSchemaBindings: true);
            if (selectedExports is not null)
                StorageManagementService.ValidateExportBindings(version, root["bindings"]?.Deserialize<StorageExportBinding[]>(Json) ?? [], selectedExports);
        }
        if (deferred && resource.Kind == ToolResourceKinds.ToolDefinition)
            planning.Register(ToolResourceKinds.Tool, AgentstrationToolProvider.ToolResourceName(
                ToolDefinitionService.VersionName(resource.Metadata.Name, root["version"]?.GetValue<string>() ?? "1.0.0")), resource.Metadata.Namespace.Value, resource);
        return (result, deferred);

        void ResolveExports(IReadOnlyList<ConnectedServiceExportDescriptor> exports)
        {
            foreach (var binding in (root["bindings"] as JsonArray ?? []).OfType<JsonObject>())
                if (binding["exportName"] is JsonValue name)
                {
                    var export = exports.SingleOrDefault(item => item.Id == name.GetValue<string>())
                        ?? throw new InvalidOperationException("bootstrap_export_not_found");
                    binding.Remove("exportName");
                    binding["export"] = Node(export);
                }
        }

        ScopedResourceAddress Address(JsonObject selector, string expectedKind)
        {
            if (selector["kind"] is { } kind && kind.GetValue<string>() != expectedKind)
                throw new InvalidOperationException("bootstrap_reference_kind_invalid");
            var targetScope = selector["scopeRef"]?.Deserialize<ResourceScopeRef>(Json) ?? scope;
            var ns = selector["namespace"]?.Deserialize<ResourceNamespace>(Json) ?? resource.Metadata.Namespace;
            return new(targetScope, ns, expectedKind, Text(selector, "name"));
        }
        void Defer(string kind, ScopedResourceAddress address)
        {
            if (applying || address.ScopeRef != scope || !planning.Contains(kind, address.Name, address.Namespace.Value))
                throw Missing(kind, address.Name);
            deferred = true;
        }
        async Task InitializeAsync(JsonNode node)
        {
            if (node is JsonObject obj)
                foreach (var property in obj.ToArray())
                {
                    if (property.Value is JsonObject wrapper && wrapper.Count == 1 && wrapper["valueFrom"] is JsonObject from)
                    {
                        var selection = operation.Inputs.SingleOrDefault(input => input.Profile == operation.ProfileName && input.Configuration == Text(from, "configuration"));
                        var binding = resource.Kind == "ConnectedService" && property.Key == "literal" && obj["inputId"] is JsonValue ? obj : null;
                        var resolved = await InputAsync(from, false, false, binding);
                        if (selection is { Kind: "parameter", Resource: { } parameter } && property.Key == "literal")
                        {
                            obj.Remove(property.Key);
                            obj["kind"] = "parameter";
                            obj["parameter"] = Node(new ParameterReference(parameter.Address, parameter.ScopeRef));
                        }
                        else if (resolved is not null) obj[property.Key] = resolved;
                        else
                        {
                            deferred = true;
                            if (property.Key == "literal" && obj["inputId"] is JsonValue inputId) deferredInputs.Add(inputId.GetValue<string>());
                        }
                    }
                    else if (property.Value is { } child) await InitializeAsync(child);
                }
            else if (node is JsonArray array)
                foreach (var child in array.OfType<JsonNode>()) await InitializeAsync(child);
        }
        async Task<JsonNode?> InputAsync(JsonObject from, bool secured, bool configured, JsonObject? binding)
        {
            if (from.Any(item => secured ? item.Key != "configuration" : item.Key is not ("configuration" or "type" or "default")))
                throw new InvalidOperationException("bootstrap_configuration_key_invalid");
            var key = Text(from, "configuration");
            if (!key.StartsWith(secured ? SecretsRoot : ParametersRoot, StringComparison.OrdinalIgnoreCase)
                || key.Length > 512) throw new InvalidOperationException("bootstrap_configuration_key_invalid");
            var valueType = secured ? "string" : from["type"]?.GetValue<string>() ?? "string";
            var selected = operation.Inputs.SingleOrDefault(input => input.Profile == operation.ProfileName && input.Configuration == key);
            JsonNode? value = null;
            if (selected is not null)
            {
                if (secured)
                {
                    if (selected.Kind != "secret" || selected.Resource is not { Kind: "Secret" } secret || selected.Literal is not null)
                        throw new InvalidOperationException("bootstrap_secured_input_requires_secret");
                    var status = await secrets.GetAuthorizedStatusAsync(new(secret.Address, secret.ScopeRef),
                        new(scope, new(resource.Metadata.Namespace, resource.Kind, resource.Metadata.Name)), cancellationToken);
                    var metadata = await secrets.GetSecretExactAsync(secret.ScopeRef, secret.Name, cancellationToken);
                    fingerprints.Add(JsonSerializer.Serialize(new { secret, metadata?.Value.Uid, metadata?.Value.Generation }, Json));
                    configured |= status == SecretValueStatus.Configured;
                }
                else if (selected.Kind == "literal" && selected.Resource is null && selected.Literal is { } literal)
                    value = JsonNode.Parse(literal.GetRawText());
                else if (selected.Kind == "parameter" && selected.Resource is { Kind: "Parameter" } parameter && selected.Literal is null && binding is not null)
                {
                    var resolved = await parameters.ResolveAsync(new(parameter.Address, parameter.ScopeRef),
                        new(scope, new(resource.Metadata.Namespace, resource.Kind, resource.Metadata.Name)), cancellationToken)
                        ?? throw new InvalidOperationException("bootstrap_parameter_unavailable");
                    value = JsonNode.Parse(resolved.Value.GetRawText());
                }
                else throw new InvalidOperationException("bootstrap_input_selection_invalid");
            }
            else if (!configured)
            {
                var text = configuration[key];
                if (!string.IsNullOrWhiteSpace(text))
                {
                    configured = true;
                    if (!secured)
                        try { value = valueType == "string" ? JsonValue.Create(text) : JsonNode.Parse(text); }
                        catch (JsonException) { throw new InvalidOperationException("bootstrap_configuration_type_invalid"); }
                }
                else if (!secured && from["default"] is { } fallback) value = fallback.DeepClone();
            }
            if (!secured && value is not null)
            {
                ValidateValue(value, valueType);
                configured = true;
                fingerprints.Add(key + ":" + value.ToJsonString());
            }
            inputs.Add(new(operation.ProfileName, resource.Metadata.Name, key, valueType, secured, configured, scope, resource.Metadata.Namespace)
            {
                AllowsParameter = binding is not null && scope.Kind != ResourceScopeKind.Instance,
                ParameterCreationScope = binding is not null && operation.Target?.TenantId is { } tenant ? ResourceScopeRef.Tenant(tenant) : null
            });
            if (!configured && applying) throw new InvalidOperationException("bootstrap_configuration_required");
            return value;
        }
    }

    public async Task<ConnectedServiceOperationResource?> ResolveOutputAsync(BootstrapResourceDocument declaration,
        BootstrapResourceOperationContext operation, string outputName, CancellationToken cancellationToken)
    {
        var definition = declaration.Definition.Deserialize<BootstrapDiscoveryDefinition>(Json)!;
        var output = definition.Operations.SingleOrDefault(item => item.Name == outputName)
            ?? throw new InvalidOperationException("bootstrap_discovery_output_unknown");
        var scope = DataSourceProfileBootstrapResourceHandler.TargetScope(operation);
        var selector = definition.ConnectedService.ResourceRef;
        var address = new ScopedResourceAddress(selector.ScopeRef ?? scope, selector.Namespace ?? declaration.Metadata.Namespace,
            "ConnectedService", selector.Name);
        var service = (await services.GetExactAsync(address.ScopeRef, address.Namespace, address.Name, cancellationToken))?.Value;
        if (service is null) return null;
        var candidates = (await discoveries.ListOperationsAsync(address.ScopeRef, address.Namespace, address.Name, cancellationToken))
            .Where(item => item.Definition.Service.ServiceUid == service.Uid && item.Definition.Service.Generation == service.Generation
                && item.ObservedState.Availability == ConnectedServiceOperationAvailability.Available
                && Matches(item.Definition.Definition, output.Selector)).ToArray();
        if (candidates.Length > 1) throw new InvalidOperationException("bootstrap_discovery_output_ambiguous");
        if (candidates.Length == 0) return null;
        var candidate = candidates[0];
        _ = await operations.ResolveAsync(Revision(candidate), scope, false, cancellationToken);
        return candidate;
    }

    internal static ConnectedServiceOperationRevisionReference Revision(ConnectedServiceOperationResource operation) =>
        new(new(operation.ScopeRef!.Value, operation.Namespace, operation.Kind, operation.Name), operation.Uid, operation.Generation, operation.Definition.DefinitionDigest);

    internal static bool Matches(JsonElement definition, IReadOnlyDictionary<string, string> selector) =>
        selector.All(item => definition.TryGetProperty(item.Key, out var value)
            && string.Equals(value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText(), item.Value, StringComparison.Ordinal));

    internal static IReadOnlyList<BootstrapResourcePlanDetail> PendingDependencies(BootstrapResourceDocument document)
    {
        var result = new List<BootstrapResourcePlanDetail>();
        var root = JsonNode.Parse(document.Definition.GetRawText())!;
        if (document.Kind == "DataSource")
        {
            Add("Storage", root["dataset"]?["storage"]);
            Add("ConnectedServiceOperationDiscovery", root["dataset"]?["acquisition"]?["operation"]);
        }
        else if (document.Kind == "Storage") Add("ConnectedService", root["connectedService"]);
        else if (document.Kind == "KnowledgeSource") Add("Storage", root["output"]?["storage"]);
        else if (document.Kind == "ConnectedService")
            foreach (var binding in (root["bindings"] as JsonArray ?? []).OfType<JsonObject>()) Add("Secret", binding["secret"]);
        return result;

        void Add(string kind, JsonNode? value)
        {
            if (Selector(value) is not { } selector) return;
            result.Add(new(kind, Text(selector, "name") + (selector["output"] is JsonValue output ? "/" + output.GetValue<string>() : string.Empty),
                BootstrapResourceDisposition.Create, kind == "ConnectedServiceOperationDiscovery" ? "bootstrap_discovery_contract_pending" : "bootstrap_resource_dependency_pending"));
        }
    }

    internal static JsonObject? Selector(JsonNode? value)
    {
        if (value is not JsonObject obj || !obj.ContainsKey("resourceRef")) return null;
        if (obj.Count != 1 || obj["resourceRef"] is not JsonObject selector) throw new InvalidOperationException("bootstrap_reference_invalid");
        if (selector.Any(item => item.Key is not ("name" or "namespace" or "scopeRef" or "kind" or "version" or "output")))
            throw new InvalidOperationException("bootstrap_reference_invalid");
        _ = Text(selector, "name");
        return selector;
    }

    private static void ValidateValue(JsonNode value, string type)
    {
        var element = JsonSerializer.SerializeToElement(value);
        var valid = type switch
        {
            "string" => element.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(element.GetString()),
            "integer" => element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out _),
            "number" => element.ValueKind == JsonValueKind.Number,
            "boolean" => element.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "object" => element.ValueKind == JsonValueKind.Object,
            "array" => element.ValueKind == JsonValueKind.Array,
            _ => false
        };
        if (!valid) throw new InvalidOperationException("bootstrap_configuration_type_invalid");
    }

    private static void ValidateLocalShape(string kind, JsonObject definition)
    {
        if (kind is "ConnectedService" or "Storage" or "DataSource" or "KnowledgeSource")
        {
            _ = Text(definition, "displayName");
            if (definition["displayName"]!.GetValue<string>().Length > 200
                || definition["description"] is JsonValue description && description.GetValue<string>().Length > 2000
                || definition["enabled"] is { } enabled && enabled.GetValueKind() is not (JsonValueKind.True or JsonValueKind.False))
                throw new InvalidOperationException("bootstrap_resource_metadata_invalid");
        }
        if (kind == "DataSource" && definition["dataset"] is JsonObject dataset)
        {
            if (dataset["storage"] is not JsonObject || dataset["acquisition"] is not JsonObject acquisition
                || acquisition["operation"] is not JsonObject || acquisition["parameters"] is not JsonObject)
                throw new InvalidOperationException("bootstrap_dataset_invalid");
            if (dataset["documentCollection"] is JsonNode collection && string.IsNullOrWhiteSpace(collection.GetValue<string>()))
                throw new InvalidOperationException("bootstrap_collection_invalid");
        }
        if (kind == "Storage" && (definition["configuration"] is not JsonObject || definition["envelope"] is not JsonObject))
            throw new InvalidOperationException("bootstrap_storage_invalid");
    }
    internal static string Text(JsonObject value, string key) => value[key] is JsonValue text
        && text.TryGetValue<string>(out var result) && !string.IsNullOrWhiteSpace(result) && result.Length <= 512
            ? result : throw new InvalidOperationException("bootstrap_reference_invalid");
    private static JsonNode Node<T>(T value) => JsonSerializer.SerializeToNode(value, Json)!;
    private static InvalidOperationException Missing(string kind, string name) => new($"bootstrap_reference_not_available: {kind}/{name}");
}

public sealed record BootstrapLogicalReference(string Name, ResourceNamespace? Namespace = null, ResourceScopeRef? ScopeRef = null, string? Kind = null);
public sealed record BootstrapServiceSelector(BootstrapLogicalReference ResourceRef);
public sealed record BootstrapDiscoveredOutput(string Name, IReadOnlyDictionary<string, string> Selector, bool Enabled = true);
public sealed record BootstrapDiscoveryDefinition(BootstrapServiceSelector ConnectedService, IReadOnlyList<BootstrapDiscoveredOutput> Operations);
