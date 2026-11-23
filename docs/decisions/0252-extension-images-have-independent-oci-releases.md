# ADR-0252: Extension images have independent OCI releases

Status: Accepted — 2026-11-23

## Context

Packaging milestone #16 supplies both private preview images and official public
Docker Hub distributions. Extensions evolve independently, and their packaging
must stay external to the Agentstration Runtime. Existing generic and specialized
Dockerfiles should remain reusable. Foundry is the first vertical (#1291).

## Decision

Use a versioned image catalogue and shared external build/publication scripts
(#1289). Implement catalogue entries only when an executable packaging vertical
exists. Each extension owns its Semantic Version; product, AEP protocol and
contribution versions remain distinct. Foundry's version file feeds its assembly,
advertised AEP extension identity and OCI metadata. Its initial version preserves
the existing `1.0.0` identity.

### Initial release baseline clarification — 2026-11-23

Before the first independently packaged release, the maintainer chose `0.1.0`
as the initial version for each extension, including Foundry. This supersedes
carrying forward Foundry's earlier development identity `1.0.0`. Subsequent
extension versions remain independent of one another and of Agentstration.
AEP protocol and contribution versions are unchanged by this baseline decision.

### Artifact delivery

Build one OCI archive with pinned base images, source/version labels, provenance,
SBOM, digest and archive checksum. Smoke that artifact in a disposable local
registry, then copy the same archive using digest-preserving OCI tooling. Private
preview publication uses GHCR; official extension tags trigger publication to
both GHCR and public Docker Hub. Existing product release tags and server Docker
Hub publication remain independent.

Official tags use `extension/<id>/v<version>` and must match the extension version
on a commit reachable from main. Private preview tags identify an exact source
commit. Publication rejects differing content under an existing release tag.
Registry credentials are available only to publication jobs, not PR builds or
extension workloads. GHCR package privacy is checked before and after publication.

## Consequences

An unchanged extension retains its version across product releases. A product
release can later recommend tested extension digests without imposing lockstep
versioning. The first image platform is Linux amd64; native/platform additions
are owned by their extension packaging tasks.

Deployment-time extension selection, server Bootstrap, infrastructure and provider
configuration remain outside image packaging. The image smoke proves protected
AEP discovery and restart; full authority enrollment and live inference require
their separate integration validation. Official publication requires configured
Docker Hub credentials/public repositories and a registry that preserves OCI
indexes and attestations.
