using System.Text.RegularExpressions;
using Agentstration.Management.Abstractions;

namespace Agentstration.Management.Contracts;

public static partial class SourceRegistryReferenceResolver
{
    public static string ResolvePublicationPath(Uri baseUri, string manifestUrl) =>
        ResolveManifestPublicationPath(baseUri, manifestUrl);

    public static string ResolveManifestPublicationPath(Uri baseUri, string manifestUrl) =>
        ResolvePublicationPath(baseUri, manifestUrl, "manifest");

    public static string ResolveRegistryPublicationPath(Uri baseUri, string registryUrl) =>
        ResolvePublicationPath(baseUri, registryUrl, "registry");

    private static string ResolvePublicationPath(Uri baseUri, string referenceUrl, string referenceKind)
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        ValidateBaseUri(baseUri);
        var property = referenceKind == "registry" ? "registryUrl" : "manifestUrl";
        var codePrefix = referenceKind == "registry" ? "source_registry_index_registry" : "source_registry_manifest";
        if (string.IsNullOrWhiteSpace(referenceUrl) || referenceUrl.Length > 2048 || referenceUrl.Contains('%'))
            throw Invalid($"{codePrefix}_url_invalid", $"{property} must be a non-empty URI without percent-encoding and at most 2048 characters.");
        if (!Uri.TryCreate(referenceUrl, UriKind.RelativeOrAbsolute, out var reference))
            throw Invalid($"{codePrefix}_url_invalid", $"{property} '{referenceUrl}' is not a valid URI reference.");
        if (!reference.IsAbsoluteUri && (referenceUrl.Contains('?') || referenceUrl.Contains('#')))
            throw Invalid($"{codePrefix}_url_invalid", $"{property} '{referenceUrl}' must not contain a query string or fragment.");
        if (reference.IsAbsoluteUri)
        {
            if (reference.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(reference.UserInfo)
                || !string.IsNullOrEmpty(reference.Query) || !string.IsNullOrEmpty(reference.Fragment))
                throw Invalid($"{codePrefix}_url_invalid", $"{property} '{referenceUrl}' must be an HTTP(S) URL without credentials, query, or fragment.");
            if (!SameOrigin(baseUri, reference))
                throw Invalid($"{codePrefix}_origin_invalid", $"{property} '{referenceUrl}' must remain on the registry origin.");
        }

        var rawPath = reference.IsAbsoluteUri ? OriginalAbsolutePath(referenceUrl) : referenceUrl;
        RejectUnsafePath(rawPath, property, referenceUrl, codePrefix);

        var publicationDocument = new Uri(baseUri, referenceKind == "registry" ? "index.json" : "registry.json");
        var resolved = new Uri(publicationDocument, reference);
        if (!SameOrigin(baseUri, resolved))
            throw Invalid($"{codePrefix}_origin_invalid", $"{property} '{referenceUrl}' must remain on the registry origin.");

        var basePath = baseUri.AbsolutePath;
        var resolvedPath = resolved.AbsolutePath;
        if (!resolvedPath.StartsWith(basePath, StringComparison.Ordinal)
            || resolvedPath.Length == basePath.Length)
            throw Invalid($"{codePrefix}_path_invalid", $"{property} '{referenceUrl}' must resolve beneath the publication base path.");
        var descendant = resolvedPath[basePath.Length..];
        RejectUnsafePath(descendant, property, referenceUrl, codePrefix);
        return descendant;
    }

    public static void ValidateBaseUri(Uri baseUri)
    {
        if (!baseUri.IsAbsoluteUri || baseUri.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(baseUri.UserInfo) || !string.IsNullOrEmpty(baseUri.Query)
            || !string.IsNullOrEmpty(baseUri.Fragment) || !baseUri.AbsoluteUri.EndsWith('/'))
            throw Invalid("source_registry_base_uri_invalid", "--base-uri must be an absolute HTTP(S) URI ending in '/' without credentials, query, or fragment.");
        var basePath = baseUri.GetComponents(UriComponents.Path, UriFormat.UriEscaped).TrimEnd('/');
        if (basePath.Length > 0) RejectUnsafePath(basePath, "--base-uri", baseUri.AbsoluteUri, "source_registry_base_uri");
    }

    private static string OriginalAbsolutePath(string value)
    {
        var authority = value.IndexOf("://", StringComparison.Ordinal);
        var path = authority < 0 ? -1 : value.IndexOf('/', authority + 3);
        return path < 0 ? string.Empty : value[(path + 1)..];
    }

    private static void RejectUnsafePath(string path, string property, string referenceUrl, string codePrefix)
    {
        if (path.StartsWith('/') || path.EndsWith('/') || path.Contains('\\') || path.Contains('%') || path.Contains("//", StringComparison.Ordinal))
            throw Invalid($"{codePrefix}_path_invalid", $"{property} '{referenceUrl}' contains an unsafe path.");
        var segments = path.Split('/');
        if (segments.Any(segment => segment is "." or ".." || !PathSegmentPattern().IsMatch(segment)))
            throw Invalid($"{codePrefix}_path_invalid", $"{property} '{referenceUrl}' contains an invalid path segment.");
    }

    private static bool SameOrigin(Uri left, Uri right) =>
        string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.IdnHost, right.IdnHost, StringComparison.OrdinalIgnoreCase)
        && left.Port == right.Port;

    private static SourceValidationException Invalid(string code, string message) => new(code, message);

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex PathSegmentPattern();
}

public sealed class SourceRegistryRuntimeReferenceResolver : ISourceRegistryReferenceResolver
{
    public string ResolveRegistryPublicationPath(Uri baseUri, string registryUrl) =>
        SourceRegistryReferenceResolver.ResolveRegistryPublicationPath(baseUri, registryUrl);

    public Uri ResolveManifestUrl(Uri finalIndexUrl, string manifestUrl)
    {
        ArgumentNullException.ThrowIfNull(finalIndexUrl);
        var builder = new UriBuilder(finalIndexUrl) { Query = string.Empty, Fragment = string.Empty };
        var slash = builder.Path.LastIndexOf('/');
        builder.Path = slash < 0 ? "/" : builder.Path[..(slash + 1)];
        var baseUri = builder.Uri;
        var path = SourceRegistryReferenceResolver.ResolveManifestPublicationPath(baseUri, manifestUrl);
        return new Uri(baseUri, path);
    }
}
