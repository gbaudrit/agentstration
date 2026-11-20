using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Agentstration.DataSources.Contracts;
using Agentstration.Resources;

namespace Agentstration.Web.Console;

public sealed class DataSourceProfileEditorModel
{
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };
    public string Name { get; set; } = string.Empty;
    public string Namespace { get; set; } = ResourceNamespace.DefaultValue;
    public string DisplayName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Tags { get; set; } = string.Empty;
    public ResourceScopeRef? ScopeRef { get; set; }
    public string Version { get; set; } = "1.0.0";
    public bool Enabled { get; set; } = true;
    public string FlowKey { get; set; } = string.Empty;
    public string DiscoveryFlowKey { get; set; } = string.Empty;
    public string SchemaJson { get; set; } = "{\"type\":\"object\",\"additionalProperties\":true}";
    public string ToolBindingsJson { get; set; } = "[]";
    public string DiscoveryToolBindingsJson { get; set; } = "[]";
    public string LimitsJson { get; set; } = "{}";
    public string PoliciesJson { get; set; } = "{}";
    public bool NamespaceValid { get { try { _ = ResourceNamespace.Parse(Namespace); return !string.IsNullOrWhiteSpace(Namespace); } catch (ArgumentException) { return false; } } }
    public bool IdentityComplete => !string.IsNullOrWhiteSpace(DisplayName) && !string.IsNullOrWhiteSpace(Name) && ScopeRef is not null && NamespaceValid;
    public bool AcquisitionComplete(DataSourceProfileResource? saved, IReadOnlyList<FlowSummary> flows) => !string.IsNullOrWhiteSpace(Version) && ResolveFlow(FlowKey, false, saved, flows) is not null;
    public bool ContractsComplete => ValidJson<JsonElement>(SchemaJson, JsonValueKind.Object)
        && ValidJson<IReadOnlyList<DataSourceProfileToolBinding>>(ToolBindingsJson, JsonValueKind.Array)
        && ValidJson<IReadOnlyList<DataSourceProfileToolBinding>>(DiscoveryToolBindingsJson, JsonValueKind.Array);
    public bool PoliciesComplete => ValidJson<IReadOnlyDictionary<string, JsonElement>>(LimitsJson, JsonValueKind.Object)
        && ValidJson<IReadOnlyDictionary<string, JsonElement>>(PoliciesJson, JsonValueKind.Object);
    public IEnumerable<ValidationResult> Validate(DataSourceProfileResource? saved, IReadOnlyList<FlowSummary> flows)
    {
        if (string.IsNullOrWhiteSpace(DisplayName)) yield return new("ProfileFieldRequired", [nameof(DisplayName)]);
        if (string.IsNullOrWhiteSpace(Name)) yield return new("ProfileFieldRequired", [nameof(Name)]);
        if (!NamespaceValid) yield return new("ProfileIdentityInvalid", [nameof(Namespace)]);
        if (ScopeRef is null) yield return new("ProfileFieldRequired", [nameof(ScopeRef)]);
        if (string.IsNullOrWhiteSpace(Version)) yield return new("ProfileFieldRequired", [nameof(Version)]);
        if (ResolveFlow(FlowKey, false, saved, flows) is null) yield return new("ProfileFieldRequired", [nameof(FlowKey)]);
        if (!ValidJson<JsonElement>(SchemaJson, JsonValueKind.Object)) yield return new("ProfileJsonInvalid", [nameof(SchemaJson)]);
        if (!ValidJson<IReadOnlyList<DataSourceProfileToolBinding>>(ToolBindingsJson, JsonValueKind.Array)) yield return new("ProfileJsonInvalid", [nameof(ToolBindingsJson)]);
        if (!ValidJson<IReadOnlyList<DataSourceProfileToolBinding>>(DiscoveryToolBindingsJson, JsonValueKind.Array)) yield return new("ProfileJsonInvalid", [nameof(DiscoveryToolBindingsJson)]);
        if (!ValidJson<IReadOnlyDictionary<string, JsonElement>>(LimitsJson, JsonValueKind.Object)) yield return new("ProfileJsonInvalid", [nameof(LimitsJson)]);
        if (!ValidJson<IReadOnlyDictionary<string, JsonElement>>(PoliciesJson, JsonValueKind.Object)) yield return new("ProfileJsonInvalid", [nameof(PoliciesJson)]);
    }
    private static bool ValidJson<T>(string value, JsonValueKind kind)
    {
        try { using var json = JsonDocument.Parse(value); return json.RootElement.ValueKind == kind && JsonSerializer.Deserialize<T>(value) is not null; }
        catch (JsonException) { return false; }
    }
    public DataSourceFlowTarget? ResolveFlow(string key, bool discovery, DataSourceProfileResource? saved, IReadOnlyList<FlowSummary> flows)
    {
        if (key == "saved-acquisition" && !discovery) return saved?.Definition.AcquisitionFlow;
        if (key == "saved-discovery" && discovery) return saved?.Definition.DiscoveryFlow;
        var flow = flows.FirstOrDefault(f => f.ScopeRef is not null && FlowIdentity(f) == key);
        return flow is null ? null : new() { ScopeRef = flow.ScopeRef, Name = flow.Id, Namespace = flow.Namespace, UseActiveVersion = true };
    }
    public DataSourceProfileProperties Definition(DataSourceProfileResource? saved, IReadOnlyList<FlowSummary> flows) => new()
    {
        DisplayName = DisplayName.Trim(),
        Description = string.IsNullOrWhiteSpace(Description) ? null : Description.Trim(),
        Enabled = Enabled,
        Version = Version.Trim(),
        ConfigurationSchema = JsonSerializer.Deserialize<JsonElement>(SchemaJson),
        AcquisitionFlow = ResolveFlow(FlowKey, false, saved, flows)!,
        DiscoveryFlow = ResolveFlow(DiscoveryFlowKey, true, saved, flows),
        ToolBindings = JsonSerializer.Deserialize<IReadOnlyList<DataSourceProfileToolBinding>>(ToolBindingsJson)!,
        DiscoveryToolBindings = JsonSerializer.Deserialize<IReadOnlyList<DataSourceProfileToolBinding>>(DiscoveryToolBindingsJson)!,
        Limits = JsonSerializer.Deserialize<IReadOnlyDictionary<string, JsonElement>>(LimitsJson)!,
        Policies = JsonSerializer.Deserialize<IReadOnlyDictionary<string, JsonElement>>(PoliciesJson)!
    };
    public static DataSourceProfileEditorModel From(DataSourceProfileResource resource) => new()
    {
        Name = resource.Name,
        Namespace = resource.Namespace.Value,
        DisplayName = resource.Definition.DisplayName,
        Description = resource.Definition.Description,
        ScopeRef = resource.ScopeRef,
        Tags = string.Join(Environment.NewLine, resource.Metadata.Tags.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}={p.Value}")),
        Enabled = resource.Definition.Enabled,
        Version = resource.Definition.Version,
        FlowKey = "saved-acquisition",
        DiscoveryFlowKey = resource.Definition.DiscoveryFlow is null ? string.Empty : "saved-discovery",
        SchemaJson = Json(resource.Definition.ConfigurationSchema),
        ToolBindingsJson = Json(resource.Definition.ToolBindings),
        DiscoveryToolBindingsJson = Json(resource.Definition.DiscoveryToolBindings),
        LimitsJson = Json(resource.Definition.Limits),
        PoliciesJson = Json(resource.Definition.Policies)
    };
    public static string Json<T>(T value) => JsonSerializer.Serialize(value, IndentedJson);
    public static string FlowIdentity(FlowSummary flow) => flow.ScopeRef is null ? string.Empty : $"{flow.ScopeRef.Value.Value}|{flow.Namespace.Value}|{flow.Id}";
}
