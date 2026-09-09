using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentstration.Secrets.Abstractions;

namespace Agentstration.Secrets.Local;

public sealed class SharedKeyFileSecretVaultProvider : ISecretVaultProvider
{
    public const string Type = "shared-key-file";
    public const int MinimumTokenBytes = 32;
    public const int MaximumFileBytes = 4096;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public string ProviderType => Type;

    public static void Validate(string path)
    {
        var provider = new SharedKeyFileSecretVaultProvider();
        using var value = provider.GetAsync(
            new SecretVaultContext(default, default, new Dictionary<string, JsonElement>
            {
                ["path"] = JsonSerializer.SerializeToElement(path)
            }),
            "token").GetAwaiter().GetResult();
        if (value is null) throw new InvalidOperationException($"The AEP shared key file '{path}' is absent.");
    }

    public async Task<string> GetHealthAsync(SecretVaultContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            using var value = await GetAsync(context, "token", cancellationToken);
            return value is null ? "unavailable" : "available";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return "unavailable";
        }
    }

    public async Task<SecretValueStatus> GetStatusAsync(SecretVaultContext context, string key, CancellationToken cancellationToken = default)
    {
        try
        {
            using var value = await GetAsync(context, key, cancellationToken);
            return value is null ? SecretValueStatus.Missing : SecretValueStatus.Configured;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return SecretValueStatus.Unavailable;
        }
    }

    public async Task<SecretValue?> GetAsync(SecretVaultContext context, string key, CancellationToken cancellationToken = default)
    {
        if (!string.Equals(key, "token", StringComparison.Ordinal))
            throw new InvalidDataException("A shared-key-file vault supports only the 'token' key.");
        var path = Path(context);
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > MaximumFileBytes)
            throw new InvalidDataException("The configured AEP shared key file exceeds the maximum size.");
        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new IOException("The configured AEP shared key file is unreadable.", exception);
        }
        try
        {
            if (bytes.Length > MaximumFileBytes)
                throw new InvalidDataException("The configured AEP shared key file exceeds the maximum size.");
            var length = bytes.Length;
            var hasTrailingLineFeed = length > 0 && bytes[length - 1] == (byte)'\n';
            if (hasTrailingLineFeed) length--;
            if (hasTrailingLineFeed && length > 0 && bytes[length - 1] == (byte)'\r') length--;
            if (length < MinimumTokenBytes)
                throw new InvalidDataException("The configured AEP shared key is empty or too short.");
            if (bytes.AsSpan(0, length).Contains((byte)'\r') || bytes.AsSpan(0, length).Contains((byte)'\n'))
                throw new InvalidDataException("The configured AEP shared key file must contain one line.");
            _ = StrictUtf8.GetCharCount(bytes, 0, length);
            return new SecretValue(bytes.AsSpan(0, length));
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("The configured AEP shared key file is not valid UTF-8.", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public Task SetAsync(SecretVaultContext context, string key, SecretValue value, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Shared key files are provisioned only by the orchestrator.");

    public Task DeleteAsync(SecretVaultContext context, string key, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Shared key files are managed only by the orchestrator.");

    private static string Path(SecretVaultContext context)
    {
        if (!context.Options.TryGetValue("path", out var path)
            || path.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(path.GetString()))
            throw new InvalidDataException("The shared-key-file vault requires a non-empty path option.");
        return System.IO.Path.GetFullPath(path.GetString()!);
    }
}
