using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentstration.Identity.Contracts;
using Microsoft.Extensions.Options;

namespace Agentstration.Runtime.Worker.MicrosoftAgentFramework;

internal sealed record AwpClientCredential(Guid WorkerId, Guid CredentialId, string InstanceId, byte[] Secret) : IDisposable
{
    public void Dispose() => CryptographicOperations.ZeroMemory(Secret);
}

internal sealed class AwpWorkerCredentialProvider(
    IOptions<RuntimeWorkerOptions> options,
    ILogger<AwpWorkerCredentialProvider> logger)
{
    public async Task<AwpClientCredential> LoadAsync(CancellationToken cancellationToken)
    {
        var configured = options.Value;
        if (configured.CredentialId != Guid.Empty && !string.IsNullOrWhiteSpace(configured.SharedKeyFile))
            return new(configured.WorkerId, configured.CredentialId, configured.InstanceId,
                await ReadSecretAsync(configured.SharedKeyFile, cancellationToken));

        var statePath = Path.GetFullPath(configured.CredentialStateFile);
        if (File.Exists(statePath))
        {
            var state = JsonSerializer.Deserialize<StoredCredential>(
                await File.ReadAllTextAsync(statePath, cancellationToken), AwpProtocolJson.Options)
                ?? throw new InvalidOperationException("The AWP Worker credential state is invalid.");
            if (state.WorkerId != configured.WorkerId || state.CredentialId == Guid.Empty
                || string.IsNullOrWhiteSpace(state.InstanceId) || string.IsNullOrWhiteSpace(state.Secret))
                throw new InvalidOperationException("The AWP Worker credential state does not match this Worker.");
            return new(state.WorkerId, state.CredentialId, state.InstanceId, Encoding.UTF8.GetBytes(state.Secret));
        }

        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation("Announcing AWP Worker {WorkerId} for pairing", configured.WorkerId);
        using var enrollment = new HttpClient { BaseAddress = new Uri(configured.AuthorityUrl, UriKind.Absolute) };
        using var announced = await enrollment.PostAsJsonAsync("/api/awp/enrollment/announce",
            new AnnounceAwpWorkerRequest(configured.WorkerId, configured.DisplayName, AwpWorkerAuthentication.ProtocolVersion),
            AwpProtocolJson.Options, cancellationToken);
        await EnsureSuccessAsync(announced, cancellationToken);

        var code = await ReadPairingCodeAsync(configured, cancellationToken);
        using var claimed = await enrollment.PostAsJsonAsync("/api/awp/enrollment/claim",
            new ClaimAwpWorkerRequest(configured.WorkerId, code), AwpProtocolJson.Options, cancellationToken);
        await EnsureSuccessAsync(claimed, cancellationToken);
        var credential = await claimed.Content.ReadFromJsonAsync<AwpWorkerCredential>(AwpProtocolJson.Options, cancellationToken)
            ?? throw new InvalidOperationException("The AWP Worker enrollment response is empty.");
        var stored = new StoredCredential(credential.WorkerId, credential.CredentialId, credential.InstanceId, credential.Secret);
        await PersistAsync(statePath, stored, cancellationToken);
        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation("Stored the AWP Worker credential for {WorkerId} at {CredentialStateFile}",
                configured.WorkerId, statePath);
        return new(credential.WorkerId, credential.CredentialId, credential.InstanceId,
            Encoding.UTF8.GetBytes(credential.Secret));
    }

    private static async Task<string> ReadPairingCodeAsync(RuntimeWorkerOptions options, CancellationToken cancellationToken)
    {
        string? code;
        if (!string.IsNullOrWhiteSpace(options.PairingCodeFile))
            code = (await File.ReadAllTextAsync(Path.GetFullPath(options.PairingCodeFile), cancellationToken)).Trim();
        else
        {
            Console.Write("AWP pairing code: ");
            code = await Console.In.ReadLineAsync(cancellationToken);
        }
        return !string.IsNullOrWhiteSpace(code) && code.Length <= 64
            ? code
            : throw new InvalidOperationException("A valid AWP Worker pairing code is required.");
    }

    private static async Task<byte[]> ReadSecretAsync(string configuredPath, CancellationToken cancellationToken)
    {
        var path = Path.GetFullPath(configuredPath);
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        var length = bytes.Length;
        while (length > 0 && bytes[length - 1] is (byte)'\r' or (byte)'\n') length--;
        if (length < 32 || length > 4096 || bytes.AsSpan(0, length).Contains((byte)'\r')
            || bytes.AsSpan(0, length).Contains((byte)'\n'))
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw new InvalidOperationException("The AWP Worker shared-key file is invalid.");
        }
        if (length == bytes.Length) return bytes;
        var result = bytes.AsSpan(0, length).ToArray();
        CryptographicOperations.ZeroMemory(bytes);
        return result;
    }

    private static async Task PersistAsync(string path, StoredCredential credential, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(credential, AwpProtocolJson.Options), cancellationToken);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temporary, path, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var detail = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new InvalidOperationException($"AWP Worker enrollment failed with HTTP {(int)response.StatusCode}: {detail}");
    }

    private sealed record StoredCredential(Guid WorkerId, Guid CredentialId, string InstanceId, string Secret);
}

internal static class AwpProtocolJson
{
    public static JsonSerializerOptions Options => Agentstration.Awp.Abstractions.AwpProtocol.JsonOptions;
}
