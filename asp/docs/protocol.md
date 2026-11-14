# ASP content profile 1.0

## Contract and authority

ASP is a provider-neutral content protocol over MCP. The unreleased contract is
`1.0`, profile `asp.content/1`; this revision directly replaces `asp_read` and
`asp_write` with text and binary session methods. There are no legacy aliases.
Provider and NuGet versions are independent dimensions. Clients validate exact
schemas, mappings, profile and optional provider pins before execution.

Every call has `{ store: { storeId, bindingId, revision }, arguments: {...} }`.
The exact logical StoreRef selects a configured binding; it grants no authority
and contains no physical location or credential. The host reauthorizes every
initial call, continuation, replay and control call. Effective grants intersect
the configured maximum policy. Providers independently enforce that trusted
context, including logical key prefix and inclusive/exclusive key interval.
Changed subject, authorization revision, policy or binding revision cannot reuse
sessions, object references, cursors or receipts.

Physical configuration is opaque provider-owned JSON, configured separately from
content calls. The optional `Asp.Aep` package supplies protected, subject-bound
configuration leases; see [product integration](../../docs/reference/storage-execution.md).
AEP discovery, typed StorageTypes and backend differences belong to the extension.
Product Tool relays belong to Agentstration (#1044), not this SDK. Direct authorized
Agent calls use the same generic Tools. No backend enum or OpenDAL dependency is
introduced into ASP.

## Operations and schemas

One Tool group serves all targets. A service prefix `P` maps to:

| Tool | Arguments | Success data |
| --- | --- | --- |
| `P_describe` | `requestId` | versions, StoreRef, effective policy/capabilities, transfer capabilities, session limits, provider configuration schema |
| `P_list` | `requestId, prefix, limit, cursor?` | `objects, cursor?` |
| `P_stat` | `requestId, reference` | object metadata |
| `P_read_text` / `P_read_blob` | initial selection or continuation below | session, sequence, final, item, byte positions, content |
| `P_write_text` / `P_write_blob` | initial options or continuation below | session, sequence, final, cumulative writtenBytes, optional receipt |
| `P_close_read` | `requestId, sessionRef` | session state |
| `P_abort_write` | `requestId, sessionRef` | session state, original receipt if already finalized |
| `P_write_status` | `requestId, sessionRef` | session state, original confirmed receipt if available |

All wire names are camelCase. [Canonical schemas](schemas/) define closed objects,
required fields and initial/continuation alternatives. Unknown fields, null required
values, wrong types and physical destination overrides fail. Every response is
`{aspVersion, requestId, data? , error?}`, with exactly one of data/error and matching
MCP `isError`. Structured content is authoritative; text content mirrors the JSON.
Errors contain bounded technical `code, message, sessionRef?`, never backend traces
or credentials. The client exposes `AspException.Code` and `.SessionRef`.

Objects contain `key, version, length, sha256, reference` and optional `eTag, metadata`.
Length is bytes. Version and SHA-256 may explicitly be null when unavailable;
ETag and requested content metadata are optional. No metadata is fabricated.
Keys are relative logical names, maximum 256 characters, with no empty, dot or dot-dot
components, backslashes or control characters. Keys do not imply a filesystem.
Versions/ETags are opaque identities. References and cursors are signed, scoped
and expire after 15 minutes; process restart invalidates them. References with a
version/ETag require that exact identity, otherwise fail `version_unavailable`.
References without either identity cannot promise immutable reads.

List is an ordinal lexical walk, bounded by effective page size. It is not a
snapshot. Cursors bind prefix and page size; continuation must be strictly after
the last returned key, with no empty continuing page.

## Implicit reads

The first call uses `requestId`, `sequence: 0`, `notAfter` and
`selection: {reference, range?: {offset, length?}, preferredChunkSizeBytes?, encoding?}`.
The range is fixed in **bytes**: omitted length means through EOF; excessive length
is clipped to EOF; offset exactly EOF yields a final empty result; offset beyond
EOF fails. Negative positions/lengths fail. No continuation changes the selection.

The result contains `sessionRef, sequence, final, item, offset, byteLength,
selectedOffset, selectedLength` plus `content` (text) or `contentBase64` (binary).
Offsets and lengths describe bytes actually returned, not requested preferences.
`preferredChunkSizeBytes` is advisory; provider read behavior and UTF-8 boundaries
can produce smaller or larger chunks, within the effective hard byte bound.
Continuation uses only `requestId, sessionRef, sequence`. Final means all selected
bytes have been returned and the provider stream has been released.

Text defaults to strict UTF-8. `encoding: "utf-8"` is the only supported explicit
encoding, fixed on the initial call. Characters, combining sequences, existing BOM
and line endings are preserved. The SDK adds no BOM or normalization. Invalid bytes,
invalid .NET text or byte ranges beginning/ending inside a character fail. Text
chunks never split a UTF-8 character; an insufficient hard bound fails explicitly.
Binary uses canonical base64 without text decoding. Both use the same byte-oriented
provider read handle, not separate physical implementations.

## Implicit writes and exact evidence

The first call uses `requestId, sequence: 0, notAfter, options, final` and `content`
or `contentBase64`. Text additionally accepts initial `encoding: "utf-8"`.
`options` fixes `key, mode` (`create` default, `replace`, optional `append`), optional
`expectedVersion, ifMatch, ifNoneMatch, metadata, expectedLength, expectedSha256`.
Expected length and SHA-256 describe transferred bytes, including in append mode.
Metadata includes contentType/contentEncoding/contentDisposition/cacheControl and
opaque string userMetadata. Unsupported options fail before transfer execution.
Providers must enforce conditions at publication; a stat followed by an unprotected
write cannot implement a conditional write.

A continuation supplies `requestId, sessionRef, sequence, final` and its chunk only.
`final: false` acknowledges accepted bytes, not a successful object write. A small
write can be one initial `final: true` call. An empty final chunk is valid. Only
successful explicit finalization returns a receipt with exact commit evidence:
`{item, writtenBytes, sha256}`. The SDK computes the transferred-byte digest and
validates provider evidence; it never stats a potentially newer object to manufacture
an original receipt. For append, item metadata describes the resulting object while
receipt length/digest describe the appended bytes.

`IAspObjectWriter.OpenWriteAsync` returns one byte-oriented `IAspWriteHandle` with
`Content`, `CompleteAsync` and `AbortAsync`. Dispose/abort releases resources and
**does not promise rollback**. Atomic publication, progressive transfer, conditions,
metadata, empty/create/replace/append support and maximum object bytes are independent
provider capabilities. Restart recovery, retained versions and durable Snapshot
retention are not provided by this SDK. No global data file, object index or storage
quota is imposed by session management.

## Identity, sequencing and uncertainty

Request identities are nonblank, at most 128 characters. Sessions have an absolute
`notAfter`, strictly future and at most five minutes from initial acceptance.
Retain the exact original request, identity, sequence, deadline and representation
for retry. Initial, intermediate and final chunks can all be replayed: an identical
live request returns the original result without touching the provider. A changed
argument at the same sequence, or reuse of a live request identity at another
sequence, fails `idempotency_conflict`. Skipped/future sequences fail
`sequence_conflict`; receipts already expired fail `request_expired`. Initial
options/deadline cannot be supplied on continuation.

The default in-process receipt window is 30 seconds per chunk, bounded by the
session deadline. Describe publishes configurable limits: 64 open sessions,
8,192 retained session/receipt entries, 64 MiB decoded read replay budget. Live
entries are never silently evicted. Capacity refusal occurs before advancing I/O;
the unchanged call can be retried when expired receipts free capacity. Session
expiry releases open handles, independently of another content call, through a
TimeProvider timer. Restart does not recover sessions or guarantee exactly-once
publication. Retention of a receipt is not retention of object content.

Close-read and abort-write are idempotent cleanup actions. Status reports `open`,
`closed`, `aborted`, `finalized` or `unknown`; only `finalized` carries confirmed
success evidence. Cancellation, timeout, failed completion or lost transport can
leave publication uncertain. An uncertain execution is never repeated as fresh I/O
by replay and never produces a success receipt. Use the returned error sessionRef
for status or replay the exact original call while valid. Transport failure before
a sessionRef is received can be reconciled by that initial replay. Abort cannot
prove an already performed physical side effect was undone.

## Limits and client Streams

Profile ceilings remain 100 objects/page, 65,536 decoded bytes/chunk, 100,000
characters/arguments, 200,000 characters/result, 4,096 characters/reference and JSON
depth 32. Effective policy may reduce these. Serialized JSON and HTTP body limits
also apply, particularly for escaped text/metadata. Hosts must bound transport
bodies before JSON parsing. Text reads use an additional 24,576-byte ceiling to
keep escaped results bounded. Object limits come from each provider; they are
independent of chunk and replay limits. The memory sample alone has a 1 MiB/128
object quota; the disk conformance fixture transfers 9 MiB with fixed buffers.

`AspClient` owns no transport and automatically retries no mutations. The supplied
MCP connection must already be authenticated. Operation timeouts default to 30
seconds; server execution and session deadlines propagate cancellation to providers.
Providers must honor cancellation without releasing native resources still in use.

`client.OpenReadStream(selection, requestId, notAfter)` is an async, non-seekable
binary Stream over bounded MCP chunks; use `ReadAsync`/`CopyToAsync` and async dispose
to close an unfinished read. `OpenWriteStream(options, requestId, notAfter)` buffers
one policy-sized chunk. `FlushAsync` sends a non-final chunk; **CompleteAsync**
explicitly finalizes and returns the receipt. Async disposal aborts unfinished work,
never silently completes it. Synchronous disposal leaves server expiry responsible
for session cleanup. Preserve the original logical operation after an uncertain
Stream failure; do not blindly repeat partially consumed caller buffers.

Common errors include `invalid_request`, `invalid_range`, `invalid_text`,
`unsupported_encoding`, `character_boundary`, `forbidden`, `invalid_reference`,
`unsupported_operation`, `unsupported_option`, `limit_exceeded`, `capacity_exceeded`,
`not_found`, `version_unavailable`, `conflict`, `idempotency_conflict`,
`sequence_conflict`, `request_expired`, `integrity_mismatch`, `unknown_outcome`, and
`invalid_result`. Description/schema mismatches use `incompatible_version`,
`incompatible_schema`, `incompatible_provider` or `unsupported_capability`.
Product consumers localize presentation at their boundary.
