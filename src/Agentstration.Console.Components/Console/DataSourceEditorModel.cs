using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Agentstration.DataSources.Contracts;
using Agentstration.Resources;

namespace Agentstration.Web.Console;

public sealed class DataSourceEditorModel
{
    [Required, RegularExpression("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$")]
    public string Name { get; set; } = string.Empty;
    [Required, StringLength(200)] public string DisplayName { get; set; } = string.Empty;
    public string Namespace { get; set; } = "default";
    [StringLength(2000)] public string? Description { get; set; }
    public string Tags { get; set; } = string.Empty;
    [Required] public ResourceScopeRef? Scope { get; set; }
    [Required] public string ProfileKey { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public JsonElement Configuration { get; set; } = JsonSerializer.SerializeToElement(new { });
    public bool NamespaceValid
    {
        get { try { _ = ResourceNamespace.Parse(Namespace); return true; } catch (ArgumentException) { return false; } }
    }
    public bool IdentityComplete => !string.IsNullOrWhiteSpace(Name) && !string.IsNullOrWhiteSpace(DisplayName)
        && Scope is not null && NamespaceValid;

    public DataSourceProperties Properties(DataSourceResource? saved = null)
    {
        var parts = ProfileKey.Split('|', 3);
        return new()
        {
            DisplayName = DisplayName.Trim(), Description = string.IsNullOrWhiteSpace(Description) ? null : Description.Trim(),
            Enabled = Enabled, Configuration = Configuration.Clone(),
            Profile = saved?.Definition.Profile is { } original && ProfileKey == From(saved).ProfileKey ? original : new(parts[2], ResourceScopeRef.Parse(parts[0]), ResourceNamespace.Parse(parts[1]))
        };
    }

    public static DataSourceEditorModel From(DataSourceResource source) => new()
    {
        Name = source.Name, Namespace = source.Namespace.Value, DisplayName = source.Definition.DisplayName,
        Description = source.Definition.Description, Scope = source.ScopeRef, Enabled = source.Definition.Enabled,
        Configuration = source.Definition.Configuration.Clone(),
        ProfileKey = source.Definition.Profile is { } profile ? $"{profile.ScopeRef ?? source.ScopeRef}|{profile.Namespace ?? source.Namespace}|{profile.Name}" : string.Empty,
        Tags = string.Join(Environment.NewLine, source.Metadata.Tags.Select(pair => $"{pair.Key}={pair.Value}"))
    };
}
