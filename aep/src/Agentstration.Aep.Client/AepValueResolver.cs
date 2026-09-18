using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentstration.Aep.Abstractions;

namespace Agentstration.Aep.Client;

public sealed class AepValueResolver
{
    private readonly IReadOnlyDictionary<string, AepBoundValue> values;
    private readonly AepSecretAccessClient secrets;

    public AepValueResolver(
        IReadOnlyList<AepBoundValue>? values,
        HttpClient secretAccessClient)
    {
        ArgumentNullException.ThrowIfNull(secretAccessClient);
        var indexed = new Dictionary<string, AepBoundValue>(StringComparer.Ordinal);
        foreach (var value in values ?? [])
        {
            if (value is null || string.IsNullOrWhiteSpace(value.RequirementId))
                throw new AepProtocolException("bound_value_invalid", "A bound value identifier is required.");
            if (!indexed.TryAdd(value.RequirementId, value))
                throw new AepProtocolException("bound_value_duplicate", "A bound value identifier is duplicated.");
        }
        this.values = indexed;
        secrets = new AepSecretAccessClient(secretAccessClient);
    }

    public bool Contains(string requirementId) => values.ContainsKey(requirementId);

    public async ValueTask<AepResolvedValue> ResolveAsync(
        string requirementId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(requirementId) || !values.TryGetValue(requirementId, out var value))
            throw new AepProtocolException("bound_value_missing", "The requested bound value is not available.");
        return value.Kind switch
        {
            AepBoundValueKind.Inline when value.InlineValue is { } inline && value.SecretGrant is null =>
                AepResolvedValue.FromInline(inline),
            AepBoundValueKind.SecretGrant when value.InlineValue is null && value.SecretGrant is { } grant =>
                AepResolvedValue.FromSecret(await secrets.RedeemAsync(grant, cancellationToken)),
            _ => throw new AepProtocolException("bound_value_variant_invalid", "The requested bound value has an invalid representation.")
        };
    }
}

public sealed class AepResolvedValue : IDisposable
{
    private byte[]? secretValue;

    private AepResolvedValue(JsonElement? inlineValue, byte[]? secretValue)
    {
        InlineValue = inlineValue?.Clone();
        this.secretValue = secretValue;
    }

    public JsonElement? InlineValue { get; }
    public bool IsSecured => secretValue is not null;

    public static AepResolvedValue FromInline(JsonElement value) => new(value, null);
    public static AepResolvedValue FromSecret(byte[] value) => new(null, value);

    public string ReadString()
    {
        if (InlineValue is { ValueKind: JsonValueKind.String } inline) return inline.GetString()!;
        if (secretValue is not null) return Encoding.UTF8.GetString(secretValue);
        throw new AepProtocolException("bound_value_type_invalid", "The bound value is not a string.");
    }

    public JsonElement ReadJson()
    {
        if (InlineValue is { } inline) return inline.Clone();
        if (secretValue is null) throw new ObjectDisposedException(nameof(AepResolvedValue));
        try { return JsonSerializer.Deserialize<JsonElement>(secretValue, AepProtocol.JsonOptions); }
        catch (JsonException exception)
        {
            throw new AepProtocolException("bound_value_type_invalid", "The secured bound value is not valid JSON.", innerException: exception);
        }
    }

    public void Dispose()
    {
        if (secretValue is null) return;
        CryptographicOperations.ZeroMemory(secretValue);
        secretValue = null;
    }

    public override string ToString() => "[REDACTED]";
}
