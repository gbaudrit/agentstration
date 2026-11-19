using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Agentstration.Resources;
using Agentstration.Tools;
using Agentstration.Tools.Contracts;

namespace Agentstration.Web.Console;

public sealed class ToolCategoryEditorModel
{
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Namespace { get; set; } = ResourceNamespace.Default.Value;
    public string Tags { get; set; } = string.Empty;
    public ResourceScopeRef? ScopeRef { get; set; }
    public HashSet<string> SelectedKeys { get; } = new(StringComparer.Ordinal);

    public bool NamespaceValid
    {
        get { try { _ = ResourceNamespace.Parse(Namespace); return !string.IsNullOrWhiteSpace(Namespace); } catch (ArgumentException) { return false; } }
    }
    public static bool NameValid(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 128 && value.All(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_');
    public bool IdentityComplete => !Validate().Any();
    public IEnumerable<ValidationResult> Validate()
    {
        if (!NameValid(Name)) yield return new("NameInvalid", [nameof(Name)]);
        if (string.IsNullOrWhiteSpace(DisplayName) || DisplayName.Length > 200) yield return new("DisplayNameInvalid", [nameof(DisplayName)]);
        if (Description.Length > 2000) yield return new("DescriptionInvalid", [nameof(Description)]);
        if (!NamespaceValid) yield return new("NamespaceInvalid", [nameof(Namespace)]);
        if (ScopeRef is null) yield return new("ScopeRequired", [nameof(ScopeRef)]);
    }
    public static bool CanAssign(ToolResource tool) => tool.ScopeRef is not null && tool.Definition.Enabled && tool.Definition.Discovery?.Available == true;
    public static string ToolKey(ToolResource tool) => $"{tool.ScopeRef?.Value}|{tool.Namespace.Value}|{tool.Name}";
    public static string MemberKey(ToolCategoryMember member) => member.Tool is { } tool ? ToolKey(tool) : "missing|" + JsonSerializer.Serialize(member.Reference);
    public IReadOnlyList<ResourceReference> References(IReadOnlyList<ToolCategoryMember> saved, IReadOnlyList<ToolResource> catalog)
    {
        var result = saved.Where(m => SelectedKeys.Contains(MemberKey(m))).Select(m => m.Reference).ToList();
        result.AddRange(catalog.Where(t => SelectedKeys.Contains(ToolKey(t)) && !saved.Any(m => m.Tool is not null && ToolKey(m.Tool) == ToolKey(t))).Select(t => new ResourceReference(t.Name, t.ScopeRef, t.Namespace)));
        return result;
    }
    public ToolCategoryProperties Properties(IReadOnlyList<ResourceReference> references) => new()
    {
        DisplayName = DisplayName.Trim(),
        Description = string.IsNullOrWhiteSpace(Description) ? null : Description.Trim(),
        Tools = references
    };
    public static ToolCategoryEditorModel From(ToolCategoryResource resource) => new()
    {
        Name = resource.Name,
        Namespace = resource.Namespace.Value,
        DisplayName = resource.Definition.DisplayName,
        Description = resource.Definition.Description ?? string.Empty,
        ScopeRef = resource.ScopeRef,
        Tags = string.Join(Environment.NewLine, resource.Metadata.Tags.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}={pair.Value}"))
    };
}
