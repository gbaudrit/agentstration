# ATC.mcp.v1 contract

## Identity and boundaries

The contract exposure is `agentstration.tracking.contracts.mcp.v1`, revision `1.0`.
The final `v1` names the compatible major; `1.<minor>` is the precise revision.
Only canonical nonnegative minor numbers are valid. Revision, MCP negotiation,
provider implementation build and SDK package distribution are separate dimensions.
Clients accept only supported schemas and an exact selected declaration revision;
advertising a newer revision alone does not establish compatible behavior.

ATC specifies external tracking. Project/plan/epic lifecycle, local functional
acceptance, one-issue/one-plan policy and scheduling stay in the product. AFC owns
forge/review behavior; generic Git owns code transport/publication; sharing owns
portable YAML. One extension can expose ATC alone or ATC and AFC independently.
REST is not implemented by this SDK.

## Selection, authorization and discovery

Every Tool input has `{ target, arguments }`. `AtcTargetRef` carries `providerId`,
`targetId`, `bindingId` and `revision`. Provider identity names a configured authority
instance, not merely a vendor. Physical connection configuration, credentials and
the meaning of target IDs belong to the host/provider; no repository locator is
required. Item identity is the provider/target/item triple and is independent of
configuration revision.

Selection is not authority. A host authenticates MCP and resolves a current trusted
subject, authorization revision, allowed operations and permitted related-target IDs
for the exact binding on **every** call, including describe, reads and recovery.
Unknown bindings/revisions fail with `forbidden`, without falling back to another
target. Cross-authority native relations are unsupported in this binding. Cross-target
relations require explicit current authorization; providers must also enforce their
native access rules. Returned relations and retained receipt snapshots are checked
against the current related-target grant.

The declaration contains contract identity/revision, Tool prefix and eight schema
digests (four input/output pairs). The optional AEP adapter uses this identity as a
metadata key on each existing Tool contribution. Its validator checks the declared
MCP server, complete unique mappings, schemas and revisions. A label is not a grant or
proof of conformance. The typed MCP client validates the actual schemas again.

## Operations

| MCP Tool suffix | Responsibility |
| --- | --- |
| `describe` | Current target, declaration, implementation, effective operations/limits and guarantees |
| `list` | Bounded page of native observations with an opaque continuation |
| `read` | One exact item observation |
| `mutate` | Discriminated create/update/comment/relate/transition intent, or recovery of that exact intent |

The default prefix is `atc`. Targets advertise only implemented functional operations.
Generic MCP Tool schemas remain stable across targets; `describe` reports the actual
authorized subset. There is no arbitrary vendor API passthrough.

Observations retain native state ID and optional completion reason. Transition IDs
and destination states are native observations, not commands to assign arbitrary
state strings. Closed, abandoned, fixed and verified are not interchangeable, and no
ATC state automatically accepts local work. Classifications are opaque IDs.

Update supports explicitly advertised `title`, `body` and additive
`classifications.add` fields. Missing title/body preserves it; an empty body clears
content. Null does not request deletion. This increment has no classification removal,
item deletion, arbitrary patch or automatic state normalization.

Relations distinguish parent, child, related, blocks and blocked-by. Supported relation
kinds and their inverse direction must retain their native meaning. Providers return
complete bounded observations for their advertised relation kinds. An unavailable or
incomplete observation fails, rather than looking like an empty dependency set.
Unsupported native relations are not silently represented by comments or labels.

## Revisions, effects and recovery

Every non-create change carries the observed item revision. `concurrency` explicitly
declares **None**, **Observation** (preflight only), or **AtomicRevision**. A revision
field alone is not an atomic compare-and-swap guarantee. Native provider limitations
must remain visible through capabilities and errors.

A mutation carries request ID, operation key, exact typed change, mode (execute or
recover), and intent digest. The digest is `sha256:` plus lower-case SHA-256 of the
SDK JSON serialization of `{ target, change }`, with the common serializer options.
It excludes per-attempt request ID/mode. List ordering in changes is significant;
retries retain the original material. The server verifies the digest before dispatch.

Providers bind stable operation keys to exact target, subject and intent. They must
serialize conflicting concurrent attempts and never reuse a key for another intent.
Receipt statuses are Applied, Rejected, Uncertain or Missing. Applied includes the
original item observation; Rejected/Uncertain include bounded technical error codes.
Missing is valid only for recovery and requires positive provider proof that this
operation was not dispatched. Search absence after a sent request is not such proof.

Recovery scope is None, Process or Durable, with an explicit retention window.
Expired/lost state is not Missing. A provider must fence the former authority/target
generation or return Uncertain after state loss. Process recovery promises neither
crash durability nor cross-instance coordination. Durable requires actual retained
state and native recovery guarantees, not merely an advertised flag.

The client performs no automatic mutation retries. Lost responses, cancellation,
timeout or malformed post-effect results can leave an effect; the caller retains the
original operation and explicitly recovers it. It must not select another provider or
new key as an implicit retry. Historical receipts do not bypass current authorization.
Recovery may return an old snapshot; it is not a fresh read or current readiness proof.

## Bounds and failures

Requests are at most 131,072 UTF-8 JSON bytes; replies at most 262,144, narrowed by
target policy (minimum supported reply budget 4,096). Hosts must enforce raw HTTP and
transport buffering limits too; SDK checks on materialized MCP results are an
additional boundary, not a network buffering guarantee. Calls have 30-second SDK
deadlines and propagate caller cancellation.

IDs are bounded to 128 characters, titles 512, body/comments 32,768 characters,
classifications 32, relations/comments 100, available transitions 32 and pages 100.
Continuations are at most 4,096 characters, opaque, integrity-protected and bound to
target/configuration, subject, grant revision, query/page size and expiration. Providers
must reject reused continuations under a changed context.

JSON object property order is insignificant, including the mutation's `operation`
discriminator. JSON is case-sensitive, unknown properties/enum values and duplicate properties are
rejected, and expected replies require matching request/contract identities. UTF-8 byte
limits apply independently of character counts. No diagnostic forwards request bodies,
credentials, provider exception text or native response content.

Errors use `{ contractId, version, requestId, error: { code } }` with MCP `isError` true.
Success uses the same identity envelope and non-null `data`; success/error cannot
coexist. Codes are bounded lower-case technical identifiers, such as `forbidden`,
`unsupported_operation`, `incompatible_contract`, `incompatible_schema`,
`revision_conflict`, `intent_conflict`, `invalid_cursor`, `incomplete_observation`,
`limit_exceeded` and `cancelled`. Infrastructure exceptions remain failures at the
host transport boundary. A cancellation error is not proof that no write occurred.

## Executable example

`MemoryTrackingProvider` exercises both repository and non-repository targets, atomic
revision checks, comments, additive classifications, hierarchy/dependency relations
and explicit native transitions. It uses a lock and process-local receipts, bounds
items/receipts and retains expired keys as uncertain. Cursors use HMAC and expire.
Each sample host creates a fresh binding generation, so retained references cannot
silently select a new provider after restart. This is not production persistence.
