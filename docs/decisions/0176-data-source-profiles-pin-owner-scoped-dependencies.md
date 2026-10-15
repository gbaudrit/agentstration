# ADR-0176: Data Source profiles pin owner-scoped dependencies

## Status

Accepted

## Context

ADR-0164 introduced hierarchical Data Sources and versioned Data Source Profiles, but resolved a profile's Flow, Tool, and provider bindings from the Workspace accepting an acquisition. A descendant Workspace could therefore shadow a logical name and change the graph of an inherited profile. Immutable profile revisions also retained only logical references, so their dependency identity could not be reconstructed independently of later mutable state.

ADR-0172 makes ancestor-resource use default-deny and separates local access grants from governed resource content. ADR-0171 gives published Flows an exact owner scope, version, and definition hash.

## Decision

A Data Source Profile resolves its complete acquisition dependency graph when a revision is published. Resolution starts from the profile's owner scope, never from a future execution Workspace. The immutable revision stores the exact Flow scope, namespace, version, and definition hash plus every Tool and Tool Provider scope, namespace, UID, generation, and external identity. A separate dependency hash covers that resolved graph.

Each ancestor Resource dependency must authorize `use` from the scope that owns the referencing resource. In particular, a profile is the consumer of its Tools, and a Tool is the consumer of its provider. Data Sources persist the exact profile scope, namespace, name, and UID selected when they are created or updated. An ancestor profile or Data Source must separately authorize the descendant scope that consumes it. Because Flows remain in Flow-owned storage and do not yet expose an identity to the shared access-policy overlay, Data Source Profile publication accepts only a same-scope acquisition Flow and fails closed for an ancestor Flow. #751 owns completing that authorization boundary before ancestor Flow consumption is enabled.

Acquisition uses only the pins stored in the active immutable profile revision. It verifies that mutable Tool and provider generations still match and that the exact immutable Flow version still matches its definition hash. Missing, disabled, revoked, or stale dependencies fail before a Flow Run is accepted. Operational acquisition records, checkpoints, artifacts, and runs remain isolated in the invoking Workspace.

Pre-release databases containing profile revisions without dependency pins must be reseeded. No compatibility fallback may resolve those revisions from the execution Workspace.

## Consequences

- A Workspace-local resource cannot shadow or rebind any dependency of an inherited Data Source or profile.
- Publishing a profile fails until its Flow, Tools, providers, contracts, and required descendant grants are valid; ancestor Flows remain denied until #751 supplies their exact access-policy identity.
- Changing a Tool or provider generation makes revisions that pinned the prior generation explicitly stale; authors publish a new profile revision after reviewing the new graph.
- Activating another already-published profile revision continues to affect only future acquisitions.
- This decision supersedes ADR-0164 only where that ADR resolves logical Flow, Tool, and provider references in the consuming Workspace. Its ownership and Workspace-isolated operational-state decisions remain in force.
