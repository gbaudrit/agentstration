using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Agentstration.Aep.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agentstration.Aep.AspNetCore;

internal sealed record AepPairingState(
    Guid InstanceId,
    string Status,
    string? ClientId,
    string? TokenDigest,
    string? PreviousTokenDigest = null,
    IReadOnlyList<string>? RevokedTokenDigests = null);

internal sealed class AepPairingStateStore : IAepDynamicCredentialStore
{
    private readonly string path;
    private readonly object sync = new();
    private AepPairingState state;

    public AepPairingStateStore(AepPairingOptions options)
    {
        path = options.StateFile;
        state = LoadOrCreate(path);
    }

    public Guid InstanceId => state.InstanceId;
    public bool IsPaired => string.Equals(state.Status, "paired", StringComparison.Ordinal);
    public bool IsRevoked => string.Equals(state.Status, "revoked", StringComparison.Ordinal);

    public AepDynamicCredentialMatch Match(ReadOnlySpan<byte> suppliedDigest, out string clientId)
    {
        lock (sync)
        {
            try
            {
                if (!File.Exists(path))
                {
                    clientId = string.Empty;
                    return AepDynamicCredentialMatch.None;
                }
                var persisted = LoadExisting(path);
                if (persisted.InstanceId != state.InstanceId)
                {
                    clientId = string.Empty;
                    return AepDynamicCredentialMatch.None;
                }
                state = persisted;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                clientId = string.Empty;
                return AepDynamicCredentialMatch.None;
            }
            clientId = state.ClientId ?? string.Empty;
            foreach (var revoked in state.RevokedTokenDigests ?? [])
                if (Matches(revoked, suppliedDigest)) return AepDynamicCredentialMatch.Revoked;
            if (!IsPaired || string.IsNullOrWhiteSpace(clientId)) return AepDynamicCredentialMatch.None;
            return Matches(state.TokenDigest, suppliedDigest) | Matches(state.PreviousTokenDigest, suppliedDigest)
                ? AepDynamicCredentialMatch.Authenticated
                : AepDynamicCredentialMatch.None;
        }
    }

    public void SetPaired(string clientId, string accessToken)
    {
        if (Encoding.UTF8.GetByteCount(accessToken) < 32)
            throw new ArgumentException("An AEP credential must contain at least 256 bits of entropy.", nameof(accessToken));
        var tokenBytes = Encoding.UTF8.GetBytes(accessToken);
        var digest = SHA256.HashData(tokenBytes);
        CryptographicOperations.ZeroMemory(tokenBytes);
        try
        {
            lock (sync)
            {
                if (IsPaired) throw new InvalidOperationException("This extension instance is already paired.");
                state = state with { Status = "paired", ClientId = clientId, TokenDigest = Convert.ToBase64String(digest), PreviousTokenDigest = null };
                WriteAtomic(path, state);
            }
        }
        finally { CryptographicOperations.ZeroMemory(digest); }
    }

    public void Rotate(string clientId, string accessToken)
    {
        if (Encoding.UTF8.GetByteCount(accessToken) < 32)
            throw new ArgumentException("An AEP credential must contain at least 256 bits of entropy.", nameof(accessToken));
        var digest = TokenDigest(accessToken);
        try
        {
            lock (sync)
            {
                if (!IsPaired || !string.Equals(state.ClientId, clientId, StringComparison.Ordinal))
                    throw new InvalidOperationException("The credential identity does not match this paired extension.");
                state = state with { PreviousTokenDigest = state.TokenDigest, TokenDigest = Convert.ToBase64String(digest) };
                WriteAtomic(path, state);
            }
        }
        finally { CryptographicOperations.ZeroMemory(digest); }
    }

    public void RevokePrevious()
    {
        lock (sync)
        {
            if (!IsPaired) throw new InvalidOperationException("This extension instance is not paired.");
            state = state with
            {
                RevokedTokenDigests = AppendRevoked(state.RevokedTokenDigests, state.PreviousTokenDigest),
                PreviousTokenDigest = null
            };
            WriteAtomic(path, state);
        }
    }

    public void Revoke()
    {
        lock (sync)
        {
            state = state with
            {
                Status = "revoked",
                RevokedTokenDigests = AppendRevoked(state.RevokedTokenDigests, state.TokenDigest, state.PreviousTokenDigest),
                TokenDigest = null,
                PreviousTokenDigest = null
            };
            WriteAtomic(path, state);
        }
    }

    public static void Reset(string stateFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateFile);
        var current = LoadOrCreate(stateFile);
        WriteAtomic(stateFile, new AepPairingState(current.InstanceId, "unpaired", null, null));
    }

    private static AepPairingState LoadOrCreate(string path)
    {
        if (File.Exists(path)) return LoadExisting(path);
        var created = new AepPairingState(Guid.NewGuid(), "unpaired", null, null);
        WriteAtomic(path, created);
        return created;
    }

    private static AepPairingState LoadExisting(string path)
    {
        var loaded = JsonSerializer.Deserialize<AepPairingState>(File.ReadAllText(path))
            ?? throw new InvalidDataException("The AEP pairing state file is invalid.");
        if (loaded.InstanceId == Guid.Empty || loaded.Status is not ("unpaired" or "paired" or "revoked")
            || loaded.Status == "paired" && (string.IsNullOrWhiteSpace(loaded.ClientId) || !ValidDigest(loaded.TokenDigest ?? string.Empty)
                || loaded.PreviousTokenDigest is not null && !ValidDigest(loaded.PreviousTokenDigest))
            || loaded.Status != "paired" && (loaded.TokenDigest is not null || loaded.PreviousTokenDigest is not null)
            || loaded.RevokedTokenDigests is { Count: > 8 }
            || loaded.RevokedTokenDigests?.Any(value => !ValidDigest(value)) == true)
            throw new InvalidDataException("The AEP pairing state file is incomplete.");
        return loaded;
    }

    private static IReadOnlyList<string> AppendRevoked(IReadOnlyList<string>? existing, params string?[] candidates) =>
        (existing ?? [])
            .Concat(candidates.OfType<string>())
            .Distinct(StringComparer.Ordinal)
            .TakeLast(8)
            .ToArray();

    private static void WriteAtomic(string path, AepPairingState value)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.WriteThrough };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using var stream = new FileStream(temporary, options);
            JsonSerializer.Serialize(stream, value);
            stream.Flush(flushToDisk: true);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static byte[] TokenDigest(string accessToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        var tokenBytes = Encoding.UTF8.GetBytes(accessToken);
        try { return SHA256.HashData(tokenBytes); }
        finally { CryptographicOperations.ZeroMemory(tokenBytes); }
    }

    private static bool Matches(string? encodedDigest, ReadOnlySpan<byte> suppliedDigest)
    {
        if (string.IsNullOrWhiteSpace(encodedDigest)) return false;
        var expected = Convert.FromBase64String(encodedDigest);
        try { return CryptographicOperations.FixedTimeEquals(suppliedDigest, expected); }
        finally { CryptographicOperations.ZeroMemory(expected); }
    }

    private static bool ValidDigest(string value)
    {
        try
        {
            var digest = Convert.FromBase64String(value);
            try { return digest.Length == SHA256.HashSizeInBytes; }
            finally { CryptographicOperations.ZeroMemory(digest); }
        }
        catch (FormatException) { return false; }
    }
}

public static class AepPairingLifecycle
{
    public static void ResetToUnpaired(string stateFile) => AepPairingStateStore.Reset(stateFile);
}

internal sealed record AepPairingOptions(
    Uri AuthorityUrl,
    Uri PublicEndpoint,
    Uri PairingUri,
    Guid TenantId,
    Guid WorkspaceId,
    string StateFile);

internal sealed class AepPairingCoordinator(
    AepPairingOptions options,
    AepPairingStateStore state,
    IHttpClientFactory httpClients,
    IOptions<AepExtensionOptions> extensionOptions,
    ILogger<AepPairingCoordinator> logger) : BackgroundService
{
    private static readonly Action<ILogger, TimeSpan, Exception?> AnnouncementFailed = LoggerMessage.Define<TimeSpan>(
        LogLevel.Warning, new EventId(1, "AepEnrollmentAnnouncementFailed"), "AEP enrollment announcement failed; retrying in {Delay}");
    private static readonly Action<ILogger, string, Exception?> PairingRejected = LoggerMessage.Define<string>(
        LogLevel.Warning, new EventId(2, "AepPairingRejected"), "AEP pairing failed with {Code}");
    private static readonly Action<ILogger, Exception?> AuthorityUnavailable = LoggerMessage.Define(
        LogLevel.Warning, new EventId(3, "AepEnrollmentAuthorityUnavailable"), "AEP pairing authority is unavailable");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (state.IsPaired || state.IsRevoked) return;
        var delay = TimeSpan.FromSeconds(1);
        while (!stoppingToken.IsCancellationRequested && !state.IsPaired)
        {
            try
            {
                using var client = Client();
                using var response = await client.PostAsJsonAsync(AepEnrollmentProtocol.AnnouncementPath, new AepEnrollmentAnnouncement(
                    state.InstanceId, options.TenantId, options.WorkspaceId, extensionOptions.Value.Extension,
                    options.PublicEndpoint, options.PairingUri), AepProtocol.JsonOptions, stoppingToken);
                await EnsureSuccessAsync(response, stoppingToken);
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

    public string PairingForm()
    {
        if (state.IsRevoked) return Page("Extension credential revoked", "This extension instance requires an explicit local reset before it can enroll again.", includeForm: false);
        if (state.IsPaired) return Page("Extension already paired", "This extension instance is paired and its enrollment form is closed.", includeForm: false);
        return Page("Pair this AEP extension", "Enter the one-time code shown by an Agentstration administrator.", includeForm: true);
    }

    public async Task<(int Status, string Html)> PairAsync(string? code, CancellationToken cancellationToken)
    {
        if (state.IsPaired || state.IsRevoked) return (410, PairingForm());
        if (string.IsNullOrWhiteSpace(code) || code.Length > 64)
            return (422, Page("Pairing failed", "The pairing code is invalid.", includeForm: true));
        try
        {
            using var client = Client();
            using var claimResponse = await client.PostAsJsonAsync(AepEnrollmentProtocol.ClaimPath,
                new AepEnrollmentClaim(state.InstanceId, state.InstanceId, options.WorkspaceId, code.Trim()), AepProtocol.JsonOptions, cancellationToken);
            var credential = await ReadAsync<AepEnrollmentCredential>(claimResponse, cancellationToken);
            state.SetPaired(credential.ClientId, credential.AccessToken);
            using var readyResponse = await client.PostAsJsonAsync(AepEnrollmentProtocol.ReadyPath,
                new AepEnrollmentReady(state.InstanceId, state.InstanceId, options.WorkspaceId, credential.CompletionToken), AepProtocol.JsonOptions, cancellationToken);
            await EnsureSuccessAsync(readyResponse, cancellationToken);
            return (200, Page("Extension paired", "The credential was installed and the authenticated AEP manifest was verified.", includeForm: false));
        }
        catch (AepPairingException exception)
        {
            PairingRejected(logger, exception.Code, null);
            return (exception.StatusCode, Page("Pairing failed", HtmlEncoder.Default.Encode(exception.Message), includeForm: !state.IsPaired));
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            AuthorityUnavailable(logger, exception);
            return (502, Page("Pairing failed", "The enrollment authority is unavailable.", includeForm: !state.IsPaired));
        }
    }

    private HttpClient Client()
    {
        var client = httpClients.CreateClient("aep-enrollment-authority");
        client.BaseAddress = options.AuthorityUrl;
        return client;
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<T>(AepProtocol.JsonOptions, cancellationToken)
            ?? throw new AepPairingException("invalid_response", "The enrollment authority returned an empty response.", 502);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        string code = "enrollment_failed";
        string message = "The enrollment authority rejected the request.";
        try
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            if (document.RootElement.TryGetProperty("error", out var error))
            {
                if (error.TryGetProperty("code", out var value)) code = value.GetString() ?? code;
                if (error.TryGetProperty("message", out value)) message = value.GetString() ?? message;
            }
        }
        catch (JsonException) { }
        throw new AepPairingException(code, message, (int)response.StatusCode);
    }

    private static string Page(string title, string message, bool includeForm) => $$"""
        <!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width"><title>{{title}}</title></head>
        <body><main><h1>{{title}}</h1><p>{{message}}</p>{{(includeForm ? $"<form method=\"post\" action=\"{AepEnrollmentProtocol.PairingPath}\"><label>Pairing code <input name=\"code\" inputmode=\"numeric\" autocomplete=\"one-time-code\" required maxlength=\"64\"></label><button type=\"submit\">Pair extension</button></form>" : string.Empty)}}</main></body></html>
        """;
}

internal sealed class AepPairingException(string code, string message, int statusCode) : Exception(message)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}

internal static class AepPairingRegistration
{
    public static IServiceCollection AddPairingCode(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection("Aep:PairingCode");
        var authority = Absolute(section["AuthorityUrl"], "Aep:PairingCode:AuthorityUrl");
        var endpoint = Absolute(section["PublicEndpoint"], "Aep:PairingCode:PublicEndpoint");
        var pairingUri = Absolute(section["PairingUri"] ?? new Uri(endpoint, AepEnrollmentProtocol.PairingPath).ToString(), "Aep:PairingCode:PairingUri");
        if (!SameOrigin(endpoint, pairingUri) || pairingUri.AbsolutePath != AepEnrollmentProtocol.PairingPath || pairingUri.Query.Length != 0 || pairingUri.Fragment.Length != 0)
            throw new InvalidOperationException("Aep:PairingCode:PairingUri must use the public endpoint origin and the standard pairing path without a query or fragment.");
        if (authority.Scheme != Uri.UriSchemeHttps && !section.GetValue("AllowInsecureHttp", false))
            throw new InvalidOperationException("The AEP enrollment authority must use HTTPS unless AllowInsecureHttp is explicitly enabled for development.");
        var options = new AepPairingOptions(
            authority, endpoint, pairingUri,
            Guid.Parse(section["TenantId"] ?? throw new InvalidOperationException("Aep:PairingCode:TenantId is required.")),
            Guid.Parse(section["WorkspaceId"] ?? throw new InvalidOperationException("Aep:PairingCode:WorkspaceId is required.")),
            section["StateFile"] ?? Path.Combine(AppContext.BaseDirectory, ".aep", "pairing-state.json"));
        services.AddSingleton(options);
        services.AddSingleton<AepPairingStateStore>();
        services.AddSingleton<IAepDynamicCredentialStore>(provider => provider.GetRequiredService<AepPairingStateStore>());
        services.AddSingleton<AepPairingCoordinator>();
        services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<AepPairingCoordinator>());
        services.AddHttpClient("aep-enrollment-authority", client => client.Timeout = TimeSpan.FromSeconds(15));
        return services.AddAepStaticBearerAuthentication(_ => { });
    }

    private static Uri Absolute(string? value, string setting) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0
            ? uri
            : throw new InvalidOperationException($"{setting} must be an absolute HTTP(S) URI without user information.");
    private static bool SameOrigin(Uri left, Uri right) => left.Scheme == right.Scheme && left.IdnHost == right.IdnHost && left.Port == right.Port;
}
