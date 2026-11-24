using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Agentstration.Extensions.GitHub;

public sealed class GitHubConnection(HttpClient http, IOptions<GitHubProviderOptions> options)
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow, MaxDepth = 16 };

    internal Session Open(string apiBaseUrl, string credential, IReadOnlyList<AllowedGitHubRepository> allowed)
    {
        if (string.IsNullOrWhiteSpace(apiBaseUrl) || !Uri.TryCreate(apiBaseUrl.TrimEnd('/') + "/", UriKind.Absolute, out var origin) || !string.IsNullOrEmpty(origin.UserInfo)
            || !string.IsNullOrEmpty(origin.Query) || !string.IsNullOrEmpty(origin.Fragment)
            || origin.Scheme != "https" && !(options.Value.AllowHttpLoopback && origin.IsLoopback && origin.Scheme == "http")
            || !options.Value.AllowedApiOrigins.Any(value => Uri.TryCreate(value.TrimEnd('/') + "/", UriKind.Absolute, out var permitted) && permitted == origin)
            || string.IsNullOrWhiteSpace(credential) || credential.Length > 8192 || credential.Any(char.IsControl)
            || allowed is not { Count: >= 1 and <= 16 } || allowed.Any(value => value is null || !RepositoryName(value.Repository)
                || !long.TryParse(value.NativeId, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id < 1)
            || allowed.Select(value => value.Repository).Distinct(StringComparer.Ordinal).Count() != allowed.Count)
            throw new GitHubProviderException("github_connection_invalid");
        return new(http, origin, credential, allowed);
    }

    internal static bool RepositoryName(string? name) => name is { Length: >= 3 and <= 201 }
        && name.Split('/') is [var owner, var repository] && Segment(owner) && Segment(repository);
    internal static bool Segment(string? text) => text is { Length: >= 1 and <= 100 } && text is not ("." or "..")
        && text.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');
    internal static string Digest(JsonElement value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.GetRawText()))).ToLowerInvariant();

    internal sealed class Session(HttpClient http, Uri origin, string credential, IReadOnlyList<AllowedGitHubRepository> allowed)
    {
        internal string Authority(string repository) => origin.AbsoluteUri + "repos/" + Allowed(repository).NativeId;
        private AllowedGitHubRepository Allowed(string repository) => allowed.SingleOrDefault(value => value.Repository == repository)
            ?? throw new GitHubProviderException("github_repository_denied");
        internal async Task<JsonElement> RepositoryAsync(string repository, CancellationToken token)
        {
            var permission = Allowed(repository);
            var result = await SendAsync(HttpMethod.Get, "repos/" + repository, null, token);
            if (Id(result) != permission.NativeId || result.GetProperty("full_name").GetString() != repository)
                throw new GitHubProviderException("github_repository_identity_changed");
            return result;
        }
        internal async Task<JsonElement> SendAsync(HttpMethod method, string path, object? body, CancellationToken token, bool allowNotFound = false)
        {
            using var request = new HttpRequestMessage(method, new Uri(origin, path));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
            request.Headers.UserAgent.ParseAdd("Agentstration-GitHub/1.0");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.Add("X-GitHub-Api-Version", "2026-03-10");
            if (body is not null) request.Content = JsonContent.Create(body);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (allowNotFound && response.StatusCode == HttpStatusCode.NotFound) return default;
            if (!response.IsSuccessStatusCode) throw new GitHubProviderException(response.StatusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "github_credentials_denied",
                HttpStatusCode.NotFound => "github_resource_unavailable",
                HttpStatusCode.Conflict or HttpStatusCode.UnprocessableEntity => "github_native_conflict",
                HttpStatusCode.TooManyRequests => "github_rate_limited",
                _ => "github_remote_unavailable"
            });
            if (response.Content.Headers.ContentLength is > 1024 * 1024) throw new GitHubProviderException("github_response_too_large");
            await using var stream = await response.Content.ReadAsStreamAsync(token);
            using var content = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(buffer, token)) != 0)
            {
                if (content.Length + read > 1024 * 1024) throw new GitHubProviderException("github_response_too_large");
                await content.WriteAsync(buffer.AsMemory(0, read), token);
            }
            if (content.Length == 0) return default;
            using var document = JsonDocument.Parse(content.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
            return document.RootElement.Clone();
        }

        internal async Task<IReadOnlyList<JsonElement>> ListAsync(string path, CancellationToken token)
        {
            var values = new List<JsonElement>();
            for (var page = 1; page <= 10; page++)
            {
                var result = await SendAsync(HttpMethod.Get, path + (path.Contains('?', StringComparison.Ordinal) ? "&" : "?") + $"per_page=100&page={page}", null, token);
                if (result.ValueKind != JsonValueKind.Array) throw new GitHubProviderException("github_response_invalid");
                values.AddRange(result.EnumerateArray());
                if (result.GetArrayLength() < 100) return values;
            }
            throw new GitHubProviderException("github_observation_incomplete");
        }
    }

    internal static string Id(JsonElement value) => value.GetProperty("id").GetInt64().ToString(CultureInfo.InvariantCulture);
    internal static string Number(JsonElement value) => value.GetProperty("number").GetInt32().ToString(CultureInfo.InvariantCulture);
    internal static bool Number(string? number) => int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0;
    internal static string Text(JsonElement value, string name) => value.GetProperty(name).GetString() ?? throw new GitHubProviderException("github_response_invalid");
}
