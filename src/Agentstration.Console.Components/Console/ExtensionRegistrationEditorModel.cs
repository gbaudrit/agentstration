using System.ComponentModel.DataAnnotations;
using Agentstration.Extensions.Contracts;
using Agentstration.Resources;

namespace Agentstration.Web.Console;

public sealed class ExtensionRegistrationEditorModel
{
    public string Name { get; set; } = string.Empty;
    public string Namespace { get; set; } = ResourceNamespace.DefaultValue;
    public string? Description { get; set; }
    public string Tags { get; set; } = string.Empty;
    [Required] public string DisplayName { get; set; } = string.Empty;
    [Required, Url] public string Endpoint { get; set; } = string.Empty;
    public string? ExpectedExtensionId { get; set; }
    public bool Enabled { get; set; }

    public static ExtensionRegistrationEditorModel From(Resource? resource, ExtensionInventoryItemResponse item) => new()
    {
        Name = resource?.Name ?? string.Empty,
        Namespace = resource?.Namespace.Value ?? ResourceNamespace.DefaultValue,
        Tags = resource is null ? string.Empty : string.Join(Environment.NewLine, resource.Metadata.Tags.OrderBy(value => value.Key, StringComparer.Ordinal).Select(value => $"{value.Key}={value.Value}")),
        DisplayName = resource is ExtensionRegistrationResource saved ? saved.Definition.DisplayName : resource is AepEnrollmentRequestResource request ? request.Definition.ExtensionName : item.DisplayName,
        Endpoint = resource is ExtensionRegistrationResource registration ? registration.Definition.Endpoint.AbsoluteUri : resource is AepEnrollmentRequestResource enrollment ? enrollment.Definition.Endpoint.AbsoluteUri : item.Endpoint.AbsoluteUri,
        ExpectedExtensionId = (resource as ExtensionRegistrationResource)?.Definition.ExpectedExtensionId,
        Enabled = resource is ExtensionRegistrationResource value ? value.Definition.Enabled : item.RegistrationEnabled
    };
}
