# ADR-0119: Resource Plan profile choices are durable review drafts

## Status

Accepted

## Context

The reviewer selects Model and Runtime Profiles for planned Agents. Those selections previously lived only in the Console component until a ChangeSet was created. Leaving the page lost partial selections, while a materialization preview looked similar to a recorded ChangeSet.

## Decision

Store a separate, Workspace-scoped profile selection draft for each Resource Plan. The draft contains partial per-role choices, the plan revision, and its own ETag. Each Console selection saves the draft through an authenticated, Workspace-scoped REST endpoint. A stale ETag or plan revision rejects concurrent or obsolete changes. Reopening the plan restores matching-revision choices; a new functional revision requires the reviewer to select profiles again.

This draft does not change functional plan content or revision and does not create a ChangeSet. Materialization still resolves and checks the chosen profiles at preview time. A ChangeSet remains the durable, digest-pinned proposal that can be validated. The Console distinguishes these steps and can restore bindings from an existing ChangeSet when no current draft exists.

## Consequences

- Partial selections survive navigation and restart on the authoritative server, independent of browser storage.
- SQLite creates the draft table for existing local stores; PostgreSQL adds it through a migration.
- Draft writes are bounded to roles in the current plan, and another Workspace cannot read or edit them.
- Validation and later application continue to use recorded ChangeSets, never a mutable selection draft.
