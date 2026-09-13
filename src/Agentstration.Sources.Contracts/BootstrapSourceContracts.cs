using Agentstration.Management.Abstractions;
using Agentstration.Resources;

namespace Agentstration.Sources.Contracts;

public sealed record BootstrapSourceProfileSelection(ResourceScopeRef ScopeRef, string Publisher, string SourceName, Guid SourceVersionUid, string Channel, Guid SnapshotUid, string CatalogName, string EntryName, string Locale, string Path);

public sealed record BootstrapSourceProvenance(ResourceScopeRef ScopeRef, Guid SourceUid, string Publisher, string SourceName, Guid SourceVersionUid, string SourceVersion, string SourceVersionDigest, string Channel, string ProviderRevision, Guid SnapshotUid, string SnapshotDigest, string CatalogKind, string CatalogName, string CatalogPath, string EntryName, string Locale, string Path, SourceRegistryImportProvenance? Registry = null);
