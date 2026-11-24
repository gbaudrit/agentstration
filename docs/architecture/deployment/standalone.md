# Standalone mode

Standalone is the executable default, not a reduced cloud-dependent profile. It runs with the .NET SDK, local files/SQLite, bounded local queues, deterministic AI, and at least one independent Runtime Worker process.

```powershell
$env:AI__Provider = "Deterministic"
dotnet run --project src/Agentstration.Web
./scripts/runtime/start-maf-worker.ps1 -AuthorityUrl http://localhost:5100
```

On startup the authoritative server initializes module-owned stores and reconciles durable Runtime assignments. The Worker pairs on first manual launch, persists its own protected workload credential, and subsequently claims Runs through AWP; it never shares the server stores. Initial Bootstrap is optional. The standard Development launch profiles enable the ordered `development` Bootstrap profile for a fresh local instance and create the documented disposable `admin / admin` fixture; no credential or topology is implicit outside Development.

Initial Bootstrap exposes topology, Worker communications, built-in resources,
extension contributions, profile resources and finalization as ordered stages.
Authenticated Workers can connect after structure creation, before full readiness.
Only server-admitted Bootstrap assignments are claimable until finalization;
ordinary work, recovery and scheduling remain suspended. Discovery-bearing
profiles use their normal durable Flow/AWP path and wait for a compatible Worker
at the instruction that needs it. Purely declarative profiles need no Worker.
See [ADR-0253](../../decisions/0253-initial-bootstrap-admits-its-own-worker-assignments.md).

The direct host embeds the Console and uses a separately started Runtime Worker.
Aspire starts its Workers after the authority process starts, without waiting for
Bootstrap readiness; Compose uses its explicit Worker resource with the same
startup ordering. There is no in-process execution fallback. Independently hosted
Console and Workplace shells consume the authoritative APIs and own no business
state. Deterministic AI remains the offline validation and fallback path; provider
integrations are optional.

The [local SQLite preview](../../contributing/preview-environments.md) consumes
independently versioned server and Worker OCI deliveries by digest. Its two-container
composition uses the existing Runtime and Bootstrap boundaries, keeps initial
Bootstrap disabled unless explicitly configured, and requires no local inference
or cloud account. This external packaging increment does not add a Runtime deployment
orchestrator; see [ADR-0255](../../decisions/0255-sqlite-previews-consume-private-oci-deliveries.md).
