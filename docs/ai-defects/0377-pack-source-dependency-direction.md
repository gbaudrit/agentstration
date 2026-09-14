# CAR-0377: Sources owned Pack-specific discovery and installation behavior

## Status

Prevented — 2026-09-14

## References

- Issue: #377
- Introducing change: commit `e4b03bd` in PR #364
- Corrective pull request: #364
- Related ADRs: ADR-0086, ADR-0095, ADR-0109

## Defect

`Agentstration.Sources` referenced `Agentstration.Packs` and `Agentstration.Packs.Contracts`, owned `SourcePackInstallationService`, parsed `PackCatalog` manifests, and invoked `PackManagementService`. At the same time, Pack installation contracts referenced Source contracts for immutable selection, pinning, and provenance.

This created a conceptual bidirectional dependency. The generic Source and Registry distribution boundary knew one consumer's catalog schema and lifecycle, while the Pack family could not own the complete “install a Pack from a Source” use case.

## Detection

- Stage: Code review
- Detection mechanism: review of the remaining family relationships after removing `Management.Abstractions` in #373
- Why it was not detected earlier: the Management.Core extraction moved `SourcePackInstallationService` according to its Source-prefixed entry point rather than the lifecycle it orchestrates, and existing tests verified behavior without constraining dependency direction

## Causal analysis

### Faulty approach

The integration was classified as Source behavior because it started from a Source selection and was exposed beneath Source routes. That transport and discovery perspective overrode the stronger ownership signal: the service validates and installs a Pack through the Pack lifecycle.

### Contributing assumptions

- A Source-discovered operation was assumed to belong to Sources regardless of the aggregate it mutates.
- Keeping `PackCatalog` contracts in Sources was assumed to be necessary for catalog browsing.
- Behavioral reuse of `PackManagementService` was assumed to be sufficient even though the orchestration owner remained reversed.

### Missed signals

- #150 defines Source as a generic discovery and distribution boundary.
- #161 and ADR-0095 require Source-discovered Packs to reuse the authoritative Pack lifecycle rather than introduce Source ownership.
- `Packs.Contracts -> Sources.Contracts` already expressed the natural consumer-to-distribution dependency.
- `Sources -> Packs` meant every new distributed artifact type could expand the generic Source module.

### Safeguard gap

No architecture test prohibited Source assemblies from referencing Pack assemblies. Catalog parsing was implemented as a closed Source-owned switch, so adding a consumer-specific catalog kind naturally added consumer semantics to Sources without a failing boundary check.

## Resolution

The Pack family now owns `PackSourceInstallationService`, `PackSourceCatalogHandler`, the `PackCatalog` schema, and Pack-specific validation. Sources exposes a provider-neutral catalog handler contract and an `ISourceCatalogContentResolver` that returns the exact authorized Source, immutable Source Version, compatible Channel Snapshot, catalog document, bounded content, and provenance.

The Source catalog browser delegates Pack projections to the Pack-owned handler. Source manifest import accepts portable catalog kinds generically, while browsing requires a registered handler. Existing API routes, JSON projection shape, preview digest, pinning, publisher checks, provenance, installation, replacement, and uninstall behavior remain unchanged.

`Agentstration.Sources` and `Agentstration.Sources.Contracts` no longer reference Pack assemblies.

## Prevention

Architecture tests now fail if either Source assembly references an `Agentstration.Packs*` assembly and verify that Pack catalog and installation types remain Pack-owned. Repository instructions, dependency documentation, the Source concept documentation, and ADR-0095 explicitly require the one-way `Packs -> Sources.Contracts` relationship.

The extensible Source catalog-handler boundary prevents future artifact families from adding their schemas or lifecycle orchestration to the generic Source module.

## Validation

- Release build: succeeded with 0 warnings and 0 errors
- Architecture tests: 72/72 passed
- Source and Pack integration tests: 93/93 passed
- API tests: 43/43 passed
- Web tests: 230 passed, 1 optional PostgreSQL integration skipped, 0 failed
- Complete deterministic functional inventory: 871 executed, 868 passed, 3 optional external integrations skipped, 0 failed
- `git diff --check`: passed
- `dotnet format` could not run in the managed environment because its Roslyn build host requires a prohibited named-pipe/socket connection; compilation analyzers reported no warning
