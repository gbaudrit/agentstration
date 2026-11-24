# ADR-0255: SQLite previews consume private OCI deliveries

Status: Accepted — 2026-11-24

## Context

Preview #1281 needs a small locally executable deployment before portable
provisioning (#1282), Azure (#1283) and manual remote deployment (#1284).
ADR-0252 and ADR-0254 establish independently versioned OCI components and
digest-preserving private delivery. The existing Development Compose topology
builds from source and starts optional extensions. It is not the minimal preview.

## Decision

Add the authoritative server to the existing catalogue as `applications/server`,
kind `server`. Its OCI version follows the product's existing VersionPrefix and
VersionSuffix in `Directory.Build.props`; no independent server version file is
introduced. Reuse the root Dockerfile, make its SDK/runtime bases pin-compatible,
and retain its embedded Bootstrap catalogue and direct dotnet entrypoint.
This catalogue entry serves private previews. Official product tags and the
existing Docker Hub release workflow remain independent.

Keep preview orchestration under `deploy/preview` and `scripts/preview`, outside
the Runtime. The first composition contains only the authoritative server with
its embedded Console and an independent MAF Worker. SQLite and deterministic AI
are the defaults. There is no inference service, AEP extension, database broker,
Azure API or separate Workplace shell in this increment.

Consume explicit server/Worker delivery receipts and record immutable image
references, individual component versions/source SHAs and stable workload IDs.
Components may have different source revisions; independent version cycles do
not imply lockstep product releases. Actual pair validation records the exact
combination tested. The deployment-level source SHA identifies the server.

The external helper generates a protected host key/configuration, mounts them
read-only and keeps all server data in a per-project named volume. The Worker
receives no database mount or connection setting. Initial Bootstrap is disabled
by default, existing manual initialization remains available, and optional initial
configuration uses the existing Bootstrap boundary. No preview-specific product
endpoint or initialization feature is added. Host ports bind only to loopback.

## Consequences

One built server archive can be validated and promoted through the existing
GHCR privacy gate without rebuilding. The server CI smoke uses the real preview
composition, verifies the unbootstrapped default, explicitly bootstraps disposable
test data with a generated password, executes through AWP and preserves data and
identity across container recreation. A same-source Worker fixture is recorded
in evidence but not implicitly published as another component release.

SQLite composition is the first #1281 increment. PostgreSQL, extension selection,
separate shells, remote lifecycle, protected URLs and automated retention remain
their own subsequent verticals. The recorded image set is an input to #1282;
provider, VM size, TTL and infrastructure identifiers do not leak into packaging.
Operators retain matching state/volumes and active image manifests; no automatic
schema rollback, ownership inventory or cloud cleanup is implied by this helper.
