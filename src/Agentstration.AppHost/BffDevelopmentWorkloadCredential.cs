using System.Security.Cryptography;

namespace Agentstration.AppHost;

public static class BffDevelopmentWorkloadCredential
{
    public static string Provision(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "primary.key");
        if (File.Exists(path)) return path;

        var secret = RandomNumberGenerator.GetBytes(32);
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
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
        return path;
    }
}
