using System.Security.Cryptography;
using System.Text;

namespace Agentstration.AppHost;

public static class AepDevelopmentSharedKeys
{
    private const int MinimumTokenBytes = 32;
    private const int MaximumFileBytes = 4096;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

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
            Validate(path);
            result.Add(extensionName, path);
        }
        return result;
    }

    private static void WriteAtomic(string path)
    {
        var bytes = RandomNumberGenerator.GetBytes(MinimumTokenBytes);
        string token;
        try
        {
            token = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
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

    private static void Validate(string path)
    {
        var bytes = File.ReadAllBytes(path);
        try
        {
            if (bytes.Length > MaximumFileBytes)
                throw new InvalidDataException("The AEP shared key file exceeds the maximum size.");
            var length = bytes.Length;
            var hasTrailingLineFeed = length > 0 && bytes[length - 1] == (byte)'\n';
            if (hasTrailingLineFeed) length--;
            if (hasTrailingLineFeed && length > 0 && bytes[length - 1] == (byte)'\r') length--;
            if (length < MinimumTokenBytes
                || bytes.AsSpan(0, length).Contains((byte)'\r')
                || bytes.AsSpan(0, length).Contains((byte)'\n'))
                throw new InvalidDataException("The AEP shared key file must contain one token of at least 32 UTF-8 bytes.");
            _ = StrictUtf8.GetCharCount(bytes, 0, length);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }
}
