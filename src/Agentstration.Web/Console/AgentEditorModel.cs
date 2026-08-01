using System.ComponentModel.DataAnnotations;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Contracts;

namespace Agentstration.Web.Console;

public sealed class AgentEditorModel
{
    [Required, RegularExpression("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$")]
    public string Name { get; set; } = string.Empty;

    [Required, RegularExpression("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$")]
    public string ResourceGroup { get; set; } = "default";

    [Required, RegularExpression("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$")]
    public string Location { get; set; } = "local";

    [Required]
    public string DisplayName { get; set; } = string.Empty;

    public string? Description { get; set; }

    [Required]
    public string AgentTypeResourceId { get; set; } = string.Empty;

    [Range(1, int.MaxValue)]
    public int? AgentTypeVersion { get; set; } = 1;

    public string? AdditionalInstructions { get; set; }

    [Required]
    public string ModelProfileResourceId { get; set; } = string.Empty;

    public string ToolResourceIds { get; set; } = string.Empty;
    public string Tags { get; set; } = string.Empty;

    public AgentResourceRequest ToRequest()
    {
        ValidateReference(AgentTypeResourceId, AgentstrationProviderNamespaces.Agents, "agentTypes", "Agent type");
        ValidateReference(ModelProfileResourceId, AgentstrationProviderNamespaces.Models, "modelProfiles", "Model profile");
        var tools = Lines(ToolResourceIds).Select(value =>
        {
            ValidateReference(value, AgentstrationProviderNamespaces.Tools, "tools", "Tool");
            return new ResourceReference(value);
        }).ToArray();
        if (tools.Select(tool => tool.ResourceId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != tools.Length)
            throw new ArgumentException("Tool resource IDs cannot be duplicated.");

        return new AgentResourceRequest
        {
            Type = AgentstrationResourceTypes.Agents,
            ApiVersion = ManagementApiVersions.V20260801,
            Name = Name.Trim(),
            ResourceGroup = ResourceGroup.Trim(),
            Location = Location.Trim(),
            Tags = ParseTags(Tags),
            Properties = new AgentProperties
            {
                DisplayName = DisplayName.Trim(),
                Description = NullIfWhiteSpace(Description),
                AgentType = new AgentTypeReference(AgentTypeResourceId.Trim(), AgentTypeVersion),
                AdditionalInstructions = NullIfWhiteSpace(AdditionalInstructions),
                ModelProfile = new ResourceReference(ModelProfileResourceId.Trim()),
                Tools = tools
            }
        };
    }

    public static AgentEditorModel FromResource(AgentResource resource) => new()
    {
        Name = resource.Name,
        ResourceGroup = resource.ResourceGroup ?? "default",
        Location = resource.Location ?? "local",
        DisplayName = resource.Properties.DisplayName,
        Description = resource.Properties.Description,
        AgentTypeResourceId = resource.Properties.AgentType.ResourceId,
        AgentTypeVersion = resource.Properties.AgentType.Version,
        AdditionalInstructions = resource.Properties.AdditionalInstructions,
        ModelProfileResourceId = resource.Properties.ModelProfile.ResourceId,
        ToolResourceIds = string.Join(Environment.NewLine, resource.Properties.Tools.Select(tool => tool.ResourceId)),
        Tags = string.Join(Environment.NewLine, resource.Tags.OrderBy(tag => tag.Key, StringComparer.Ordinal).Select(tag => $"{tag.Key}={tag.Value}"))
    };

    private static IReadOnlyDictionary<string, string> ParseTags(string value)
    {
        var tags = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in Lines(value))
        {
            var separator = line.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0 || separator == line.Length - 1) throw new ArgumentException("Each tag must use the format key=value.");
            var key = line[..separator].Trim();
            var tagValue = line[(separator + 1)..].Trim();
            if (!tags.TryAdd(key, tagValue)) throw new ArgumentException($"Tag '{key}' is duplicated.");
        }
        return tags;
    }

    private static string[] Lines(string value) => value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static void ValidateReference(string value, string provider, string resourceType, string label)
    {
        if (!ResourceIdentifier.TryParse(value?.Trim(), out var identifier)
            || !string.Equals(identifier.ProviderNamespace, provider, StringComparison.Ordinal)
            || !string.Equals(identifier.ResourceType, resourceType, StringComparison.Ordinal))
            throw new ArgumentException($"{label} must reference {provider}/{resourceType}.");
    }

    private static string? NullIfWhiteSpace(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
