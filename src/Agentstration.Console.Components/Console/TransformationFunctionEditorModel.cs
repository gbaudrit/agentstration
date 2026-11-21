using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Agentstration.Functions.Abstractions;
using Agentstration.Functions.Contracts;
using Agentstration.Resources;

namespace Agentstration.Web.Console;

public sealed class TransformationFunctionEditorModel
{
    [Required] public string Name { get; set; } = string.Empty;
    [Required, StringLength(200)] public string DisplayName { get; set; } = string.Empty;
    public string Namespace { get; set; } = ResourceNamespace.DefaultValue;
    [StringLength(4000)] public string? Description { get; set; }
    public string Tags { get; set; } = string.Empty;
    [Required] public ResourceScopeRef? ScopeRef { get; set; }
    public bool Enabled { get; set; } = true;
    [Required, StringLength(64)] public string Version { get; set; } = "1.0.0";
    [Required] public string KindKey { get; set; } = string.Empty;
    [Required] public string ImplementationKey { get; set; } = string.Empty;
    public bool IdentityComplete
    {
        get
        {
            if (ScopeRef is null || string.IsNullOrWhiteSpace(DisplayName) || DisplayName.Length > 200 || Description?.Length > 4000) return false;
            try { _ = ResourceAddress.Create(ResourceNamespace.Parse(Namespace), FunctionResourceKinds.TransformationFunction, Name); return true; }
            catch (ArgumentException) { return false; }
        }
    }
    public bool ConfigurationComplete => !string.IsNullOrWhiteSpace(Version) && Version.Length <= 64
        && !string.IsNullOrWhiteSpace(KindKey) && !string.IsNullOrWhiteSpace(ImplementationKey);
    public static string KindIdentity(FunctionKindResource kind, string version) => $"{kind.ScopeRef}|{kind.Namespace}|{kind.Name}|{version}";
    public static string ImplementationIdentity(NativeFunctionDescriptor value) => value.Id + "@" + value.Version;
    public static TransformationFunctionEditorModel From(TransformationFunctionResource resource)
    {
        var latest = resource.Definition.Versions.Last();
        var implementation = latest.Configuration.GetProperty("implementation");
        return new()
        {
            Name = resource.Name,
            Namespace = resource.Namespace.Value,
            ScopeRef = resource.ScopeRef,
            DisplayName = resource.Definition.DisplayName,
            Description = resource.Definition.Description,
            Tags = string.Join(Environment.NewLine, resource.Metadata.Tags.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}={p.Value}")),
            Enabled = resource.Definition.Enabled,
            Version = latest.Version,
            KindKey = $"{latest.FunctionKind.ScopeRef ?? resource.ScopeRef}|{latest.FunctionKind.Namespace ?? resource.Namespace}|{latest.FunctionKind.Name}|{latest.FunctionKind.Version}",
            ImplementationKey = implementation.GetProperty("id").GetString() + "@" + implementation.GetProperty("version").GetString()
        };
    }
    public TransformationFunctionResource ToResource(TransformationFunctionResource? original, FunctionKindResource kind, FunctionKindVersion kindVersion, NativeFunctionDescriptor implementation)
    {
        var reference = new FunctionReference(kind.Name, kindVersion.Version, kind.ScopeRef, kind.Namespace, kind.Uid, FunctionDigests.Compute(kindVersion));
        var next = new TransformationFunctionVersion(Version, reference,
            JsonSerializer.SerializeToElement(new { implementation = new { id = implementation.Id, version = implementation.Version } }));
        // Preserve the frozen native contract and original reference when this immutable version is unchanged.
        var previous = original?.Definition.Versions.FirstOrDefault(v => v.Version == Version);
        if (previous is not null && KindKey == $"{previous.FunctionKind.ScopeRef ?? original!.ScopeRef}|{previous.FunctionKind.Namespace ?? original!.Namespace}|{previous.FunctionKind.Name}|{previous.FunctionKind.Version}"
            && JsonElement.DeepEquals(previous.Configuration, next.Configuration)) next = previous;
        var versions = original?.Definition.Versions.Where(v => v.Version != Version).Append(next).ToArray() ?? [next];
        var resource = original ?? new TransformationFunctionResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = FunctionResourceKinds.TransformationFunction,
            ScopeRef = ScopeRef,
            Metadata = new() { Name = Name, Namespace = ResourceNamespace.Parse(Namespace) },
            Definition = new(DisplayName, versions)
        };
        return resource with
        {
            Metadata = resource.Metadata with { Tags = ParseTags() },
            Definition = new(DisplayName, versions, Enabled) { Description = string.IsNullOrWhiteSpace(Description) ? null : Description }
        };
    }
    private IReadOnlyDictionary<string, string> ParseTags() => Tags.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(line => line.Split('=', 2)).ToDictionary(p => p[0].Trim(), p => p[1].Trim(), StringComparer.Ordinal);
}
