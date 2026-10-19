# ADR-0114 — Console Entry discovery projects canonical execution readiness

## Status

Accepted

## Context

ADR-0113 separates Workspace ownership from Entry exposure. A Console-targeted Entry can therefore be discovered outside the Workspace currently selected for administration, but presentation eligibility alone does not establish that its pinned Flow remains executable. The Flow may have been disabled or its resource/version may be unavailable after publication. Resolving that state in the Console UI would duplicate server policy and could disagree with invocation.

The independently hosted Console BFF from ADR-0111 does not yet own interactive user delegation. Entry discovery must remain an authoritative Work API capability and must not add temporary BFF credentials or resource access.

## Decision

The Work application boundary resolves every authorized, exposed Entry to an execution-readiness projection. The initial states are `Executable`, `Disabled`, and `Unavailable`, accompanied by a stable reason code when invocation is not possible. Resolution uses the Entry's owning Workspace and exact pinned Flow version. A missing Flow or version is unavailable; a disabled Flow is disabled. Entries configured for an immediate response without Task creation do not require a Flow lookup.

The shared discovery endpoint returns the ordinary public Entry contract plus this readiness projection. Console clients request `surface=Console` and invoke through the existing Workspace-qualified interaction route with `surface=Console`; no Console-only resource store, Flow resolver, or execution path is introduced. Invocation reuses the same readiness resolver and fails closed if the target is no longer executable between discovery and submission.

Draft-only and unauthorized Entries remain absent rather than being disclosed with a status. Presentation metadata and the pinned target remain sufficient for the Console Assistant shell to render an Entry and address its owning Workspace.

## Consequences

Console UI code can disable unavailable Entries and localize stable reason codes without loading Management internals. Discovery and submission cannot disagree about disabled or missing pinned targets, while all Flow and resource resolution stays inside the owner Workspace.

The separated Console will consume the same client after the server-side session and audience-bound delegation increments of the BFF roadmap are delivered. This decision neither completes nor bypasses that trust boundary.
