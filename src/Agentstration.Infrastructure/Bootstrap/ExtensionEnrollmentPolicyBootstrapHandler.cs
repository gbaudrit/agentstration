using System.Text.Json;
using Agentstration.Extensions.Aep;
using Agentstration.Extensions.Contracts;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;

namespace Agentstration.Infrastructure.Bootstrap;

public sealed class ExtensionEnrollmentPolicyBootstrapHandler(ExtensionEnrollmentPolicyService policies) : IBootstrapResourceHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public string Kind => ExtensionKinds.ExtensionEnrollmentPolicy;
    public BootstrapProfileScope Scope => BootstrapProfileScope.Instance;

    public async Task<BootstrapResourcePlanResult> PlanAsync(
        BootstrapResourceDocument resource, BootstrapResourceOperationContext operation,
        BootstrapPlanningContext planning, CancellationToken cancellationToken)
    {
        var definition = Read(resource);
        if (await policies.GetAsync(resource.Metadata.Name, cancellationToken) is not null)
            return new(BootstrapResourceDisposition.Skip);
        var selector = $"{definition.ExtensionId}:{definition.InstanceId:D}";
        if (planning.Contains(Kind, selector)
            || (await policies.ListAsync(cancellationToken)).Any(value =>
                ExtensionEnrollmentPolicyService.SameSelector(value.Value.Definition, definition)))
            throw new AepEnrollmentException("enrollment_policy_conflict", "Several policies select the same extension installation.", 409);
        planning.Register(Kind, selector, document: resource);
        return new(BootstrapResourceDisposition.Create);
    }

    public async Task<BootstrapResourceApplyResult> ApplyAsync(
        BootstrapResourceDocument resource, BootstrapResourceOperationContext operation, CancellationToken cancellationToken)
    {
        var definition = Read(resource);
        if (await policies.GetAsync(resource.Metadata.Name, cancellationToken) is not null)
            return BootstrapResourceApplyResult.Skipped;
        await policies.CreateAsync(new ExtensionEnrollmentPolicyResource
        {
            ApiVersion = resource.ApiVersion,
            Kind = Kind,
            Metadata = resource.Metadata,
            ScopeRef = ResourceScopeRef.Instance,
            Definition = definition
        }, cancellationToken);
        return BootstrapResourceApplyResult.Created;
    }

    private static ExtensionEnrollmentPolicyProperties Read(BootstrapResourceDocument resource)
    {
        var definition = resource.Definition.Deserialize<ExtensionEnrollmentPolicyProperties>(JsonOptions)
            ?? throw new AepEnrollmentException("enrollment_policy_invalid", "An enrollment policy definition is required.");
        ExtensionEnrollmentPolicyService.Validate(definition);
        if (resource.Metadata.Namespace is { } ns && ns != ResourceNamespace.Default)
            throw new AepEnrollmentException("enrollment_policy_invalid", "Enrollment policies use the default instance namespace.");
        return definition;
    }
}
