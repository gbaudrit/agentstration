using Agentstration.Parameters;
using Agentstration.Resources;

namespace Agentstration.Parameters.Contracts;

public sealed record CreateParameterRequest(string Name, ParameterProperties Properties, ResourceScopeRef? ScopeRef = null);
public sealed record PutParameterRequest(ParameterProperties Properties);
public sealed record ParameterUsageResponse(string ResourceType, string Name, string DisplayName, string Url);
public sealed record ParameterUsagesResponse(IReadOnlyList<ParameterUsageResponse> Value, int Count);
