# Project sharing

A private Project can connect to one portable sharing space while retaining local Work execution.
The default local Project needs no connector. See [ADR-0266](../decisions/0266-project-sharing-preserves-portable-plans-and-local-execution-authority.md).

## Configure a connection

Provision the optional [Git extension](git-operations.md), discover and enable its Tool, then publish
a protected relay with the native Git capability metadata. Bind `/repository`, `/authentication`
(`Basic` or `Bearer`), `/username`, `/request/refName`, `/allowedRefPrefixes` and
`/allowedPathPrefixes` as literals, and `/credential` as a Secret. Sharing accepts an exact published
Tool reference, not arbitrary native arguments or a caller-provided credential. Repository access
is checked again through the normal governed Tool pipeline for every operation.

The Project definition's `sharing` property contains `projectId`, `participantId`, `bindingName`,
`location` and `formatVersion: "2.0"`. Typed identifiers use the existing `{ "value": "<uuid>" }`
wire representation. Both instances use the same portable Project ID and logical location; each
participant has its own stable participant ID, private Workspace/Principal and local Tool identity.
`bindings` contains the named `Sharing` binding with its exact scoped Tool UID/generation.

A Workspace Bootstrap profile may instead declare:

```yaml
apiVersion: agentstration.io/v1
kind: ProjectSharing
metadata:
  name: shared-coding
  namespace: default
definition:
  projectId:
    value: 11111111-1111-4111-8111-111111111111
  participantId:
    value: 22222222-2222-4222-8222-222222222222
  location: data
  toolName: agentstration.git-connection.v1.0.0
  toolNamespace: default
  displayName: Shared coding
```

The protected Tool must already exist in the target Workspace. Bootstrap pins that local identity,
creates once, and skips an equivalent connection. A changed connection reports a conflict;
local edits are not automatically overwritten. No remote profile is applied by synchronization.

## API workflow

All routes are beneath `/api/workspaces/{workspace}/projects/{projectId}/sharing` and enforce the
current Workspace and private Principal. Calls that contact the connector require Runs Execute
and current binding Discover/Use authorization.

| Route | Behavior |
| --- | --- |
| `POST /pull` | Body `{ "revision": null }` reads current state; an exact revision reads history. Returns a stored projection. Safe to repeat after restart. |
| `POST /operations` | Body `{ "id": "<uuid>", "expectedProjectVersion": 1, "changes": [...] }` durably reserves a publication before contacting the provider. Returns `202` and operation location. |
| `GET /operations` | Explicit `skip`/`take` (1–100), stable chronological ordering and `hasMore`. |
| `GET /operations/{id}` | Pending, uncertain, conflict, rejected or succeeded state; no credentials or local provider arguments. |
| `POST /operations/{id}/resume` | Recovers the saved native attempt first, then resumes only when its outcome permits it. |
| `POST /plans/{planId}/import` | Body `{ "expectedDigest": "<sha256>" }`; explicitly imports current shared declarations into ordinary local draft Work. |

A change is `{ "key": { "domain": "Contribution", "name": "<uuid>" }, "expectedVersion": 0,
"content": { ... } }`. Domains are `BootstrapProfile`, `ProjectMetadata`, `Plan`, `Contribution` and
`Usage`, plus `Organization` for independent logical epic membership. Product contracts define each payload; unknown typed fields are rejected. Plan content
must match an associated, authorized local plan and its immutable task declarations. Allocation
covers each atomic plan member once. A shared plan may include complete task trees while selecting
only some members, preserving the ordinary WorkPlan hierarchy rules.

Use a new operation ID for a new intent. Replay an existing ID only with the same normalized
changes. Contributions and usage use stable UUID record names and immutable content; identical
records count once. Usage supports legacy bounded named counters and [typed canonical Work accounting](work-usage.md), including immutable correction lineages. Contributions carry
explicit evidence and artifact references; neither implies functional acceptance of local Work.
Bootstrap records carry portable declarations, not credentials or physical scope references.

Current limits are 64 records, 16 KiB content per record, 1 MiB per snapshot, 32 changes and
16 KiB of changes per publication command, with a 40 KiB index/record publication envelope. The Git transport also enforces the protected relay's
input limit. Split independent record additions into separate identified commands when necessary.
Versions of changed records and Git history are retained; there is no delete/GC endpoint.

## Concurrency and local authority

Publications use compare-and-swap. Disjoint additions retry against a fresh index after a proven
conflict. A stale plan or allocation version reports `409` and retains its operation for inspection.
Uncertain effects are recovered using the saved native identity; they are never silently rebased.
Changed connection configuration requires explicit reconciliation before a saved operation resumes.

Pull does not apply configuration, activate plans, run tasks or accept outcomes. Plan import never
replaces a divergent local proposal or immutable task tree. Reconcile such differences explicitly
through the ordinary authoring services before retrying. Before a new attempt, the existing
WorkPlan admission gate verifies fresh shared plan/allocation evidence. Previously admitted
attempts retain their captured evidence and local authority.

No live Git host or model is required by the offline suite. Local fixtures exercise authenticated
native Git, protected MCP/Secret calls, independent Work stores, concurrent publication, historical
reads, reconnect/restart, stale assignments and denied scope access.

## YAML representation (format 2.0)

New spaces use UTF-8 YAML: `<location>/index.yaml` and immutable
`<location>/records/<domain>/<identity>/<version>-<content-digest>.yaml`.
Use `data` as the tracking repository location. Configuration and Flow definitions stay under
`bootstrap/`; they are not copied into the work directory. API bodies and local databases retain
their existing JSON contracts.

The YAML reader accepts one bounded JSON-compatible document and rejects duplicate/unknown
keys, aliases, anchors, explicit tags, invalid UTF-8 and excessive nesting. Typed normalization
precedes content-digest validation. Integers and decimal values retain precision; quoted strings
and multiline instructions retain their meaning. Record index sizes measure actual YAML bytes.
Exact Git revisions and archive digests retain transport integrity independently of business IDs.

Format 1.0 JSON spaces are not automatically converted. Both participants must upgrade together.
Select a new reviewed format-2.0 location or explicitly convert and review the complete index and
records while retaining the original Git history. A legacy `index.json` at the selected location
returns `sharing_format_upgrade_required`; it is never treated as an empty new space. Never reset
an existing index or replay an uncertain old operation into a different format/location.

See [ADR-0274](../decisions/0274-tracking-documents-use-strict-versioned-yaml.md).
