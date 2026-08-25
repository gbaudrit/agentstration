using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentstration.Secrets.Abstractions;

namespace Agentstration.Secrets.Local;

public sealed class EnvironmentMasterKeyProvider(string defaultKeyFilePath) : IMasterKeyProvider, IMasterKeyInitializer
{
    public async ValueTask<byte[]> GetKeyAsync(CancellationToken cancellationToken = default)
    {
        var file = ConfiguredKeyFile();
        var encoded = File.Exists(file)
            ? await File.ReadAllTextAsync(file, cancellationToken)
            : Environment.GetEnvironmentVariable("AGENTSTRATION_MASTER_KEY");
        if (string.IsNullOrWhiteSpace(encoded)) throw new InvalidOperationException("Configure AGENTSTRATION_MASTER_KEY_FILE with a Base64-encoded 256-bit key.");
        byte[] key;
        try { key = Convert.FromBase64String(encoded.Trim()); }
        catch (FormatException exception) { throw new InvalidOperationException("The Agentstration master key must be Base64 encoded.", exception); }
        if (key.Length != 32) throw new InvalidOperationException("The Agentstration master key must contain exactly 32 bytes.");
        return key;
    }

    public async Task<SecretVaultInitializationResult> InitializeAsync(CancellationToken cancellationToken = default)
    {
        var path = ConfiguredKeyFile();
        if (File.Exists(path) || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AGENTSTRATION_MASTER_KEY")))
            return new(false, Path.GetFullPath(path));
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        var key = RandomNumberGenerator.GetBytes(32);
        var encoded = Encoding.UTF8.GetBytes(Convert.ToBase64String(key) + Environment.NewLine);
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.Asynchronous | FileOptions.WriteThrough };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using var stream = new FileStream(path, options);
            await stream.WriteAsync(encoded, cancellationToken);
            await stream.FlushAsync(cancellationToken);
            return new(true, Path.GetFullPath(path));
        }
        catch (IOException) when (File.Exists(path)) { return new(false, Path.GetFullPath(path)); }
        finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(encoded); }
    }

    private string ConfiguredKeyFile() => Environment.GetEnvironmentVariable("AGENTSTRATION_MASTER_KEY_FILE") is { Length: > 0 } configured ? configured : defaultKeyFilePath;
}

public sealed record LocalSecretPayload(int EncryptionVersion, string CipherText, string Nonce, string Tag);

public sealed class LocalSecretVaultProvider(string rootPath, IMasterKeyProvider masterKeys) : ISecretVaultProvider, ISecretVaultInitializer
{
    public const string Type = "local";
    public string ProviderType => Type;
    Task<SecretVaultInitializationResult> ISecretVaultInitializer.InitializeAsync(SecretVaultContext context, CancellationToken cancellationToken) =>
        masterKeys is IMasterKeyInitializer initializer ? initializer.InitializeAsync(cancellationToken) : throw new InvalidOperationException("The configured master key provider cannot initialize a key file.");

    public async Task<string> GetHealthAsync(SecretVaultContext context, CancellationToken cancellationToken = default)
    {
        try { var key = await masterKeys.GetKeyAsync(cancellationToken); CryptographicOperations.ZeroMemory(key); return "available"; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException) { return "unavailable"; }
    }

    public Task<SecretValueStatus> GetStatusAsync(SecretVaultContext context, string key, CancellationToken cancellationToken = default) =>
        Task.FromResult(File.Exists(PathFor(context, key)) ? SecretValueStatus.Configured : SecretValueStatus.Missing);

    public async Task<SecretValue?> GetAsync(SecretVaultContext context, string key, CancellationToken cancellationToken = default)
    {
        var path = PathFor(context, key);
        if (!File.Exists(path)) return null;
        var payload = JsonSerializer.Deserialize<LocalSecretPayload>(await File.ReadAllTextAsync(path, cancellationToken))
            ?? throw new CryptographicException("The encrypted secret payload is invalid.");
        if (payload.EncryptionVersion != 1) throw new CryptographicException($"Unsupported secret encryption version '{payload.EncryptionVersion}'.");
        var cipher = Convert.FromBase64String(payload.CipherText);
        var nonce = Convert.FromBase64String(payload.Nonce);
        var tag = Convert.FromBase64String(payload.Tag);
        var clear = new byte[cipher.Length];
        var masterKey = await masterKeys.GetKeyAsync(cancellationToken);
        try
        {
            using var aes = new AesGcm(masterKey, 16);
            aes.Decrypt(nonce, cipher, tag, clear, AssociatedData(context, key));
            return new SecretValue(clear);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(masterKey);
            CryptographicOperations.ZeroMemory(clear);
        }
    }

    public async Task SetAsync(SecretVaultContext context, string key, SecretValue value, CancellationToken cancellationToken = default)
    {
        ValidateKey(key);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var clear = value.AccessValue();
        var cipher = new byte[clear.Length];
        var tag = new byte[16];
        var masterKey = await masterKeys.GetKeyAsync(cancellationToken);
        try
        {
            using var aes = new AesGcm(masterKey, tag.Length);
            aes.Encrypt(nonce, clear.Span, cipher, tag, AssociatedData(context, key));
            Directory.CreateDirectory(rootPath);
            var path = PathFor(context, key);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new LocalSecretPayload(1, Convert.ToBase64String(cipher), Convert.ToBase64String(nonce), Convert.ToBase64String(tag))), cancellationToken);
            File.Move(temporary, path, true);
        }
        finally { CryptographicOperations.ZeroMemory(masterKey); }
    }

    public Task DeleteAsync(SecretVaultContext context, string key, CancellationToken cancellationToken = default)
    {
        var path = PathFor(context, key);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    private string PathFor(SecretVaultContext context, string key)
    {
        ValidateKey(key);
        var identity = $"{context.TenantId:N}:{context.WorkspaceId:N}:{context.Vault}:{key}";
        return Path.Combine(rootPath, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))) + ".secret");
    }

    private static byte[] AssociatedData(SecretVaultContext context, string key) => Encoding.UTF8.GetBytes($"{context.TenantId:N}:{context.WorkspaceId:N}:{context.Vault}:{key}:v1");
    private static void ValidateKey(string key) { ArgumentException.ThrowIfNullOrWhiteSpace(key); if (key.Length > 256) throw new ArgumentException("Secret keys cannot exceed 256 characters.", nameof(key)); }
}
