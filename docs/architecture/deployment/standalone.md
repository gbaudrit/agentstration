# Standalone mode

Standalone is the executable default, not a reduced cloud-dependent profile. It runs with the .NET SDK, local files/SQLite, bounded local queues, deterministic AI, and at least one independent Runtime Worker process.

```powershell
$env:AI__Provider = "Deterministic"
dotnet run --project src/Agentstration.Web
./scripts/runtime/start-maf-worker.ps1 -AuthorityUrl http://localhost:5100
```

On startup the authoritative server initializes module-owned stores and reconciles durable Runtime assignments. The Worker pairs on first manual launch, persists its own protected workload credential, and subsequently claims Runs through AWP; it never shares the server stores. Initial Bootstrap is optional. The standard Development launch profiles enable the ordered `development` Bootstrap profile for a fresh local instance and create the documented disposable `admin / admin` fixture; no credential or topology is implicit outside Development.

The direct host embeds the Console, while the launcher starts the Runtime Worker as a real second process. There is no in-process execution fallback. Independently hosted Console and Workplace shells consume the authoritative APIs and own no business state. Aspire development orchestration starts a configurable number of Workers; Compose starts its explicit Worker resource. Both preserve the same server boundary while optionally adding those shells, AEP extensions, inference services, or PostgreSQL. Deterministic AI remains the offline validation and fallback path; provider integrations are optional.
