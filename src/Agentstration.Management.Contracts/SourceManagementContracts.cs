using Agentstration.Resources;

namespace Agentstration.Management.Contracts;

public sealed record ImportSourceYamlRequest(string Manifest, ResourceScopeRef? ScopeRef = null);
public sealed record ImportSourceUrlRequest(string Url, ResourceScopeRef? ScopeRef = null);
public sealed record UpdateSourceDisplayNameRequest(string DisplayName);
