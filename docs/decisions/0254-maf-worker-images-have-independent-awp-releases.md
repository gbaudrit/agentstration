# ADR-0254: MAF Worker images have independent AWP releases

Status: Accepted — 2026-11-23

## Context

ADR-0168 places MAF execution on an independent AWP Worker, distinguishes Worker,
protocol, capability and material versions, and excludes server storage access.
Packaging #1300 needs the same build-once delivery machinery as Foundry (#1289)
without classifying the Worker as an AEP extension. Docker Hub delivery is deferred.

## Decision

Add an implemented `awp-worker` catalogue family beside AEP extension entries.
Reuse the existing publisher, digest-preserving OCI archive, private GHCR checks,
provenance, SBOM and pinned bases. Reuse `Dockerfile.worker`; the initial supported
platform is Linux amd64. Tooling remains external to the Runtime.

The MAF executable owns `version.txt`, initially `0.1.0`. Its software/assembly and
OCI release versions follow that file independently from Agentstration and other
executables. AWP stays at `1.0`, workload authentication at `1`, and the MAF runtime
capability at `1.0` with execution material `1.0` and `1.1`. Registration validates
these distinct contracts; no lockstep product version is inferred.

Use catalogue ID `maf-worker`, image name `worker-maf`, and official tag namespace
`worker/maf-worker/v<version>`. Private previews retain exact source tags and actual
deployments retain digests. New Worker deliveries use neutral `component` metadata;
existing AEP delivery metadata and the `--extension` CLI spelling remain supported.

A real SQLite authority/Worker Compose smoke must validate liveness before
registration, authenticated readiness, a deterministic assignment and a new session
for the same Worker after container recreation. SharedKeyFile identity is supplied
by the orchestrator and mounted read-only. The Worker receives no server database
mount or connection setting. Pairing remains the existing optional manual path.

## Consequences

One validated archive can be promoted without rebuilding. A catalogue entry alone
does not create a private package or publish a release. New GHCR packages must be
initialized privately and grant repository Actions Write access before application
upload. Docker Hub publication and official tags remain deferred; registry evidence
is recorded separately from local-registry build and Compose validation.

Foundry-only edits retain their own image validation. Worker/AWP edits select the
Worker image; shared packaging inputs select both. Existing required CI checks
aggregate the selected matrix and keep conservative product validation for Worker
and mixed/shared product changes. No provider, broker or cloud service is added.
