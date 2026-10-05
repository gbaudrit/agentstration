# ADR-0162: Core Knowledge Source Profiles are protected local defaults

## Status

Accepted

## Context

ADR-0161 introduced reusable, versioned Knowledge Source Profiles, but a fresh Workspace still had no immediately usable profile. A universal ingestion Flow cannot represent Web fetches, REST responses, and existing Artifact imports with one honest configuration schema and one implementation. Optional AEP extensions must enrich the catalog without becoming a prerequisite for local execution.

ADR-0069 treats the generic `agentstration.io/builtin` annotation as descriptive provenance, not as an authorization boundary. The three profiles in this decision need a stronger lifecycle because their definitions are code-owned compatibility contracts that Agentstration must be able to evolve predictably.

## Decision

Every Workspace reconciles three reserved, system-owned profiles: `web-builtin`, `rest-builtin`, and `artifact-import-builtin`. Each profile publishes an immutable revision and pins a specialized ingestion Flow, the shared local retrieval Flow, the built-in filesystem Storage Flows, and its governed implementation Tool.

The Web and REST profiles fetch one bounded public HTTP(S) resource. The transport resolves and connects only to public addresses, revalidates every redirect, rejects URL credentials and fragments, limits redirects, duration and response bytes, and accepts only profile-specific textual media types. The initial REST capability is an unauthenticated GET and cannot send arbitrary headers or credentials.

The Artifact import profile consumes explicit durable Artifact identifiers already governed in the Workspace. It does not copy data through the network.

The profile identities are reserved by the Knowledge family. Interactive create, update, publication, activation, application, delete, and YAML replacement operations cannot mutate those identities. System reconciliation is the only writer. The protection is based on the reserved identities and system Control Plane context, not on mutable annotations. Metadata still records `agentstration.io/builtin: "true"`, `agentstration.io/origin: core`, and `agentstration.io/owner: agentstration.knowledge` for discovery and diagnostics.

Reconciliation creates a missing profile and its initial immutable revision, then leaves an existing core-owned profile untouched. It rejects a non-core resource occupying a reserved identity. Future code-owned revisions must retain historical revisions and use an explicit upgrade or repair operation rather than silently replacing an active profile. Administrators customize behavior by creating a separate profile, optionally applying a built-in revision through the ADR-0161 application mechanism.

## Consequences

- Fresh, existing, interactively created, and declaratively bootstrapped Workspaces receive the same executable local defaults through the shared Workspace provisioner.
- Profiles can share retrieval and storage infrastructure while retaining source-specific ingestion contracts.
- Optional extensions add independent profiles or may be explicitly applied to administrator-owned profiles; they do not mutate the core defaults.
- Private, loopback, link-local, authenticated, JavaScript-rendered, paginated, or multi-page acquisition requires a different governed Tool and profile.
- Core profile evolution requires a new immutable version and an explicit upgrade/repair surface; startup does not erase local provenance or silently switch active behavior.

