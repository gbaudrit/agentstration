using Agentstration.ResourceManagement.Contracts;

namespace Agentstration.ConnectedServices.Contracts;

/// <summary>Validates the exact ownership link before selecting a service's live access policy.</summary>
public static class ConnectedServiceOperationAccessPolicy
{
    public static ResourceAccessIdentity? GetSource(
        ConnectedServiceOperationResource operation, ConnectedServiceResource service)
    {
        var reference = operation.Definition.Service;
        return service.Definition.ExposeOperationsToDescendants
            && service.Kind == ConnectedServiceResourceKinds.ConnectedService
            && operation.Kind == ConnectedServiceResourceKinds.ConnectedServiceOperation
            && service.ScopeRef is { } owner && operation.ScopeRef == owner
            && operation.Namespace == service.Namespace
            && reference.ServiceUid == service.Uid
            && reference.Service.ScopeRef == owner
            && reference.Service.Address == service.Address
                ? new(service.Uid, owner, service.Namespace, service.Kind, service.Generation)
                : null;
    }
}
