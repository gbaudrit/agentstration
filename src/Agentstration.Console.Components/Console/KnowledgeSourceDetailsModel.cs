using Agentstration.Knowledge.Contracts;
using Agentstration.Resources;
namespace Agentstration.Web.Console;
public static class KnowledgeSourceDetailsModel
{
    public static string? IdentityError(KnowledgeSourceManifest manifest, KnowledgeSourceResource saved)
    {
        if (manifest.ApiVersion != ResourceApiVersions.CoreV1) return "ApiVersionRequired";
        if (manifest.Kind != KnowledgeResourceKinds.KnowledgeSource) return "KindRequired";
        if (manifest.Metadata.Namespace != saved.Namespace) return "SourceNamespaceImmutable";
        if (manifest.Metadata.Name != saved.Name || manifest.ScopeRef is { } scope && scope != saved.ScopeRef
            || !manifest.Metadata.Tags.OrderBy(pair => pair.Key).SequenceEqual(saved.Metadata.Tags.OrderBy(pair => pair.Key))) return "SourceNameImmutable";
        return null;
    }
}
public sealed record KnowledgeSourceManifest
    {
        public ResourceScopeRef? ScopeRef { get; init; }
        public required string ApiVersion { get; init; }
        public required string Kind { get; init; }
        public required ResourceMetadata Metadata { get; init; }
        public required KnowledgeSourceProperties Definition { get; init; }
    }
