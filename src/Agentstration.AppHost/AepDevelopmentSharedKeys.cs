using Agentstration.Aep.AspNetCore;

namespace Agentstration.AppHost;

public static class AepDevelopmentSharedKeys
{
    public static IReadOnlyDictionary<string, string> Provision(string directory, params string[] extensionNames)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory.CreateDirectory(directory);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var extensionName in extensionNames)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(extensionName);
            var path = Path.Combine(directory, $"{extensionName}.key");
            if (!File.Exists(path)) WriteAtomic(path);
            _ = AepSharedKeyFile.Read(path);
            result.Add(extensionName, path);
        }
        return result;
    }

    private static void WriteAtomic(string path)
    {
        var token = AepSharedKeyFile.GenerateToken();
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, token + "\n");
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            try { File.Move(temporary, path, overwrite: false); }
            catch (IOException) when (File.Exists(path)) { }
        }
        finally
        {
            token = string.Empty;
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
