using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentstration.Resources;
using Agentstration.Secrets.Abstractions;
using Agentstration.Secrets.Local;

namespace Agentstration.Management.Tests;

[TestClass]
[DoNotParallelize]
public sealed class LocalSecretVaultTests
{
    [TestMethod]
    public async Task MasterKeyInitializationCreatesOnceAndNeverOverwrites()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"agentstration-master-key-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "master.key");
        var previousFile = Environment.GetEnvironmentVariable("AGENTSTRATION_MASTER_KEY_FILE");
        var previousKey = Environment.GetEnvironmentVariable("AGENTSTRATION_MASTER_KEY");
        try
        {
            Environment.SetEnvironmentVariable("AGENTSTRATION_MASTER_KEY_FILE", null);
            Environment.SetEnvironmentVariable("AGENTSTRATION_MASTER_KEY", null);
            var provider = new EnvironmentMasterKeyProvider(path);

            var first = await provider.InitializeAsync();
            var originalContents = await File.ReadAllTextAsync(path);
            var second = await provider.InitializeAsync();

            Assert.IsTrue(first.Created);
            Assert.AreEqual(Path.GetFullPath(path), first.KeyFilePath);
            Assert.IsFalse(second.Created);
            Assert.AreEqual(originalContents, await File.ReadAllTextAsync(path));
            Assert.HasCount(32, Convert.FromBase64String(originalContents.Trim()));
            var loadedKey = await provider.GetKeyAsync();
            Assert.HasCount(32, loadedKey);
            CryptographicOperations.ZeroMemory(loadedKey);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AGENTSTRATION_MASTER_KEY_FILE", previousFile);
            Environment.SetEnvironmentVariable("AGENTSTRATION_MASTER_KEY", previousKey);
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [TestMethod]
    public async Task EncryptsRoundTripsAndUsesUniqueNonce()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"agentstration-secret-{Guid.NewGuid():N}");
        try
        {
            var provider = new LocalSecretVaultProvider(directory, new FixedKey(RandomNumberGenerator.GetBytes(32)));
            var context = Context();
            using var value = new SecretValue(Encoding.UTF8.GetBytes("sensitive-value"));
            await provider.SetAsync(context, "api-key", value);
            var first = Directory.GetFiles(directory).Single();
            var firstPayload = await File.ReadAllTextAsync(first);
            await provider.SetAsync(context, "api-key", value);
            var secondPayload = await File.ReadAllTextAsync(first);
            using var resolved = await provider.GetAsync(context, "api-key");
            Assert.IsNotNull(resolved);
            Assert.AreEqual("sensitive-value", Encoding.UTF8.GetString(resolved.AccessValue().Span));
            Assert.AreNotEqual(firstPayload, secondPayload);
            Assert.AreEqual("[REDACTED]", resolved.ToString());
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [TestMethod]
    public async Task RejectsWrongKeyAndTamperedCiphertext()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"agentstration-secret-{Guid.NewGuid():N}");
        try
        {
            var context = Context();
            var key = RandomNumberGenerator.GetBytes(32);
            var provider = new LocalSecretVaultProvider(directory, new FixedKey(key));
            using var value = new SecretValue(Encoding.UTF8.GetBytes("secret"));
            await provider.SetAsync(context, "key", value);
            var wrong = new LocalSecretVaultProvider(directory, new FixedKey(RandomNumberGenerator.GetBytes(32)));
            await Assert.ThrowsExactlyAsync<AuthenticationTagMismatchException>(() => wrong.GetAsync(context, "key"));
            var path = Directory.GetFiles(directory).Single();
            var payload = JsonSerializer.Deserialize<LocalSecretPayload>(await File.ReadAllTextAsync(path))!;
            var cipher = Convert.FromBase64String(payload.CipherText);
            cipher[0] ^= 0xff;
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(payload with { CipherText = Convert.ToBase64String(cipher) }));
            await Assert.ThrowsAsync<Exception>(() => provider.GetAsync(context, "key"));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [TestMethod]
    public async Task SharedKeyFileVaultIsReadOnlyAndReturnsAValidatedToken()
    {
        var path = Path.GetTempFileName();
        var token = new string('k', SharedKeyFileSecretVaultProvider.MinimumTokenBytes);
        await File.WriteAllTextAsync(path, token + "\n");
        try
        {
            var provider = new SharedKeyFileSecretVaultProvider();
            var context = new SecretVaultContext(
                ResourceScopeRef.Instance,
                ResourceAddress.Create(ResourceNamespace.Default, "Vault", "shared-key"),
                new Dictionary<string, JsonElement> { ["path"] = JsonSerializer.SerializeToElement(path) });

            using var value = await provider.GetAsync(context, "token");

            Assert.IsNotNull(value);
            Assert.AreEqual(token, Encoding.UTF8.GetString(value.AccessValue().Span));
            Assert.AreEqual(SecretValueStatus.Configured, await provider.GetStatusAsync(context, "token"));
            await Assert.ThrowsExactlyAsync<NotSupportedException>(() => provider.SetAsync(context, "token", value));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static SecretVaultContext Context() => new(ResourceScopeRef.Workspace(Guid.NewGuid()), ResourceAddress.Create(ResourceNamespace.Default, "Vault", "local"), new Dictionary<string, System.Text.Json.JsonElement>());
    private sealed class FixedKey(byte[] key) : IMasterKeyProvider { public ValueTask<byte[]> GetKeyAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(key.ToArray()); }
}
