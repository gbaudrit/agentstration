# ADR-0241: System Explorer streams provisional authorized observations

## Status

Proposed

## Context

System Explorer initially waits for a full repeated SystemGraph observation before
receiving its first 80 nodes. Advancing the cursor repeats that reconstruction.
Large resource catalogues can exceed the Console HTTP policy: 15 seconds per
attempt and a 45-second total resilience budget. Raising that budget alone does
not provide useful progressive exploration.

## Decision

Keep the repeated-observation `/api/system-graph`, impact, comparison and
reconciliation contracts unchanged. Add a separate finite exploration stream,
`GET /api/system-graph/stream`, owned by the same application query service through
`ISystemGraphStreamService`. It consumes the existing typed family contributors
and current RBAC/Discover/Use adapter. It owns no definitions, persisted index,
publication command, execution, content access or renderer state.

The transport is newline-delimited JSON. Frames carry an observation epoch,
monotonic sequence, kind, observation time and authorized neutral nodes, relations
and represented owner scopes. Initial validation and resource-read authorization
precede the first byte. A `start` frame precedes family enumeration. The first
authorized contribution is sent immediately, followed by batches of at most 20
nodes. A second typed source pass checks observed metadata and emits relations
only after both endpoints are known and authorized, in batches of at most 100.
Root queries emit only the authorized root and its direct neighbourhood; missing
and invisible roots remain indistinguishable.

The application rechecks context before delivery and affected nodes, binding
owners and relation endpoints before each data batch. Discover-only metadata is
redacted; Use alone creates no catalogue visibility. Logical System membership
retains its Discover-only rule. Current identities and binding owners of emitted
data are checked again before completion. Changed visible metadata or detected permission
loss produces a generic terminal `error`; clients must clear the whole observation.
No hidden identities, counts, paths or contributor exception details enter frames.
Already received data was authorized when delivered; this finite read does not
promise instantaneous removal of all old data at the moment a grant changes.

Five-second application heartbeats keep a slow metadata enumeration distinguishable
from a broken connection without revealing progress through hidden resources.
They recheck the current request context and do not trigger layout. The Console
uses `ResponseHeadersRead` and a 30-second idle deadline, reset by each frame,
instead of buffering the entire response under an ordinary request deadline.
Disconnect/navigation cancellation reaches pending metadata I/O before iterator
disposal. Mid-body failures are not automatically replayed.

Per-request working sets remain bounded to 10,000 authorized nodes and 20,000
relations. Reaching a bound completes a truncated observation with explicit UI
feedback. A failed or incomplete body is discarded. There is no resume cursor or
reused server graph cache. `stream:<epoch>:<sequence>` in the Console export is an
observation identifier, never the stable digest/read-set proof of the strict API.
All exported observations remain `globallyComplete: false`.

The Console starts loading after its first interactive render, leaving prerender
as a fast loading shell. Logical System listing is independent of the graph body.
Received nodes stay visible while more arrive; local windows contain at most 80
nodes and preserve a selected neighbour root. Advancing a window does not query
the backend again. Export contains only the authorized data actually received.
Changing context/namespace, a sequence gap, stream error or incomplete EOF clears
old data. Async results from an older request cannot replace the new observation.

LikeC4 retains its current mount while one layout runs and keeps only the latest
pending update. Completed incremental layouts can appear before the source stream
finishes. Unchanged windows and operational note values avoid layout work.
Navigation/window changes dispose the old mount and prevent a late layout from
committing. The existing 100-node adapter guard and 20-second layout deadline
remain; no graphical property enters semantic contracts or resource storage.

## Consequences

First visible data no longer waits for complete cross-family reconstruction, and
large catalogues are explored through bounded visual windows. Relations and some
neighbours can arrive later than nodes. Family enumeration and authorization still
cost work; this change does not promise constant-time complete reconstruction.
The stream is a provisional exploration view, not a consistent global snapshot
or a safe dependency-adoption plan. Actions continue to use strict owner-family
commands and fresh reconciliation evidence.

Deterministic tests gate a slow source behind a barrier and prove early delivery,
heartbeat/cancellation, current permissions, generic failures, root/namespace
filtering, limits, native SQLite deletion, idle eviction, progressive localized
rendering, stale-context rejection and renderer coalescing/disposal. Desktop/mobile
implementation smoke remains required. Reusable journey #1201 stays deferred until
explicit maintainer UX validation and merged owning UX PRs.
