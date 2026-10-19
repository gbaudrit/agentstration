using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Agentstration.Aep.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
        services.TryAddSingleton(TimeProvider.System);
        var modeText = configuration["Aep:EnrollmentMode"] ?? nameof(AepEnrollmentMode.Disabled);
        if (!Enum.TryParse<AepEnrollmentMode>(modeText, ignoreCase: true, out var mode))
            throw new InvalidOperationException("Aep:EnrollmentMode must be Disabled, PairingCode, or SharedKeyFile.");
        if (mode == AepEnrollmentMode.Disabled) return services;
        if (mode == AepEnrollmentMode.PairingCode)
            return services.AddPairingCode(configuration);
        var path = configuration["Aep:SharedKeyFile:Path"];
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("Aep:SharedKeyFile:Path is required for SharedKeyFile enrollment.");
        var token = AepSharedKeyFile.Read(path);
        var clientId = configuration["Aep:SharedKeyFile:ClientId"] ?? "agentstration-development";
        services.AddAepStaticBearerAuthentication(options =>
            options.AddToken("shared-key-file", clientId, token, AepAuthenticationDefaults.InvokePermission));
        var section = configuration.GetSection("Aep:SharedKeyFile");
        if (string.IsNullOrWhiteSpace(section["AuthorityUrl"])) return services;
        var authority = Absolute(section["AuthorityUrl"], "Aep:SharedKeyFile:AuthorityUrl");
        if (authority.Scheme != Uri.UriSchemeHttps && !section.GetValue("AllowInsecureHttp", false))
            throw new InvalidOperationException("The AEP enrollment authority must use HTTPS unless AllowInsecureHttp is explicitly enabled for development.");
        var publicEndpoint = Absolute(section["PublicEndpoint"], "Aep:SharedKeyFile:PublicEndpoint");
        var stateFile = section["StateFile"] ?? Path.Combine(AppContext.BaseDirectory, ".aep", "shared-key-instance");
        var announcementOptions = new AepSharedKeyAnnouncementOptions(
            authority,
            publicEndpoint,
            SharedKeyInstanceId.LoadOrCreate(stateFile),
            token);
        services.AddSingleton(announcementOptions);
        services.AddSingleton<AepSharedKeyAnnouncementCoordinator>();
        services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<AepSharedKeyAnnouncementCoordinator>());
        services.AddHttpClient("aep-shared-key-enrollment", client => client.Timeout = TimeSpan.FromSeconds(15));
        return services;
    }

    private static Uri Absolute(string? value, string setting) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0
            ? uri
            : throw new InvalidOperationException($"{setting} must be an absolute HTTP(S) URI without user information.");
}

internal sealed record AepSharedKeyAnnouncementOptions(
    Uri AuthorityUrl,
    Uri PublicEndpoint,
    Guid InstanceId,
    string SharedKey);

internal sealed class AepSharedKeyAnnouncementCoordinator(
    AepSharedKeyAnnouncementOptions options,
    IHttpClientFactory httpClients,
    IOptions<AepExtensionOptions> extensionOptions,
    TimeProvider timeProvider,
    ILogger<AepSharedKeyAnnouncementCoordinator> logger) : BackgroundService
{
    private static readonly Action<ILogger, TimeSpan, Exception?> AnnouncementFailed = LoggerMessage.Define<TimeSpan>(
        LogLevel.Warning,
        new EventId(1, "AepSharedKeyEnrollmentAnnouncementFailed"),
        "AEP SharedKeyFile enrollment announcement failed; retrying in {Delay}");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var delay = TimeSpan.FromSeconds(1);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var announcement = new AepEnrollmentAnnouncement(
                    options.InstanceId,
                    extensionOptions.Value.Extension,
                    options.PublicEndpoint,
                    null,
                    AepEnrollmentMethod.SharedKeyFile);
                var timestamp = timeProvider.GetUtcNow().ToUnixTimeSeconds();
                using var client = httpClients.CreateClient("aep-shared-key-enrollment");
                client.BaseAddress = options.AuthorityUrl;
                using var request = new HttpRequestMessage(HttpMethod.Post, AepEnrollmentProtocol.AnnouncementPath)
                {
                    Content = JsonContent.Create(announcement, options: AepProtocol.JsonOptions)
                };
                request.Headers.Add("X-AEP-Enrollment-Timestamp", timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture));
                request.Headers.Add("X-AEP-Enrollment-Signature", AepEnrollmentProofs.Sign(announcement, timestamp, options.SharedKey));
                using var response = await client.SendAsync(request, stoppingToken);
                response.EnsureSuccessStatusCode();
                return;
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                AnnouncementFailed(logger, delay, exception);
                await Task.Delay(delay, stoppingToken);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 30));
            }
        }
    }
}

internal static class SharedKeyInstanceId
{
    public static Guid LoadOrCreate(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        using var stream = new FileStream(fullPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (stream.Length > 0)
        {
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            if (Guid.TryParse(reader.ReadToEnd(), out var existing) && existing != Guid.Empty) return existing;
            throw new InvalidDataException("The AEP SharedKeyFile instance state is invalid.");
        }
        var created = Guid.NewGuid();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
        writer.Write(created.ToString("D"));
        writer.Flush();
        stream.Flush(flushToDisk: true);
        return created;
    }
}
