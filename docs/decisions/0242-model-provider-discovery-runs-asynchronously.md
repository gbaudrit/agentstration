# ADR-0242: Model Provider discovery runs asynchronously

## Status

Accepted

## Context

Bootstrap created Model Providers without populating the governed Model inventory.
Console creation/refresh and provider health reads waited on upstream services.
A provider outage therefore affected administrative responsiveness and could leave
dependent Model Profiles pointing to models that had never been discovered.

## Decision

The Models family owns discovery scheduling and processing. Provider creation and
updates queue an observation; startup scans recover providers without observations.
A separate instance-owned `ModelProviderDiscovery` resource stores provider UID and
definition generation, operation state, health, attempts, success, next retry, lease,
invariant error code and reconciliation counts. Observations never modify the
governed provider ETag/generation or descendant access policies.

Commands accept asynchronously with HTTP 202. Stored-state GETs never probe the
provider. The Console polls stored observations and displays never/empty/failure
states separately. Extension editor metadata is loaded independently with a short
budget so an unavailable extension cannot block the retained provider overview.
Existing remote-dependent Value Binding validation remains part of configuration
validation; it is distinct from inventory discovery.

An ETag claim coalesces per-provider work across workers. A bounded lease recovers
interrupted running operations; definition changes invalidate old observations.
Each discovery attempt has a thirty-second budget. Success schedules a five-minute
refresh; failure uses exponential backoff from five seconds to five minutes.
Up to four providers are processed concurrently. Only bounded invariant errors are
stored in the operation; the existing reconciliation retains the last model catalog.
Provider UID/generation is checked before applying a remote observation.

Staged Bootstrap's Model Profile handler waits for its exact required model before
creation, including retained definitions on create-only reapplication. The wait is
bounded independently by ModelDependencyTimeout. Manual preview is still read-only.
The discovery worker starts before execution readiness and owns its System scope.
This extends ADR-0240: after topology/policy setup, the startup fence permits an
explicit administrative route allowlist, retaining ordinary authentication, scope
and authorization. Execution stays fenced. Dependency failure keeps administration
available with readiness false; repair and restart retry the fenced initializer.

## Consequences

- Provider outages cannot hold the model inventory HTTP response or Console page.
- A successful empty catalog is represented independently of no prior discovery.
- Successful provider health and failed catalog discovery can coexist.
- Optional providers do not block staged Bootstrap's required-model wait.
- Extension process supervision and model downloads remain external concerns.
- The discovery commands change from synchronous 200/diff to 202/status; clients
  must poll status before assuming the inventory has been reconciled.
- This is a family-owned lifecycle using existing resource storage, without a new
  broker, scheduler framework or relational schema migration.

Related issues: #1239 and deferred UX scenario #1240.
