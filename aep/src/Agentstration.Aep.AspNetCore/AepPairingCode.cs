using System.Globalization;
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

    public bool TryUnenroll(string? accessToken)
    {
        if (string.IsNullOrWhiteSpace(accessToken)) return false;
        var digest = TokenDigest(accessToken);
        try
        {
            lock (sync)
            {
                var matchesActive = IsPaired
                    && (Matches(state.TokenDigest, digest) | Matches(state.PreviousTokenDigest, digest));
                var matchesCompletedRequest = string.Equals(state.Status, "unpaired", StringComparison.Ordinal)
                    && (state.RevokedTokenDigests ?? []).Any(value => Matches(value, digest));
                if (!matchesActive && !matchesCompletedRequest) return false;
                if (matchesActive)
                {
                    state = state with
                    {
                        Status = "unpaired",
                        ClientId = null,
                        RevokedTokenDigests = AppendRevoked(state.RevokedTokenDigests, state.TokenDigest, state.PreviousTokenDigest),
                        TokenDigest = null,
                        PreviousTokenDigest = null
                    };
                    WriteAtomic(path, state);
                }
                return true;
            }
        }
        finally { CryptographicOperations.ZeroMemory(digest); }
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

    private static string[] AppendRevoked(IReadOnlyList<string>? existing, params string?[] candidates) =>
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
            using (var stream = new FileStream(temporary, options))
            {
                JsonSerializer.Serialize(stream, value);
                stream.Flush(flushToDisk: true);
            }
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
                    state.InstanceId, extensionOptions.Value.Extension,
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

    public string PairingForm(string? acceptLanguage, string? theme)
    {
        var text = PairingPageText.For(acceptLanguage);
        if (state.IsRevoked) return Page(text, text.RevokedTitle, text.RevokedMessage, includeForm: false, theme);
        if (state.IsPaired) return Page(text, text.AlreadyPairedTitle, text.AlreadyPairedMessage, includeForm: false, theme);
        return Page(text, text.PairTitle, text.PairMessage, includeForm: true, theme);
    }

    public async Task<(int Status, string Html)> PairAsync(string? code, string? acceptLanguage, string? theme, CancellationToken cancellationToken)
    {
        var text = PairingPageText.For(acceptLanguage);
        if (state.IsPaired || state.IsRevoked) return (410, PairingForm(acceptLanguage, theme));
        if (string.IsNullOrWhiteSpace(code) || code.Length > 64)
            return (422, Page(text, text.FailedTitle, text.InvalidCodeMessage, includeForm: true, theme));
        try
        {
            using var client = Client();
            using var claimResponse = await client.PostAsJsonAsync(AepEnrollmentProtocol.ClaimPath,
                new AepEnrollmentClaim(state.InstanceId, state.InstanceId, code.Trim()), AepProtocol.JsonOptions, cancellationToken);
            var credential = await ReadAsync<AepEnrollmentCredential>(claimResponse, cancellationToken);
            state.SetPaired(credential.ClientId, credential.AccessToken);
            using var readyResponse = await client.PostAsJsonAsync(AepEnrollmentProtocol.ReadyPath,
                new AepEnrollmentReady(state.InstanceId, state.InstanceId, credential.CompletionToken), AepProtocol.JsonOptions, cancellationToken);
            await EnsureSuccessAsync(readyResponse, cancellationToken);
            return (200, Page(text, text.PairedTitle, text.PairedMessage, includeForm: false, theme, autoClose: true));
        }
        catch (AepPairingException exception)
        {
            PairingRejected(logger, exception.Code, null);
            return (exception.StatusCode, Page(text, text.FailedTitle, exception.Message, includeForm: !state.IsPaired, theme));
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            AuthorityUnavailable(logger, exception);
            return (502, Page(text, text.FailedTitle, text.AuthorityUnavailableMessage, includeForm: !state.IsPaired, theme));
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

    private static string Page(
        PairingPageText text,
        string title,
        string message,
        bool includeForm,
        string? theme,
        bool autoClose = false)
    {
        var encodedTitle = HtmlEncoder.Default.Encode(title);
        var encodedMessage = HtmlEncoder.Default.Encode(message);
        var normalizedTheme = theme?.Trim() switch
        {
            var value when string.Equals(value, "dark", StringComparison.OrdinalIgnoreCase) => "dark",
            var value when string.Equals(value, "light", StringComparison.OrdinalIgnoreCase) => "light",
            _ => null
        };
        var themeAttribute = normalizedTheme is null ? string.Empty : $" class=\"theme-{normalizedTheme}\"";
        var themeField = normalizedTheme is null
            ? string.Empty
            : $"<input type=\"hidden\" name=\"theme\" value=\"{normalizedTheme}\">";
        var form = includeForm
            ? $"""
              <form method="post" action="{AepEnrollmentProtocol.PairingPath}">
                {themeField}
                <label for="pairing-code">{HtmlEncoder.Default.Encode(text.CodeLabel)}</label>
                <input id="pairing-code" name="code" inputmode="numeric" autocomplete="one-time-code" required maxlength="64" autofocus>
                <p class="hint">{HtmlEncoder.Default.Encode(text.CodeHint)}</p>
                <button type="submit">{HtmlEncoder.Default.Encode(text.SubmitLabel)}</button>
              </form>
              """
            : string.Empty;
        var closeNotice = autoClose
            ? $"""
              <p class="close-notice"
                 data-auto-close-seconds="10"
                 data-countdown-plural="{HtmlEncoder.Default.Encode(text.CloseCountdownPlural)}"
                 data-countdown-singular="{HtmlEncoder.Default.Encode(text.CloseCountdownSingular)}"
                 data-close-blocked="{HtmlEncoder.Default.Encode(text.CloseBlockedMessage)}">{HtmlEncoder.Default.Encode(string.Format(CultureInfo.InvariantCulture, text.CloseCountdownPlural, 10))}</p>
              <noscript><p class="close-notice">{HtmlEncoder.Default.Encode(text.CloseBlockedMessage)}</p></noscript>
              """
            : string.Empty;
        var closeScript = autoClose
            ? $"<script src=\"{AepPairingBrand.CloseScriptPath}\" defer></script>"
            : string.Empty;
        return $$$"""
            <!doctype html>
            <html lang="{{{text.Language}}}"{{{themeAttribute}}}>
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width,initial-scale=1">
              <meta name="color-scheme" content="light dark">
              <meta name="theme-color" content="#f4f7fb" media="(prefers-color-scheme:light)">
              <meta name="theme-color" content="#080e18" media="(prefers-color-scheme:dark)">
              <link rel="icon" type="image/png" href="{{{AepPairingBrand.MarkPath}}}">
              <title>{{{encodedTitle}}} · Agentstration</title>
              <style>
                :root{color-scheme:light;--bg:#f4f7fb;--surface:#fff;--surface-muted:#f8fafd;--border:#dce4ef;--border-strong:#c5d1e0;--text:#111827;--muted:#64748b;--primary:#3678f6;--primary-hover:#2869da;--primary-soft:#eaf1ff;--glow:#e5eeff;--shadow:0 18px 50px rgba(28,45,77,.12);font-family:Inter,"Segoe UI Variable","Segoe UI",system-ui,sans-serif;font-synthesis:none}
                :root.theme-dark{color-scheme:dark;--bg:#080e18;--surface:#111c2b;--surface-muted:#152234;--border:#24344b;--border-strong:#354a65;--text:#f4f7fb;--muted:#9cb0c9;--primary:#6c98ff;--primary-hover:#80a6ff;--primary-soft:#172e54;--glow:#13294c;--shadow:0 20px 55px rgba(0,0,0,.38)}
                @media(prefers-color-scheme:dark){:root:not(.theme-light):not(.theme-dark){color-scheme:dark;--bg:#080e18;--surface:#111c2b;--surface-muted:#152234;--border:#24344b;--border-strong:#354a65;--text:#f4f7fb;--muted:#9cb0c9;--primary:#6c98ff;--primary-hover:#80a6ff;--primary-soft:#172e54;--glow:#13294c;--shadow:0 20px 55px rgba(0,0,0,.38)}}
                *{box-sizing:border-box}body{min-height:100vh;margin:0;display:grid;place-items:center;padding:24px;background:radial-gradient(circle at 50% 0,var(--glow) 0,transparent 44%),var(--bg);color:var(--text)}
                main{width:min(100%,560px);padding:36px;background:var(--surface);border:1px solid var(--border);border-radius:18px;box-shadow:var(--shadow)}
                .brand{display:flex;align-items:center;margin-bottom:28px}.brand img{display:block;width:min(100%,218px);height:auto}.brand .logo-dark{display:none}:root.theme-dark .brand .logo-light{display:none}:root.theme-dark .brand .logo-dark{display:block}
                @media(prefers-color-scheme:dark){:root:not(.theme-light):not(.theme-dark) .brand .logo-light{display:none}:root:not(.theme-light):not(.theme-dark) .brand .logo-dark{display:block}}
                .eyebrow{margin:0 0 8px;color:var(--primary);font:700 .75rem "Cascadia Code",Consolas,monospace;letter-spacing:.09em;text-transform:uppercase}h1{margin:0;font-size:clamp(1.7rem,5vw,2.2rem);line-height:1.15;letter-spacing:-.035em}main>p:not(.eyebrow){margin:14px 0 26px;color:var(--muted);line-height:1.55}
                form{display:grid;gap:10px}label{font-size:.9rem;font-weight:700}input{width:100%;height:52px;padding:0 15px;border:1px solid var(--border-strong);border-radius:10px;background:var(--surface-muted);color:var(--text);font:600 1.15rem/1 Inter,"Segoe UI",system-ui,sans-serif;letter-spacing:.08em;outline:none;box-shadow:inset 0 1px 2px rgba(20,35,58,.08)}input:focus{border-color:var(--primary);background:var(--surface);box-shadow:0 0 0 4px color-mix(in srgb,var(--primary) 16%,transparent)}
                .hint{margin:0 0 8px;color:var(--muted);font-size:.82rem;line-height:1.45}.close-notice{padding:12px 14px;border:1px solid var(--border);border-radius:10px;background:var(--surface-muted);color:var(--muted);font-size:.9rem}button{min-height:46px;padding:0 20px;border:0;border-radius:10px;color:#fff;background:var(--primary);font:700 .92rem Inter,"Segoe UI",system-ui,sans-serif;cursor:pointer;box-shadow:0 7px 16px color-mix(in srgb,var(--primary) 25%,transparent)}button:hover{background:var(--primary-hover)}button:focus-visible{outline:3px solid color-mix(in srgb,var(--primary) 28%,transparent);outline-offset:2px}
                @media(max-width:520px){body{padding:14px}main{padding:26px 22px;border-radius:14px}}
              </style>
            </head>
            <body><main><div class="brand" aria-label="Agentstration"><img class="logo-light" src="{{{AepPairingBrand.LightLockupPath}}}" alt=""><img class="logo-dark" src="{{{AepPairingBrand.DarkLockupPath}}}" alt=""></div><p class="eyebrow">{{{HtmlEncoder.Default.Encode(text.Eyebrow)}}}</p><h1>{{{encodedTitle}}}</h1><p>{{{encodedMessage}}}</p>{{{closeNotice}}}{{{form}}}</main>{{{closeScript}}}</body>
            </html>
            """;
    }

    private sealed record PairingPageText(
        string Language,
        string Eyebrow,
        string PairTitle,
        string PairMessage,
        string CodeLabel,
        string CodeHint,
        string SubmitLabel,
        string FailedTitle,
        string InvalidCodeMessage,
        string AuthorityUnavailableMessage,
        string PairedTitle,
        string PairedMessage,
        string CloseCountdownPlural,
        string CloseCountdownSingular,
        string CloseBlockedMessage,
        string AlreadyPairedTitle,
        string AlreadyPairedMessage,
        string RevokedTitle,
        string RevokedMessage)
    {
        public static PairingPageText For(string? acceptLanguage) =>
            acceptLanguage?.TrimStart().StartsWith("fr", StringComparison.OrdinalIgnoreCase) == true
                ? new("fr", "Enrôlement d’une extension AEP", "Associer cette extension AEP", "Saisissez le code à usage unique affiché par un administrateur Agentstration.", "Code d’association", "Ce code expire après 60 secondes et ne peut être utilisé qu’une fois.", "Associer l’extension", "Échec de l’association", "Le code d’association est invalide.", "L’autorité d’enrôlement est indisponible.", "Extension associée", "L’identifiant a été installé et le manifeste AEP authentifié a été vérifié.", "Cette fenêtre se fermera automatiquement dans {0} secondes.", "Cette fenêtre se fermera automatiquement dans {0} seconde.", "La fermeture automatique a été bloquée. Vous pouvez fermer cette fenêtre manuellement.", "Extension déjà associée", "Cette instance d’extension est déjà associée et son formulaire d’enrôlement est fermé.", "Identifiant de l’extension révoqué", "Cette instance d’extension doit être réinitialisée localement avant de pouvoir être enrôlée à nouveau.")
                : new("en", "AEP extension enrollment", "Pair this AEP extension", "Enter the one-time code shown by an Agentstration administrator.", "Pairing code", "This code expires after 60 seconds and can only be used once.", "Pair extension", "Pairing failed", "The pairing code is invalid.", "The enrollment authority is unavailable.", "Extension paired", "The credential was installed and the authenticated AEP manifest was verified.", "This window will close automatically in {0} seconds.", "This window will close automatically in {0} second.", "Automatic closing was blocked. You can close this window manually.", "Extension already paired", "This extension instance is paired and its enrollment form is closed.", "Extension credential revoked", "This extension instance requires an explicit local reset before it can enroll again.");
    }
}

internal static class AepPairingBrand
{
    public const string MarkPath = "/aep/enrollment/agentstration-mark.png";
    public const string DarkLockupPath = "/aep/enrollment/agentstration-lockup-dark.png";
    public const string LightLockupPath = "/aep/enrollment/agentstration-lockup-light.png";
    public const string CloseScriptPath = "/aep/enrollment/close.js";
    public const string CloseScript = """
        (() => {
          const notice = document.querySelector('[data-auto-close-seconds]');
          if (!notice) return;
          let remaining = Number.parseInt(notice.dataset.autoCloseSeconds, 10);
          const render = () => {
            const template = remaining === 1 ? notice.dataset.countdownSingular : notice.dataset.countdownPlural;
            notice.textContent = template.replace('{0}', remaining.toString());
          };
          render();
          const timer = window.setInterval(() => {
            remaining -= 1;
            render();
            if (remaining > 0) return;
            window.clearInterval(timer);
            window.close();
            window.setTimeout(() => {
              notice.textContent = notice.dataset.closeBlocked;
            }, 250);
          }, 1000);
        })();
        """;
    private const string ResourcePrefix = "Agentstration.Aep.AspNetCore.Assets.";

    public static Stream OpenMark() => Open("agentstration-mark.png");
    public static Stream OpenDarkLockup() => Open("agentstration-lockup-dark.png");
    public static Stream OpenLightLockup() => Open("agentstration-lockup-light.png");

    private static Stream Open(string name)
    {
        var resourceName = $"{ResourcePrefix}{name}";
        return typeof(AepPairingBrand).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded pairing brand asset '{resourceName}' was not found.");
    }
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
