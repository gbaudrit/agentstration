using System.Text.RegularExpressions;
using Agentstration.Management.Abstractions;

namespace Agentstration.Management.Contracts;

public static partial class SourceRegistryReferenceResolver
{
    public static string ResolvePublicationPath(Uri baseUri, string manifestUrl)
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        ValidateBaseUri(baseUri);
        if (string.IsNullOrWhiteSpace(manifestUrl) || manifestUrl.Length > 2048 || manifestUrl.Contains('%'))
            throw Invalid("source_registry_manifest_url_invalid", "manifestUrl must be a non-empty URI without percent-encoding and at most 2048 characters.");
        if (!Uri.TryCreate(manifestUrl, UriKind.RelativeOrAbsolute, out var reference))
            throw Invalid("source_registry_manifest_url_invalid", $"manifestUrl '{manifestUrl}' is not a valid URI reference.");
        if (!reference.IsAbsoluteUri && (manifestUrl.Contains('?') || manifestUrl.Contains('#')))
            throw Invalid("source_registry_manifest_url_invalid", $"manifestUrl '{manifestUrl}' must not contain a query string or fragment.");
        if (reference.IsAbsoluteUri)
        {
            if (reference.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(reference.UserInfo)
                || !string.IsNullOrEmpty(reference.Query) || !string.IsNullOrEmpty(reference.Fragment))
                throw Invalid("source_registry_manifest_url_invalid", $"manifestUrl '{manifestUrl}' must be an HTTP(S) URL without credentials, query, or fragment.");
            if (!SameOrigin(baseUri, reference))
                throw Invalid("source_registry_manifest_origin_invalid", $"manifestUrl '{manifestUrl}' must remain on the registry origin.");
        }

        var rawPath = reference.IsAbsoluteUri ? OriginalAbsolutePath(manifestUrl) : manifestUrl;
        RejectUnsafePath(rawPath, manifestUrl);

        var registryDocument = new Uri(baseUri, "registry.json");
        var resolved = new Uri(registryDocument, reference);
        if (!SameOrigin(baseUri, resolved))
            throw Invalid("source_registry_manifest_origin_invalid", $"manifestUrl '{manifestUrl}' must remain on the registry origin.");

        var basePath = baseUri.AbsolutePath;
        var resolvedPath = resolved.AbsolutePath;
        if (!resolvedPath.StartsWith(basePath, StringComparison.Ordinal)
            || resolvedPath.Length == basePath.Length)
            throw Invalid("source_registry_manifest_path_invalid", $"manifestUrl '{manifestUrl}' must resolve beneath the publication base path.");
        var descendant = resolvedPath[basePath.Length..];
        RejectUnsafePath(descendant, manifestUrl);
        return descendant;
    }

    public static void ValidateBaseUri(Uri baseUri)
    {
        if (!baseUri.IsAbsoluteUri || baseUri.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(baseUri.UserInfo) || !string.IsNullOrEmpty(baseUri.Query)
            || !string.IsNullOrEmpty(baseUri.Fragment) || !baseUri.AbsoluteUri.EndsWith('/'))
            throw Invalid("source_registry_base_uri_invalid", "--base-uri must be an absolute HTTP(S) URI ending in '/' without credentials, query, or fragment.");
        var basePath = baseUri.GetComponents(UriComponents.Path, UriFormat.UriEscaped).TrimEnd('/');
        if (basePath.Length > 0) RejectUnsafePath(basePath, baseUri.AbsoluteUri);
    }

    private static string OriginalAbsolutePath(string value)
    {
        var authority = value.IndexOf("://", StringComparison.Ordinal);
        var path = authority < 0 ? -1 : value.IndexOf('/', authority + 3);
        return path < 0 ? string.Empty : value[(path + 1)..];
    }

    private static void RejectUnsafePath(string path, string manifestUrl)
    {
        if (path.StartsWith('/') || path.EndsWith('/') || path.Contains('\\') || path.Contains('%') || path.Contains("//", StringComparison.Ordinal))
            throw Invalid("source_registry_manifest_path_invalid", $"manifestUrl '{manifestUrl}' contains an unsafe path.");
        var segments = path.Split('/');
        if (segments.Any(segment => segment is "." or ".." || !PathSegmentPattern().IsMatch(segment)))
            throw Invalid("source_registry_manifest_path_invalid", $"manifestUrl '{manifestUrl}' contains an invalid path segment.");
    }

    private static bool SameOrigin(Uri left, Uri right) =>
        string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.IdnHost, right.IdnHost, StringComparison.OrdinalIgnoreCase)
        && left.Port == right.Port;

    private static SourceValidationException Invalid(string code, string message) => new(code, message);

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex PathSegmentPattern();
}
