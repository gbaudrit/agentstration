using Agentstration.Resources;

namespace Agentstration.Extensions.Contracts;

public sealed record ExtensionEnrollmentPolicyProperties
{
    public required string ExtensionId { get; init; }
    public Guid? InstanceId { get; init; }
    // A null destination explicitly keeps this selector's assignment manual.
    public string? AutoEnrollmentLevel { get; init; }
}

public sealed record ExtensionEnrollmentPolicyResource : Resource
{
    public required ExtensionEnrollmentPolicyProperties Definition { get; init; }
}
