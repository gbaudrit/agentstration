namespace Agentstration.Management.Contracts;

public sealed record ImportSourceYamlRequest(string Manifest);
public sealed record ImportSourceUrlRequest(string Url);
public sealed record UpdateSourceDisplayNameRequest(string DisplayName);
