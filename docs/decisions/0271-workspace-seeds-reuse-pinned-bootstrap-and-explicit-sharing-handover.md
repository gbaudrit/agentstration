# ADR-0271: Workspace seeds reuse pinned Bootstrap and explicit sharing handover

## Status

Accepted — implementation for [#1347](https://github.com/gbaudrit/agentstration/issues/1347).

## Context

A new Workspace cannot use its Project sharing connector to acquire the configuration that creates
that connector. Source-backed Bootstrap already owns bounded snapshot materialization, profile
parsing, exact targeting, review digests, configured inputs and durable administrative applications.
ADR-0246 requires explicit retry after process interruption; ADR-0266 keeps sharing synchronization
separate from Bootstrap application and local execution authority.

## Decision

Accept one explicitly Workspace-targeted ZIP package or a bounded raw JSON pointer, supplied directly
or at an explicit public HTTPS JSON URL. The pointer identifies one HTTPS ZIP URL and its SHA-256.
No URL is interpreted as a directory listing. The first package format contains one root seed
manifest and exactly one Workspace Bootstrap profile. Source artifact storage, archive validation,
materialization and the existing catalog/parser are reused. Acquisition requires an installed
instance, an authenticated Platform administrator and an existing active ordinary Workspace.

Persist a private receipt by Workspace, actor and request identity. The receipt retains immutable
artifact integrity, manifest/version/revision, profile digest and acquisition provenance. Transport
responses omit artifact storage keys. Seed selection cannot be mixed with catalog, Source or
multi-target plans. Every reload checks the target, actor and cached artifact digest. Only the
existing Bootstrap worker and family handlers create resources.

Preview and execution require current installed extension contributions, Instance ModelProviders,
Instance RuntimeProfiles with Discover/Use grants, and a recently registered compatible native
worker. Stable prerequisite identities/access and capability availability enter the review digest;
worker heartbeat timestamps do not. A Workspace seed never installs software, creates Instance
capabilities, changes topology or grants access. The normal catalog retains its broader scopes.

Tools owns a Workspace protected-relay Bootstrap handler. It resolves semantic source references,
validates through the real Tool publication lifecycle and publishes immutable versions. Typed
Secret selectors reuse Bootstrap preparation; secrets remain local. ProjectSharing may bind the
published sharing, repository and issue-operation relays. Its participant identity is an explicit
local configured input rather than a copied Workspace identity. Existing compatible resources are
preserved, including operator edits; incompatible immutable publications or connections conflict.

After resource application succeeds, the same administrative application explicitly hands over to
WorkProjectSharingService for first pull and any manifest-listed, digest-pinned draft plan imports.
Pending, partial and successful handover receipts retain Project identity, provider revision and
completed imports. A failure retains created resources. Process restart interrupts an in-flight
manual application as in ADR-0246; a new reviewed application using the same private receipt retries
create-only handlers and existing idempotent imports. There is no automatic provisioner or bootstrap
replay on subsequent sharing pulls. Handover never activates or admits a plan.

## Consequences

Seed ingestion is usable before any Source or Project connection exists, with no new storage engine
or server process. Public-file acquisition uses the existing DNS/IP policy and refuses redirects,
credential-bearing URLs, private origins and unbounded content. Native account setup remains a
worker prerequisite; the server does not inspect remote CLI files or infer account credentials.

The Console extends `/settings/bootstrap` with localized input, source inspection, prerequisites,
review and retained handover evidence. The first Project receipt link exposes the existing authorized
JSON record; no new Project editor is introduced. Seed replay remains private to its original actor
and target. The deferred reusable UX journey is tracked by #1350 and still requires maintainer UX
validation and implementation merge; ordinary regression tests and browser smoke remain required.
