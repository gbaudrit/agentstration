using System.Text.Json;
using Agentstration.Resources;

namespace Agentstration.Web.Console;

public enum ValueBindingSourceKind { Parameter, Secret, Literal, Caller }

public sealed record ValueBindingRequirement(
    string Id,
    bool Required,
    string ValueType,
    bool Secured = false,
    string? Description = null,
    string? Format = null,
    IReadOnlyList<JsonElement>? AllowedValues = null,
    JsonElement? LiteralSchema = null);

public sealed record ValueBindingOption(
    ValueBindingSourceKind Kind,
    ScopedResourceAddress Address,
    string DisplayName);

public sealed record ValueBindingSelection(
    ValueBindingSourceKind Kind,
    ScopedResourceAddress? Resource = null,
    JsonElement? Literal = null)
{
    public bool IsConfigured => Kind switch
    {
        ValueBindingSourceKind.Caller => true,
        ValueBindingSourceKind.Literal => Literal is { ValueKind: not JsonValueKind.Undefined },
        _ => Resource is not null
    };

    public override string ToString() => $"{Kind}:[REDACTED]";
}
