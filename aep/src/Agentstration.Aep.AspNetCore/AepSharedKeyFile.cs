using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Aep.AspNetCore;

public enum AepEnrollmentMode { Disabled, PairingCode, SharedKeyFile }

public static class AepSharedKeyFile
{
    public const int MinimumTokenBytes = 32;
    public const int MaximumFileBytes = 4096;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static string Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        byte[] bytes;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, MaximumFileBytes + 1, FileOptions.SequentialScan);
            if (stream.Length > MaximumFileBytes) throw new InvalidDataException("The AEP shared key file exceeds the maximum size.");
            bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"The AEP shared key file '{path}' is absent or unreadable.", exception);
        }

        try
        {
            var length = bytes.Length;
            var hasTrailingLineFeed = length > 0 && bytes[length - 1] == (byte)'\n';
            if (hasTrailingLineFeed) length--;
            if (hasTrailingLineFeed && length > 0 && bytes[length - 1] == (byte)'\r') length--;
            if (length < MinimumTokenBytes)
                throw new InvalidDataException("The AEP shared key must contain at least 256 bits of token material.");
            if (bytes.AsSpan(0, length).Contains((byte)'\r') || bytes.AsSpan(0, length).Contains((byte)'\n'))
                throw new InvalidDataException("The AEP shared key file must contain one UTF-8 line.");
            return StrictUtf8.GetString(bytes, 0, length);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("The AEP shared key file is not valid UTF-8.", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public static string GenerateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(MinimumTokenBytes);
        try
        {
            return Convert.ToBase64String(bytes)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }
}

public static class AepSharedKeyFileServiceCollectionExtensions
{
    public static IServiceCollection AddAepEnrollmentAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        var modeText = configuration["Aep:EnrollmentMode"] ?? nameof(AepEnrollmentMode.Disabled);
        if (!Enum.TryParse<AepEnrollmentMode>(modeText, ignoreCase: true, out var mode))
            throw new InvalidOperationException("Aep:EnrollmentMode must be Disabled, PairingCode, or SharedKeyFile.");
        if (mode == AepEnrollmentMode.Disabled) return services;
        if (mode == AepEnrollmentMode.PairingCode)
            throw new InvalidOperationException("PairingCode enrollment is not available in this host version.");
        var path = configuration["Aep:SharedKeyFile:Path"];
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("Aep:SharedKeyFile:Path is required for SharedKeyFile enrollment.");
        var token = AepSharedKeyFile.Read(path);
        var clientId = configuration["Aep:SharedKeyFile:ClientId"] ?? "agentstration-development";
        return services.AddAepStaticBearerAuthentication(options =>
            options.AddToken("shared-key-file", clientId, token, AepAuthenticationDefaults.InvokePermission));
    }
}
