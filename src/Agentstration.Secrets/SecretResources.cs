using System.Text.Json;
using System.Text.Json.Serialization;
using Agentstration.Resources;

namespace Agentstration.Secrets;

public static class SecretResourceKinds
{
    public const string Secret = "Secret";
    public const string Vault = "Vault";
}

public enum SecretType { [JsonStringEnumMemberName("opaque")] Opaque }

public sealed record VaultProperties
{
    public required string DisplayName { get; init; }
    public required string ProviderType { get; init; }
    public IReadOnlyDictionary<string, JsonElement> ProviderOptions { get; init; } = new Dictionary<string, JsonElement>();
}

public sealed record VaultResource : Resource
{
    public VaultProperties Definition { get; init; } = null!;
}

public sealed record SecretProperties
{
    public required string DisplayName { get; init; }
    public string? Description { get; init; }
    public required ResourceReference Vault { get; init; }
    public required string Key { get; init; }
    public SecretType SecretType { get; init; } = SecretType.Opaque;
}

public sealed record SecretResource : Resource
{
    public SecretProperties Definition { get; init; } = null!;
}
