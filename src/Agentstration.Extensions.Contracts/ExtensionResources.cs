using System.Text.Json.Serialization;
using Agentstration.Resources;

namespace Agentstration.Extensions.Contracts;

public static class ExtensionKinds
{
    public const string ExtensionRegistration = "ExtensionRegistration";
    public const string ExtensionBundleInstallation = "ExtensionBundleInstallation";
    public const string AepEnrollmentSettings = "AepEnrollmentSettings";
    public const string AepEnrollmentRequest = "AepEnrollmentRequest";
}

[JsonConverter(typeof(JsonStringEnumConverter<ExtensionBundleInstallationState>))]
public enum ExtensionBundleInstallationState
{
    [JsonStringEnumMemberName("installing")] Installing,
    [JsonStringEnumMemberName("installed")] Installed,
    [JsonStringEnumMemberName("failed")] Failed,
    [JsonStringEnumMemberName("degraded")] Degraded
}

public sealed record ExtensionManagedResource
{
    public required string Kind { get; init; }
    public required ResourceNamespace Namespace { get; init; }
    public required string Name { get; init; }
    public required ResourceScopeRef ScopeRef { get; init; }
    public Guid? Uid { get; init; }
    public long? Generation { get; init; }
    public string? Version { get; init; }
    public string? DefinitionHash { get; init; }
    public bool Published { get; init; }
}

public sealed record ExtensionBundleInstallationProperties
{
    public required ResourceReference Registration { get; init; }
    public required string ExtensionId { get; init; }
    public required string ExtensionVersion { get; init; }
    public required string BundleId { get; init; }
    public required string BundleVersion { get; init; }
    public required string BundleDigest { get; init; }
    public required ExtensionBundleInstallationState State { get; init; }
    public IReadOnlyDictionary<string, ResourceReference> ToolBindings { get; init; } =
        new Dictionary<string, ResourceReference>();
    public IReadOnlyList<ExtensionManagedResource> ManagedResources { get; init; } = [];
    public string? Diagnostic { get; init; }
}

public sealed record ExtensionBundleInstallationResource : Resource
{
    public ExtensionBundleInstallationProperties Definition { get; init; } = null!;
}

public enum ExtensionRegistrationSource
{
    [JsonStringEnumMemberName("manual")] Manual,
    [JsonStringEnumMemberName("configuration")] Configuration,
    [JsonStringEnumMemberName("aspire")] Aspire
}

[JsonConverter(typeof(JsonStringEnumConverter<AepTransportAuthenticationMode>))]
public enum AepTransportAuthenticationMode
{
    [JsonStringEnumMemberName("none")] None,
    [JsonStringEnumMemberName("staticBearer")] StaticBearer
}

[JsonConverter(typeof(JsonStringEnumConverter<AepEnrollmentMode>))]
public enum AepEnrollmentMode
{
    [JsonStringEnumMemberName("disabled")] Disabled,
    [JsonStringEnumMemberName("pairingCode")] PairingCode,
    [JsonStringEnumMemberName("sharedKeyFile")] SharedKeyFile
}

public sealed record AepEnrollmentSettingsProperties
{
    public bool PairingCodeEnabled { get; init; } = true;
    public bool SharedKeyFileEnabled { get; init; } = true;
}

public sealed record AepEnrollmentSettingsResource : Resource
{
    public AepEnrollmentSettingsProperties Definition { get; init; } = new();
}

public sealed record ExtensionRegistrationProperties
{
    public required string DisplayName { get; init; }
    public required Uri Endpoint { get; init; }
    public bool Enabled { get; init; } = true;
    public string? ExpectedExtensionId { get; init; }
    public ExtensionRegistrationSource Source { get; init; } = ExtensionRegistrationSource.Manual;
    public AepTransportAuthenticationMode AuthenticationMode { get; init; }
    public AepEnrollmentMode EnrollmentMode { get; init; }
    public ResourceReference? Credential { get; init; }
}

public sealed record ExtensionRegistrationResource : Resource
{
    public ExtensionRegistrationProperties Definition { get; init; } = null!;
}

[JsonConverter(typeof(JsonStringEnumConverter<AepEnrollmentState>))]
public enum AepEnrollmentState
{
    [JsonStringEnumMemberName("unpaired")] Unpaired,
    [JsonStringEnumMemberName("pending")] Pending,
    [JsonStringEnumMemberName("codeIssued")] CodeIssued,
    [JsonStringEnumMemberName("credentialIssued")] CredentialIssued,
    [JsonStringEnumMemberName("verifying")] Verifying,
    [JsonStringEnumMemberName("available")] Available,
    [JsonStringEnumMemberName("expired")] Expired,
    [JsonStringEnumMemberName("attemptsExceeded")] AttemptsExceeded,
    [JsonStringEnumMemberName("rejected")] Rejected,
    [JsonStringEnumMemberName("cancelled")] Cancelled,
    [JsonStringEnumMemberName("verificationFailed")] VerificationFailed,
    [JsonStringEnumMemberName("unenrolling")] Unenrolling,
    [JsonStringEnumMemberName("revoked")] Revoked,
    [JsonStringEnumMemberName("disabled")] Disabled
}

public sealed record AepEnrollmentRequestProperties
{
    public required Guid InstanceId { get; init; }
    public ResourceScopeRef? TargetScopeRef { get; init; }
    public Guid? TargetTenantId { get; init; }
    public required string ExtensionId { get; init; }
    public required string ExtensionName { get; init; }
    public required string ExtensionVersion { get; init; }
    public required Uri Endpoint { get; init; }
    public Uri? PairingUri { get; init; }
    public AepEnrollmentMode EnrollmentMode { get; init; } = AepEnrollmentMode.PairingCode;
    public AepEnrollmentState State { get; init; }
    public DateTimeOffset AnnouncedAt { get; init; }
    public DateTimeOffset? CodeIssuedAt { get; init; }
    public DateTimeOffset? CodeExpiresAt { get; init; }
    public int AttemptCount { get; init; }
    public string? CodeSalt { get; init; }
    public string? CodeDigest { get; init; }
    public string? CompletionDigest { get; init; }
    public string? CredentialSecretName { get; init; }
    public string? RegistrationName { get; init; }
    public string? Outcome { get; init; }
}

public sealed record AepEnrollmentRequestResource : Resource
{
    public AepEnrollmentRequestProperties Definition { get; init; } = null!;
}
