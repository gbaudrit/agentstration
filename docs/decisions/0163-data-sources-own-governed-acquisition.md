# ADR-0163: Data Sources own governed acquisition independently from Knowledge

## Status

Accepted

## Context

ADR-0158 and ADR-0161 placed origin configuration and acquisition composition on Knowledge Sources and Knowledge Source Profiles. That model couples access to an external origin with one Knowledge projection. It prevents one acquired origin from feeding several projections and makes otherwise generic transport Tools appear Knowledge-specific.

Agentstration already has an ancestor-only resource hierarchy across instance, tenant, and Workspace scopes. Flows and executable Tools remain Workspace-owned, while reusable governance templates may belong to a broader scope.

## Decision

Introduce `DataSource` and `DataSourceProfile` as first-class Resource Management families at instance, tenant, and Workspace scope. A Data Source owns origin identity, configuration and lifecycle. A Data Source Profile owns the configuration schema, a logical acquisition Flow reference, governed Tool and provider bindings, limits, egress policies and compatibility requirements. Profile drafts publish immutable revisions and one revision may be active.

A Data Source may reference only a profile visible from its own scope. Instance resources therefore cannot depend on tenant or Workspace profiles, tenant resources cannot depend on a Workspace profile, and lateral references are rejected by the shared scope resolver.

Acquisition is requested from a Workspace execution context. The active profile revision is resolved without copying it. Because Flows, Tools and Tool Providers are Workspace-owned, their logical references are resolved at acquisition acceptance in that consuming Workspace. The acquisition record then pins the exact Data Source scope, UID and generation; profile scope, UID, generation, version and definition hash; Flow version; Tool and provider scopes, UIDs and generations; and the effective limits and policies. Later edits or activations affect only future acquisitions.

The Flow contract is `data.source.acquisition/v1`. It receives the trusted Data Source identity and generation, exact profile evidence, source configuration, caller context, correlation identity and caller parameters. It returns a bounded artifact manifest. Artifact validation remains governed and Workspace-isolated. Knowledge projection from those artifacts is a separate concern.

`KnowledgeSourceProfile` and direct Knowledge acquisition remain readable and executable as a compatibility path while migration is staged. A newly created Data Source may retain an immutable `migratedFrom` input containing the exact legacy resource kind, scope, namespace, identity, UID and generation; this is migration provenance, not a live dependency. New acquisition composition belongs to Data Source Profiles. This decision supersedes the acquisition-ownership portions of ADR-0158 and ADR-0161; their historical provenance, Snapshot, retrieval and Knowledge Tool decisions remain valid.

## Consequences

- The same governed origin can feed zero, one or several downstream projections.
- Generic REST, crawler and connector Tools no longer need Knowledge-specific identities.
- Broader-scope profiles are reusable governance templates without gaining authority over descendant resources.
- Every accepted acquisition remains reproducible from exact scope and revision evidence.
- Bootstrap can create profiles and Data Sources at all three existing scopes.
- A later migration moves existing Knowledge acquisition configuration into Data Sources without rewriting historical runs, artifacts or Snapshots.
