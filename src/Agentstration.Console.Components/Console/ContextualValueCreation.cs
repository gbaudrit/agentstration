using Agentstration.Parameters;
using Agentstration.Resources;

namespace Agentstration.Web.Console;

public sealed record ContextualValueCreationContext(
    ResourceScopeRef ScopeRef,
    string SuggestedName,
    string SuggestedDisplayName,
    string? Description = null,
    ResourceNamespace? Namespace = null,
    ParameterValueType? ValueType = null,
    string? Format = null)
{
    public ResourceNamespace EffectiveNamespace => Namespace ?? ResourceNamespace.Default;
}
