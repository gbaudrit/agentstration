# ADR-0222: Logical document collections remain governed Storage configuration

## Status

Proposed

## Context

[#1153](https://github.com/gbaudrit/agentstration/issues/1153) must support mutable shared documents without changing the acquisition responsibility of Data Sources or creating another connection and permissions system.

## Decision

Configure bounded logical collections on Storage. Each declaration has a server-assigned UID, a human name, an optional document schema and an operation/limit/profile policy. Recreating a name creates a new UID. No physical table, SQL schema, connection string or arbitrary destination is a Tool argument.

A new resource would duplicate scope, lifecycle and Use governance without an independent lifecycle requirement in this increment. Making a Data Source the mutable collection would mix acquisition receipts and universal application writes. Storage-attached configuration reuses exact revision pinning and the existing resource family. A future independent lifecycle can motivate a separate resource through an additive decision.

The effective grant intersects Storage Type capabilities, Storage envelope, enabled exact collection, consumer constraints and current authorization. Existing Resource Use, resources/read, resources/write, resources/delete and runs/execute remain authoritative. Infrastructure narrows each provider binding to the invoked operation. Generic relays fix exact Storage revision, collection UID, consumer policy and query profile; source and profile substitutions are rejected structurally before invocation.

## Consequences

Collection changes participate in the Storage binding digest and require explicit reconciliation. Existing dependent snapshots and accepted consumers keep their exact references; they cannot silently rebind. API, Flow and agent Tools reuse the same document gateway. There is no universal cross-agent sharing grant: only explicitly assigned, scoped Tools and current rights authorize access.
