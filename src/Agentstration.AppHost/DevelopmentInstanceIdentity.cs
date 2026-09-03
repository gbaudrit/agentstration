using System.Security.Cryptography;

namespace Agentstration.AppHost;

internal static class DevelopmentInstanceIdentity
{
    private const int GeneratedLength = 12;
    private const int MaximumLength = 32;

    public static string Resolve(string? configuredInstanceId, string worktreeRoot)
    {
        if (!string.IsNullOrWhiteSpace(configuredInstanceId))
            return Validate(configuredInstanceId.Trim());

        var stateDirectory = Path.Combine(worktreeRoot, ".agentstration");
        var identityPath = Path.Combine(stateDirectory, "instance-id");
        Directory.CreateDirectory(stateDirectory);
        if (!File.Exists(identityPath))
            Create(identityPath);

        return Validate(File.ReadAllText(identityPath).Trim());
    }

    private static void Create(string identityPath)
    {
        var instanceId = RandomNumberGenerator.GetHexString(GeneratedLength).ToLowerInvariant();
        var temporaryPath = $"{identityPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, instanceId);
            try
            {
                File.Move(temporaryPath, identityPath);
            }
            catch (IOException) when (File.Exists(identityPath))
            {
                // Another AppHost created the identity first.
            }
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static string Validate(string instanceId)
    {
        if (instanceId.Length is < 1 or > MaximumLength
            || !System.Text.RegularExpressions.Regex.IsMatch(instanceId, "^[a-z0-9](?:[a-z0-9-]*[a-z0-9])?$"))
        {
            throw new InvalidOperationException(
                $"Agentstration:InstanceId must contain only lowercase letters, digits, and internal hyphens (maximum {MaximumLength} characters).");
        }

        return instanceId;
    }
}
