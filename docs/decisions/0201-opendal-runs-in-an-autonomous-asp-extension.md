# ADR-0201: OpenDAL runs in an autonomous ASP extension

## Status

Proposed

## Context

FR #1039, under #1032, needs physical storage through OpenDAL while preserving
ADR-0193's ASP/MCP contract, AEP discovery and ADR-0200's protected configuration
exchange. OpenDAL's README still calls the .NET binding unreleased, but NuGet
publishes the official Apache.OpenDAL 0.3.1 package. Its native library and managed
wrapper share source commit fce9cdefdea7c8e54dfbaf3c88c3b6d5ec68146e.

ASP writers must commit content and idempotency receipts atomically. Ordinary
OpenDAL writes do not establish that guarantee for arbitrary backends. The .NET
binding's task cancellation also does not prove cancellation of native work.

## Decision

Use the exact official Apache.OpenDAL 0.3.1 package inside an autonomous .NET
extension. Product, Storage, Connected Service and neutral ASP/AEP SDK assemblies
do not reference OpenDAL. Reuse Asp.Server, Asp.Aep and AEP enrollment authentication.
No additional wire transport, Python MCP server, permission engine or product
resource dependency is introduced.

Contribute opendal.fs@1.0.0 as the executable filesystem implementation. Publish
S3 and Azure Blob preview descriptors with typed standard/secured requirements
and all executable capabilities false. Their configuration leases are refused
before credential redemption. They create no Connected Service and cannot infer
cross-extension mappings.

Filesystem physical placement uses an operator-owned absolute root and a hash of
the logical StoreId/BindingId pair. Configuration is empty; callers cannot choose
roots or raw backend settings. The Storage envelope and dataset policy remain
separate documents, intersected without widening. Every lease and content call
checks the authenticated AEP subject against an explicit operator allowlist.
Monitored disablement or removal of that subject revokes further operations.

For this bounded backend, persist content, current versions and live receipts as
one state object through OpenDAL. An exclusive file lock serializes cooperating
processes, including reads. Configure fs atomic_write_dir on the same filesystem;
OpenDAL commits the state object by atomic rename. .NET handles directory/link
checks and coordination only; OpenDAL performs all content/state I/O.

Bound each store to 128 objects, 8 MiB decoded content and 512 live receipts,
with a 16 MiB serialized state ceiling. Bound the host to four concurrent native
operations. This deliberately trades throughput for a small executable backend
whose conditional writes and receipts have one commit boundary.

Use synchronous bounded native filesystem operations inside that boundary.
Check cancellation before acquiring locks, before commit and after native work.
Do not release locks or handles while a cancelled asynchronous native write may
still be running. Cancellation or failure may leave an unknown outcome; exact
original retries reconcile through persisted receipts within their deadline.

No deletion, historical-version conservation, durable Snapshot retention,
power-loss durability or arbitrary-directory interoperability is advertised.
Reject symbolic/reparse/dangling links. The root must not be writable by an
untrusted actor: path checks do not defend against a privileged concurrent
filesystem replacement. Persist the complete root and product identities.

## Consequences

- No fork or Rust compiler is required for ordinary builds or execution.
- Publish RID-specific managed/native distributions, upstream notices and license
  texts. Validate real MCP through the published executable before and after restart.
- Windows x64 and Linux x64 receive a dedicated CI matrix. Linux uses glibc;
  Alpine/musl, trimming, single-file publication and Native AOT are unsupported.
- Updating OpenDAL requires the same native, ASP, isolation and packaging checks.
  If a blocking defect requires a fork, pin one upstream revision, build wrapper
  and native library together, publish under a distinct internal package identity
  and document the correction and criteria for returning upstream.
- Larger stores and executable cloud backends need their own implemented atomic
  receipt strategy and new immutable contribution versions. Their descriptors
  do not promise execution or Snapshot suitability.
