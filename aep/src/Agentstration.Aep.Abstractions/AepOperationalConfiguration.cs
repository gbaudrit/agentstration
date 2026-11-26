using System.Text.Json;

namespace Agentstration.Aep.Abstractions;

/// <summary>Operational settings of an enrolled installation, separate from contribution options.</summary>
public static class AepOperationalConfigurationProtocol
{
    public const string Capability = "aep.operational-configuration";
    public const string Version = "1.0";
    public const string Path = "/aep/operational-configuration";
    public const int MaximumBytes = 65_536;
}

public enum AepConfigurationScope { Process, ConsumerContext, Binding }
public enum AepConfigurationApplyMode { Immediate, NewBinding, RestartRequired }
public enum AepConfigurationValueSource { Literal, Parameter, Secret }

public sealed record AepOperationalOption(
    string Id, JsonElement Schema, bool Required, AepValueProtection Protection,
    IReadOnlyList<AepConfigurationValueSource> AllowedSources,
    AepConfigurationScope Scope, AepConfigurationApplyMode ApplyMode, string? AtomicGroup = null);

public sealed record AepOperationalConfigurationDescriptor(
    string Id, string Version, string SchemaDigest, IReadOnlyList<AepOperationalOption> Options)
{
    public static AepOperationalConfigurationDescriptor Create(string id, string version, IReadOnlyList<AepOperationalOption> options) =>
        new(id, version, AepSchemaDigest.Compute(JsonSerializer.SerializeToElement(options, AepProtocol.JsonOptions)), options);

    public bool IsValid() => !string.IsNullOrWhiteSpace(Id) && Id.Length <= 256
        && !string.IsNullOrWhiteSpace(Version) && Version.Length <= 64
        && Options is { Count: > 0 and <= 32 }
        && Options.All(o => o is not null)
        && Options.Select(o => o.Id).Distinct(StringComparer.Ordinal).Count() == Options.Count
        && Options.All(o => !string.IsNullOrWhiteSpace(o.Id) && o.Id.Length <= 128
            && o.Schema.ValueKind == JsonValueKind.Object && Enum.IsDefined(o.Scope) && Enum.IsDefined(o.ApplyMode)
            && Enum.IsDefined(o.Protection) && o.AllowedSources is { Count: > 0 and <= 3 }
            && o.AllowedSources.All(s => Enum.IsDefined(s))
            && o.AllowedSources.Distinct().Count() == o.AllowedSources.Count
            && (o.Protection != AepValueProtection.Secured || o.AllowedSources.All(s => s == AepConfigurationValueSource.Secret)))
        && SchemaDigest == AepSchemaDigest.Compute(JsonSerializer.SerializeToElement(Options, AepProtocol.JsonOptions))
        && JsonSerializer.SerializeToUtf8Bytes(this, AepProtocol.JsonOptions).Length <= AepOperationalConfigurationProtocol.MaximumBytes;
}

public sealed record AepOperationalConfigurationState(
    AepOperationalConfigurationDescriptor Descriptor, string? AppliedRevision, bool Ready,
    IReadOnlyDictionary<string, JsonElement> AppliedValues);

public sealed record AepOperationalConfigurationRequest(
    string OptionSet, string Version, string SchemaDigest, Guid ConfigurationId, long Generation,
    string Revision, string? ExpectedRevision, IReadOnlyList<AepBoundValue> Values)
{
    public override string ToString() => "[REDACTED]";
}
