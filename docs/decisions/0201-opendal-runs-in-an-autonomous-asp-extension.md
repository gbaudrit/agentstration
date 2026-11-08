# ADR-0201: OpenDAL runs in an autonomous ASP extension

## Status

Proposed

## Context

FR #1039 under #1032 needs OpenDAL physical access behind the ASP/MCP and AEP
boundaries. The official Apache.OpenDAL 0.3.1 NuGet package is published despite
its embedded README saying otherwise. Wrapper and native library share commit
fce9cdefdea7c8e54dfbaf3c88c3b6d5ec68146e. FR #1085 now supplies provider-neutral
text/blob sessions, progressive streams and bounded replay receipts in the SDK.

## Decision

Consume the pinned official package inside an autonomous .NET extension. Product,
Storage, Connected Service and neutral ASP/AEP SDK assemblies never reference
OpenDAL. Reuse ASP.Server, ASP.Aep, AEP.Client and enrollment authentication. No
new transport, permission engine, Python MCP server or business resource dependency
is introduced. No fork or Rust compiler is needed for normal builds.

Use one ASP-to-Operator adapter for filesystem, S3 and Azure Blob. Contribute all
three executable StorageTypes at 1.0.0 with typed requirements and schemas; other
native compiled services are not silently advertised as integrated. The adapter
stores logical keys directly through OpenDAL. The SDK owns session sequencing,
text conversion and in-process replay. There is no aggregate content JSON, custom
object/version index, persistent receipt ledger or extension store-size quota.

Keep connection bindings, physical configuration, Storage envelope and dataset
policy separate. Remote Secret values are redeemed through existing AEP grants,
not supplied as content arguments. Endpoint/identity/region exports are standard
values; credentials are secured. Never infer cross-extension mappings or create
a Connected Service. Exact StoreRef and current authenticated allowlist checks
remain mandatory on every operation; intersect restrictions without widening.

Partition physical roots by logical StoreId/BindingId. Filesystem roots are absolute
operator-owned configuration, never caller content parameters. Validate relative
key containment and reject reparse/dangling/symbolic links. Remote bucket/container
and optional prefix are protected configuration, with the same partition appended.
No unconfigured raw native settings or ambient S3 credentials are accepted.

Derive effective transfer capabilities from the configured native Operator and
narrow published operations. Reject unsupported conditions instead of emulating
CAS with a stat/write pair. In 0.3.1 the managed write options do not expose native
version conditions, and stream completion returns no Metadata. Therefore decline
version conditions and append; preserve known transferred length/requested metadata
with null unavailable version/ETag/digest. The SDK computes transfer integrity.
Never call stat to manufacture exact original commit evidence.

Do not advertise atomic publication or durable replay. The filesystem native
atomic_write_dir mode allowed concurrent IfNotExists writers to all succeed in
our regression test. Use the native direct create_new/O_EXCL path instead; content
can be progressively visible. This enforces native create exclusion without a
custom lock/receipt engine. Other condition support remains capability gated.

Keep native resources alive until submitted work settles. The .NET cancellation
API cancels observation and does not prove native cancellation. Check request
cancellation before/after native work, use native timeout layers and retain handles
through completion. Normalize native errors without leaking paths or credentials.
The native output-stream API has no abort primitive and disposal may close/publish
unfinished work: surface unknown status rather than claim rollback. SDK replay
never re-executes an uncertain protocol chunk.

## Consequences

- Large transfers use native streams and bounded ASP chunks. No 128-object or
  8 MiB aggregate quota remains. Chunk, object, session and replay limits differ.
- The binding materializes native listings; ASP page results are sorted and bounded,
  but listing large namespaces can consume memory proportional to native entries.
  A future native iterator API is required to remove that separate limitation.
- Content survives provider restart where the physical backend persists it.
  Sessions, references and replay receipts require reacquisition/reconciliation;
  no exactly-once restart recovery, historical-version retention, Snapshot retention,
  power-loss durability or deletion operation is promised.
- Ship matching managed/native RID outputs plus upstream notices/licenses. Validate
  real published MCP before/after restart. Dedicated CI targets Windows x64 and
  Ubuntu 24.04 Linux x64. Other packaged RIDs are not support claims; glibc is required,
  and musl/Alpine, trimming, single-file and Native AOT remain unsupported.
- Offline native cloud protocol peers exercise the common adapter without accounts;
  live cloud behavior, credentials, network and operational limits still need
  deployment-specific validation. No generic Agent API changes with the backend.
- If a blocking upstream defect later requires a fork, pin wrapper/native sources
  together under a distinct internal package identity and document the correction
  and criteria for returning upstream. Accepted ADRs remain unchanged.
