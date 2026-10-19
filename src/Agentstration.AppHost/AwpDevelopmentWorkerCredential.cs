using System.Security.Cryptography;
using System.Text;

namespace Agentstration.AppHost;

public sealed record AwpDevelopmentWorkerCredential(Guid WorkerId, Guid CredentialId, string SharedKeyFile);

public static class AwpDevelopmentWorkerCredentials
{
    public static IReadOnlyList<AwpDevelopmentWorkerCredential> Provision(
        string directory,
        string instanceId,
        int count)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        if (count is < 1 or > 16) throw new ArgumentOutOfRangeException(nameof(count));
        return Enumerable.Range(1, count).Select(index => ProvisionOne(directory, instanceId, index)).ToArray();
    }

    private static AwpDevelopmentWorkerCredential ProvisionOne(string directory, string instanceId, int index)
    {
        var workerId = StableGuid($"{instanceId}\nawp-worker\n{index}");
        var credentialId = StableGuid($"{instanceId}\nawp-worker-credential\n{index}");
        var workerDirectory = Path.Combine(directory, workerId.ToString("N"));
        Directory.CreateDirectory(workerDirectory);
        var path = Path.Combine(workerDirectory, "primary.key");
        if (!File.Exists(path))
        {
            var secret = RandomNumberGenerator.GetBytes(48);
            try
            {
                var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
                try
                {
                    File.WriteAllText(temporary, Convert.ToBase64String(secret) + Environment.NewLine);
                    if (!OperatingSystem.IsWindows())
                        File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                    try { File.Move(temporary, path, overwrite: false); }
                    catch (IOException) when (File.Exists(path)) { }
                }
                finally
                {
                    if (File.Exists(temporary)) File.Delete(temporary);
                }
            }
            finally { CryptographicOperations.ZeroMemory(secret); }
        }
        return new(workerId, credentialId, path);
    }

    private static Guid StableGuid(string value)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(value), hash);
        return new Guid(hash[..16]);
    }
}
