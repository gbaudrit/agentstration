namespace Agentstration.Web.Security;

/// <summary>Transport-safe principal projection attached to the current HTTP request.</summary>
public sealed record ResolvedPrincipalFeature(Guid PrincipalId, string DisplayName);
