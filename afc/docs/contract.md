# AFC.mcp.v1 specification

## Identity and ownership

The exposure identity is `agentstration.forge.contracts.mcp.v1`, revision `1.0`.
The major identity, precise compatible contract revision, negotiated MCP version,
provider implementation version and SDK package version are separate dimensions.
Only canonical `1.<nonnegative minor>` revisions are valid; clients require the
selected revision and exact supported canonical schemas, not an unverified badge.

AFC owns forge observations and bounded change-request creation. A native GitHub PR,
GitLab MR or equivalent retains provider-native identity/state. Git transport and
publication receipts, ATC tracking, Project/plan lifecycle, functional acceptance,
execution and merging retain their respective owners. This SDK exposes no merge,
tracker mutation, clone, fetch, commit, push or REST operation.

## Selection and authorization

Every Tool input is `{ target, arguments }`. The target has `providerId`, `targetId`,
`bindingId` and configuration `revision`. Provider identity names a configured
transport authority, not just a vendor family. The host resolves an exact configured
repository target and trusted subject/grant revision and allowed operations on every
call, including describe and cached receipt recovery. A selected ID grants no access.
Unknown targets/configuration revisions fail closed without choosing another binding.

Repository/native change-request identity is provider/target/repository/native ID.
Ref evidence includes a native ref name and exact lowercase Git object ID (40 or 64
hexadecimal characters). This increment supports same-repository head/base creation;
it does not claim fork, cross-repository or non-Git revision support.

Physical repository configuration and credentials remain host/provider-owned.
Providers independently enforce native authorization in addition to the host grant.
Per-call authorization and immutable target selection also apply during replay.
Transport authentication never establishes tracking authority.

The optional AEP mapping stores a structured declaration under the contract identity
key on each existing Tool contribution. It includes precise revision, prefix and ten
schema digests. Validators check a declared MCP server and complete unique mappings;
the typed client also verifies actual discovered input/output schemas. No generic
contract catalogue, enrollment mechanism or dependency on AEP is added to neutral SDKs.

## Operations and bounded observations

| Tool suffix | Responsibility |
| --- | --- |
| `describe` | Exact target, implementation, effective capabilities, limits and guarantees |
| `resolve` | Resolve exact requested refs, or the default ref when none were requested |
| `read` | Read one qualified native change request and current head/base evidence |
| `create` | Execute or recover one exact create intent with pinned repository/head/base |
| `observe` | Read explicitly requested native review/check evidence at expected revisions |

The default prefix is `afc`. Generic schemas stay stable while current authorized
operations are advertised by `describe`. Reviews and checks are independently gated.
Unimplemented capabilities fail explicitly, never as a fabricated empty successful
observation. Resolution returns the exact requested ref set; missing refs cannot be
substituted or silently truncated. No unbounded catalogue/list operation is exposed.

A change request retains native ID, display ID, state, revision token, link, draft and
merged flags, and head/base refs. These observations do not imply local acceptance.
Optional tracking references are opaque external associations, not proof of tracker
existence, a permission grant, or a request to modify/close a native issue. Different
tracking and forge authorities are supported; no ATC SDK or tracker is required.

Review/check records retain native IDs/states, their commit object ID when known,
and a documented common interpretation:
reviews are Unknown/Pending/Approved/ChangesRequested; checks are
Unknown/Pending/Success/Failure/Skipped. Unmapped native values are Unknown. These
states supply evidence only; they do not authorize merge or accept functional work.

Observation status Fresh means the exact expected head/base were observed, all
requested collections are complete, and every review/check identifies the expected
head commit. Missing or older commit evidence cannot be treated as Fresh. It does not mean all checks succeeded or all
reviews approved. Stale requires different observed head/base evidence. Unknown makes
no freshness/completeness claim. Unrequested collections are empty with completeness
false. Native pagination must be fully collected within bounds before claiming Fresh;
otherwise return Unknown or an explicit failure, never a false complete empty set.
The fixture has bounded complete sets and needs no continuation API.

## Creation, concurrency and recovery

Create intents include pinned repository ID, title/body, head/base ref names and exact
object IDs, draft state and optional external tracking references. Changing an intent
requires a new operation after a definitive prior rejection; never silently replace an
uncertain operation's key or selected provider.

Creation concurrency is None, Observation (preflight only), or AtomicRevision. An
expected SHA or successful preflight alone is not native compare-and-swap. A real
provider unable to bind creation atomically to the expected SHAs must declare
Observation/None, inspect resulting evidence and retain Uncertain when it cannot
establish the required outcome. A partial receipt may retain a qualified artifact
whose native head/base differs; it must not be upgraded to Applied.

Request IDs identify attempts. Operation keys identify stable effects, scoped to exact
target, subject and intent. The digest is `sha256:` plus lowercase SHA-256 of the SDK's
JSON serialization of `{ target, intent }`, using the shared serializer options.
It excludes request ID/mode; array ordering remains significant. Retain the original
material for retries. Servers verify the digest before dispatch.

Receipts are Applied, Rejected, Uncertain or Missing. Applied includes the original
creation snapshot matching the exact intent; Rejected proves no create effect and
includes a bounded error code. Uncertain includes a code and may retain partial native
artifact evidence. Missing is valid only for recovery with positive proof that the
operation was not dispatched. Search absence, cancellation or a lost response is not
such proof. Partial effects must never be reported as Rejected.

Recovery is None, Process or Durable, with an explicit window. Process provides no
crash durability or cross-instance coordination. Lost/expired state must remain
Uncertain or the former binding generation must be fenced. Never evict old keys into
proof of absence. Durable requires actual retained state and native recovery evidence.
The client does not automatically retry or switch providers. Historical recovery is
not a current read, review approval, check success or permission bypass.

## Bounds and wire failures

Calls have 30-second SDK deadlines and propagate caller cancellation. Cancellation,
transport failure, oversized/malformed post-effect replies and timeouts can all leave
a native effect; callers must preserve the original intent for explicit recovery.

Requests are limited to 131,072 UTF-8 JSON bytes; replies to 262,144, narrowed by target
policy (minimum supported reply budget 4,096). Materialized SDK checks supplement
host-owned raw request and transport-buffering limits. IDs are 128 characters, ref
names 256, titles 512, body 32,768, refs 16, tracking references 16 and review/check
observations 32 per collection. Public web/clone locators are bounded and cannot carry
passwords or credential query/fragment data. They do not initiate Git transport.

JSON property names are case-sensitive, duplicate/unknown properties and unknown
values fail explicitly, and replies require matching request/contract identities.
Success envelopes contain non-null data; failure envelopes contain an error code and
MCP `isError` true. They cannot coexist. Unexpected infrastructure exceptions remain
transport failures, without exposing exception bodies, credentials or request content.
A bounded `cancelled` error does not prove that no native create effect occurred.

## Executable fixture and adoption

The sample is a bounded process-local forge with current ref observations, exact
in-memory atomic create checks, stable replay/conflict, explicit expiry uncertainty,
revocable grants and restart generations. Its native approval/check strings are
fixture evidence only. It is not a production forge or proof of GitHub/GitLab behavior.
Product routing, protected connection selection, durable Work/Coding receipts and
vendor adaptation stay in #951/#958/#959/#1349. This contract owns no delivery engine.
