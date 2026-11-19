using System.ComponentModel.DataAnnotations;
using Agentstration.Resources;
using Agentstration.Tools;

namespace Agentstration.Web.Console;

public sealed class ToolProviderEditorModel
{
    public string Name { get; set; } = string.Empty;
    public string Namespace { get; set; } = ResourceNamespace.DefaultValue;
    public string DisplayName { get; set; } = string.Empty;
    public string? Description => null;
    public string Tags { get; set; } = string.Empty;
    public ResourceScopeRef? ScopeRef { get; set; }
    public bool Enabled { get; set; } = true;
    public ToolProviderType ProviderType { get; set; } = ToolProviderType.Mcp;
    public string ExtensionId { get; set; } = string.Empty;
    public ResourceNamespace? ExtensionNamespace { get; set; }
    public ResourceScopeRef? ExtensionScopeRef { get; set; }
    public McpToolProviderTransport Transport { get; set; } = McpToolProviderTransport.Stdio;
    public string Command { get; set; } = string.Empty;
    public string Arguments { get; set; } = string.Empty;
    public string WorkingDirectory { get; set; } = string.Empty;
    public string Endpoint { get; set; } = string.Empty;
    public string EnvironmentReferences { get; set; } = string.Empty;
    public bool IdentityComplete
    {
        get
        {
            if (ScopeRef is null || string.IsNullOrWhiteSpace(DisplayName)) return false;
            try { _ = ResourceAddress.Create(ResourceNamespace.Parse(Namespace), ToolResourceKinds.ToolProvider, Name); return true; }
            catch (ArgumentException) { return false; }
        }
    }
    public bool ConnectionComplete => !Validate().Any(v => !v.MemberNames.Contains(nameof(Name)) && !v.MemberNames.Contains(nameof(DisplayName)) && !v.MemberNames.Contains(nameof(ScopeRef)));
    public IEnumerable<ValidationResult> Validate()
    {
        if (string.IsNullOrWhiteSpace(DisplayName)) yield return new("IdentityIncomplete", [nameof(DisplayName)]);
        if (ScopeRef is null) yield return new("IdentityIncomplete", [nameof(ScopeRef)]);
        bool validName;
        try { _ = ResourceAddress.Create(ResourceNamespace.Parse(Namespace), ToolResourceKinds.ToolProvider, Name); validName = true; }
        catch (ArgumentException) { validName = false; }
        if (!validName) yield return new("IdentityIncomplete", [nameof(Name)]);
        bool validReferences;
        try { _ = Pairs(EnvironmentReferences); validReferences = true; } catch (ArgumentException) { validReferences = false; }
        if (!validReferences) yield return new("ConnectionIncomplete", [nameof(EnvironmentReferences)]);
        if (ProviderType == ToolProviderType.Aep)
        {
            if (string.IsNullOrWhiteSpace(ExtensionId)) yield return new("ConnectionIncomplete", [nameof(ExtensionId)]);
        }
        else if (ProviderType == ToolProviderType.Mcp)
        {
            if (Transport == McpToolProviderTransport.Stdio && string.IsNullOrWhiteSpace(Command)) yield return new("ConnectionIncomplete", [nameof(Command)]);
            if (Transport != McpToolProviderTransport.Stdio && !Uri.TryCreate(Endpoint, UriKind.Absolute, out _)) yield return new("ConnectionIncomplete", [nameof(Endpoint)]);
        }
    }
    public ToolProviderProperties Properties() => new()
    {
        DisplayName = DisplayName,
        ProviderType = ProviderType,
        Enabled = Enabled,
        Aep = ProviderType == ToolProviderType.Aep ? new AepToolProviderConfiguration { Extension = new ResourceReference(ExtensionId, ExtensionScopeRef, ExtensionNamespace) } : null,
        Mcp = ProviderType == ToolProviderType.Mcp ? new McpToolProviderConfiguration
        {
            Transport = Transport,
            Command = Null(Command),
            Arguments = Lines(Arguments),
            WorkingDirectory = Null(WorkingDirectory),
            Endpoint = Uri.TryCreate(Endpoint, UriKind.Absolute, out var uri) ? uri : null,
            EnvironmentReferences = Pairs(EnvironmentReferences)
        } : null
    };
    public static ToolProviderEditorModel From(ToolProviderResource value) => new()
    {
        Name = value.Name,
        Namespace = value.Namespace.Value,
        DisplayName = value.Definition.DisplayName,
        ScopeRef = value.ScopeRef,
        Tags = string.Join(Environment.NewLine, value.Metadata.Tags.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}={p.Value}")),
        Enabled = value.Definition.Enabled,
        ProviderType = value.Definition.ProviderType,
        ExtensionId = value.Definition.Aep?.Extension.Name ?? string.Empty,
        ExtensionNamespace = value.Definition.Aep?.Extension.Namespace,
        ExtensionScopeRef = value.Definition.Aep?.Extension.ScopeRef,
        Transport = value.Definition.Mcp?.Transport ?? McpToolProviderTransport.Stdio,
        Command = value.Definition.Mcp?.Command ?? string.Empty,
        Arguments = string.Join(Environment.NewLine, value.Definition.Mcp?.Arguments ?? []),
        WorkingDirectory = value.Definition.Mcp?.WorkingDirectory ?? string.Empty,
        Endpoint = value.Definition.Mcp?.Endpoint?.ToString() ?? string.Empty,
        EnvironmentReferences = string.Join(Environment.NewLine, (value.Definition.Mcp?.EnvironmentReferences ?? new Dictionary<string, string>()).Select(p => $"{p.Key}={p.Value}"))
    };
    private static string? Null(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string[] Lines(string value) => value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    private static IReadOnlyDictionary<string, string> Pairs(string value) => Lines(value).Select(line => line.Split('=', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0].Trim(), p => p[1].Trim(), StringComparer.Ordinal);
}
