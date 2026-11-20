using Agentstration.Flows;
using Agentstration.Flows.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;

namespace Agentstration.Web.Console;

public static class FlowYamlPresentation
{
    public static string ToYaml(FlowResponse resource) => ResourceManifestSerializer.ToYaml(new
    {
        ApiVersion = ResourceApiVersions.CoreV1,
        Kind = FlowResourceKinds.Flow,
        Metadata = new { resource.Namespace, Name = resource.Id },
        resource.ScopeRef,
        Definition = new
        {
            DisplayName = resource.DisplayName ?? resource.Name,
            resource.Description,
            resource.Version,
            resource.Enabled,
            Spec = resource.Definition,
            resource.Metadata,
            resource.Graph
        }
    });
}
