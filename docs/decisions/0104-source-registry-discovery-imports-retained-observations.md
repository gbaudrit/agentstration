# ADR-0104: Source registry discovery imports retained observations exactly

- Status: Accepted
- Date: 2026-09-10

## Context

Registry refresh retains compatible metadata shards without importing SourceVersion manifests. Several enabled registries can publish the same portable Source identity and opaque version, either with equal bytes or conflicting digests. Accepting a client-supplied URL or digest at import time would bypass the retained observation, current trust policy, and registry transport protections.

## Decision

Discovery reads only current last-known-good cached shards, merges results deterministically by `publisher/name`, and groups exact opaque versions without assigning global semantic order. Every equal observation remains visible. A shard's `latest` marker is reported only on that shard observation. Different manifest digests for one identity and version are an explicit blocking conflict.

An import selects the server-retained registration UID, observation ID, shard name, Source identity, and opaque version. Agentstration resolves that tuple again, requires the registration and cached observation to remain available, reapplies current policy, revocation, and conflict decisions, resolves the manifest beneath the Registry `/v1/` origin, and retrieves only that selected manifest. The canonical manifest identity, version, and digest must match before the normal Source import service runs.

The immutable SourceVersion and import record retain the configured and observed Registry URLs, index and shard digests, shard entry, timestamps and validators, publisher assertion, expected identity/version/digest, final manifest URL, and trust evaluation snapshot. Source refresh cannot silently replace this Registry provenance; a later definition requires another exact observation import. Pack provenance carries the Source's Registry observation while independently retaining and validating Channel, provider revision, Snapshot, catalog, entry, and archive evidence.

## Consequences

Registry refresh remains metadata-only, discovery does not fetch manifests, and exact import does not materialize Channels. Equal observations are deduplicated for presentation without losing provenance, while conflicts fail closed. Disabling or deleting a registration never deletes already imported Sources or Pack history, but prevents a new import through that registration. Bootstrap and Pack consumers can trace their SourceVersion back to its exact Registry observation without treating Registry transport as Channel transport.
