using Agentstration.Management.Abstractions;
using Agentstration.Resources;

namespace Agentstration.Management.Core;

public sealed record ExtensionInventoryItem(
    string Key,
    string? RegistrationName,
    string RegistrationNamespace,
    ResourceScopeRef? RegistrationScopeRef,
    Guid? EnrollmentInstanceId,
    string DisplayName,
    string ExtensionId,
    string? Version,
    Uri Endpoint,
    string RegistrationSource,
    bool RegistrationEnabled,
    string AvailabilityStatus,
    AepEnrollmentState? EnrollmentStatus,
    DateTimeOffset? AnnouncedAt,
    ExtensionView? Extension,
    IReadOnlyList<ExtensionInventoryConnection> Connections);

public sealed record ExtensionInventoryConnection(
    string RegistrationName,
    string RegistrationNamespace,
    ResourceScopeRef? RegistrationScopeRef,
    string DisplayName,
    Uri Endpoint,
    string Source,
    bool Enabled,
    AepEnrollmentMode EnrollmentMode,
    string AvailabilityStatus);

public sealed class ExtensionInventoryService(
    ExtensionManagementService extensions,
    IControlPlaneStore store,
    IAuthorizationService authorization,
    IPlatformAuthorizationService platformAuthorization,
    ICurrentRequestContext requestContext)
{
    public async Task<IReadOnlyList<ExtensionInventoryItem>> ListAsync(
        RequestContext context,
        CancellationToken cancellationToken)
    {
        using var scopeContext = RequestScopes().Push(context);
        await authorization.EnsurePermissionAsync(context, AuthorizationPermissions.ResourcesRead, cancellationToken);
        var isPlatformAdministrator = await platformAuthorization.IsPlatformAdministratorAsync(
            context.PrincipalId,
            cancellationToken);

        var observed = await extensions.ListAsync(cancellationToken);
        var enrollments = (await store.ListExactAsync<AepEnrollmentRequestResource>(
                ResourceScopeRef.Instance,
                ResourceKinds.AepEnrollmentRequest,
                0,
                200,
                cancellationToken))
            .Select(value => value.Value)
            .Where(value => value.Definition.TargetScopeRef is null
                ? isPlatformAdministrator
                : IsVisibleFrom(value.Definition.TargetScopeRef, context))
            .ToArray();

        var result = new List<ExtensionInventoryItem>(observed.Count + enrollments.Length);
        foreach (var group in observed.GroupBy(value => EndpointKey(value.Endpoint), StringComparer.OrdinalIgnoreCase))
        {
            var candidates = group.ToArray();
            var enrollment = enrollments
                .Where(value => string.Equals(EndpointKey(value.Definition.Endpoint), group.Key, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(value => value.Definition.AnnouncedAt)
                .FirstOrDefault();
            var primary = candidates
                .OrderByDescending(value => enrollment is not null
                    && string.Equals(value.RegistrationName, enrollment.Definition.RegistrationName, StringComparison.Ordinal)
                    && value.RegistrationScopeRef == enrollment.ScopeRef)
                .ThenByDescending(value => string.Equals(value.Status, "available", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(value => value.RegistrationEnabled)
                .ThenByDescending(value => value.EnrollmentMode == AepEnrollmentMode.PairingCode)
                .ThenBy(value => value.RegistrationName, StringComparer.Ordinal)
                .First();
            var identity = candidates
                .OrderByDescending(value => string.Equals(value.Status, "available", StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault(value => value.Extension is not null);
            var availability = AggregateAvailability(candidates);
            var extension = identity is null ? primary : primary with
            {
                Status = availability,
                Extension = identity.Extension,
                Contributions = identity.Contributions,
                OptionSets = identity.OptionSets,
                Usages = identity.Usages,
                Providers = candidates.SelectMany(value => value.Providers).Distinct().ToArray(),
                Details = identity.Details
            };
            result.Add(new ExtensionInventoryItem(
                RegistrationKey(primary.RegistrationScopeRef, primary.RegistrationNamespace, primary.RegistrationName),
                primary.RegistrationName,
                primary.RegistrationNamespace,
                primary.RegistrationScopeRef,
                enrollment?.Definition.InstanceId,
                identity?.Extension?.Name ?? enrollment?.Definition.ExtensionName ?? primary.RegistrationDisplayName,
                identity?.Extension?.Id ?? enrollment?.Definition.ExtensionId ?? primary.RegistrationName,
                identity?.Extension?.Version ?? enrollment?.Definition.ExtensionVersion,
                primary.Endpoint,
                enrollment is not null ? EnrollmentSource(enrollment.Definition.EnrollmentMode) : primary.DiscoverySource,
                primary.RegistrationEnabled,
                availability,
                enrollment?.Definition.State,
                enrollment?.Definition.AnnouncedAt,
                extension,
                VisibleConnections(candidates, enrollment).Select(ToConnection).ToArray()));
        }

        var linkedInstances = result
            .Where(value => value.EnrollmentInstanceId.HasValue)
            .Select(value => value.EnrollmentInstanceId!.Value)
            .ToHashSet();
        foreach (var enrollment in enrollments.Where(value => !linkedInstances.Contains(value.Definition.InstanceId)))
        {
            result.Add(new ExtensionInventoryItem(
                EnrollmentKey(enrollment.Definition.InstanceId),
                null,
                ResourceNamespace.DefaultValue,
                enrollment.ScopeRef,
                enrollment.Definition.InstanceId,
                enrollment.Definition.ExtensionName,
                enrollment.Definition.ExtensionId,
                enrollment.Definition.ExtensionVersion,
                enrollment.Definition.Endpoint,
                EnrollmentSource(enrollment.Definition.EnrollmentMode),
                false,
                "unregistered",
                enrollment.Definition.State,
                enrollment.Definition.AnnouncedAt,
                null,
                []));
        }

        return result
            .OrderBy(value => value.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(value => value.Key, StringComparer.Ordinal)
            .ToArray();
    }

    public static string RegistrationKey(ResourceScopeRef? scopeRef, string @namespace, string name) =>
        $"registration:{scopeRef?.Value ?? "visible"}:{@namespace}/{name}";
    public static string EnrollmentKey(Guid instanceId) => $"enrollment:{instanceId:N}";

    private static ExtensionInventoryConnection ToConnection(ExtensionView extension) => new(
        extension.RegistrationName,
        extension.RegistrationNamespace,
        extension.RegistrationScopeRef,
        extension.RegistrationDisplayName,
        extension.Endpoint,
        extension.EnrollmentMode == AepEnrollmentMode.PairingCode ? "pairingCode" : extension.DiscoverySource,
        extension.RegistrationEnabled,
        extension.EnrollmentMode,
        extension.Status);

    private static string AggregateAvailability(IReadOnlyList<ExtensionView> extensions)
    {
        foreach (var status in new[] { "available", "incompatible", "unavailable", "unknown", "disabled" })
            if (extensions.Any(value => string.Equals(value.Status, status, StringComparison.OrdinalIgnoreCase))) return status;
        return extensions[0].Status;
    }

    private static string EnrollmentSource(AepEnrollmentMode mode) => mode switch
    {
        AepEnrollmentMode.PairingCode => "pairingCode",
        AepEnrollmentMode.SharedKeyFile => "sharedKeyFile",
        _ => "enrollment"
    };

    private static bool IsVisibleFrom(ResourceScopeRef? targetScopeRef, RequestContext context) => targetScopeRef switch
    {
        { Kind: ResourceScopeKind.Instance } => true,
        { Kind: ResourceScopeKind.Tenant, TargetId: var targetId } => targetId == context.TenantId,
        { Kind: ResourceScopeKind.Workspace, TargetId: var targetId } => targetId == context.WorkspaceId,
        _ => false
    };

    private static IEnumerable<ExtensionView> VisibleConnections(
        IReadOnlyList<ExtensionView> candidates,
        AepEnrollmentRequestResource? enrollment)
    {
        if (string.IsNullOrWhiteSpace(enrollment?.Definition.RegistrationName)) return candidates;
        return candidates.Where(value =>
            string.Equals(value.RegistrationName, enrollment.Definition.RegistrationName, StringComparison.Ordinal)
            || value.DiscoverySource is not ("configuration" or "aspire"));
    }

    private static string EndpointKey(Uri endpoint) => endpoint.GetComponents(
        UriComponents.SchemeAndServer | UriComponents.Path,
        UriFormat.Unescaped).TrimEnd('/');

    private IRequestContextScopeFactory RequestScopes() => requestContext as IRequestContextScopeFactory
        ?? throw new InvalidOperationException("Extension inventory requires a mutable Control Plane request context.");
}
