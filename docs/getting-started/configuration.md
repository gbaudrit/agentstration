# Configuration

Agentstration uses standard ASP.NET Core configuration. JSON settings can be overridden by environment variables using double underscores, for example `AI__Provider=Deterministic`.

The main verified settings are:

| Setting | Current default | Purpose |
| --- | --- | --- |
| `AI:Provider` | `Managed` | Selects model resolution/execution mode. Use `Deterministic` explicitly for offline or test execution. |
| `LlamaCpp:Endpoint` | `http://localhost:8080` | Native llama.cpp server used by the autonomous llama.cpp extension and Aspire. |
| `LocalAI:Endpoint` | `http://localhost:8081` | Native LocalAI server used by the autonomous LocalAI extension and Aspire. Port 8081 avoids the llama.cpp default on 8080. |
| `LocalAI:ApiKey` | unset | Optional LocalAI Bearer key. Supply it through environment or secret-backed host configuration; never commit it. |
| `Data:Directory` | `.agentstration` | Base directory for module-owned SQLite databases, key material, Pack archives and Work artifacts. |
| `Agentstration:Storage:Provider` | `Sqlite` | Relational storage profile: `Sqlite` or `PostgreSql` (case-insensitive). |
| `ConnectionStrings:Agentstration` | unset | Required main PostgreSQL connection when the storage provider is `PostgreSql`. |
| `Data:ControlPlanePath` | `.agentstration/control-plane.db` | Management Plane SQLite database. |
| `Data:WorkPlanePath` | `.agentstration/work-plane.db` | Work Plane SQLite database. |
| `Data:FlowPath` | `.agentstration/flow-plane.db` | Flow SQLite database. |
| `Data:RuntimePath` | `.agentstration/runtime-plane.db` | Runtime SQLite database. |
| `ConnectionStrings:Identity` | `.agentstration/identity.db` | ASP.NET Core Identity account database, managed through EF Core migrations. |
| `Agentstration:Authentication:DataProtectionKeysPath` | `.agentstration/data-protection-keys` | Persistent ASP.NET Core Data Protection key ring for cookies and Identity tokens. |
| `Agentstration:WorkApi:BaseAddress` | `http://localhost:5100/` | Console-to-Work-API connection on the authoritative server. |
| `Agentstration:ApiBaseUrl` | `http://localhost:5100/` | Workplace-to-server API connection. |
| `Agentstration:WorkplaceHubUrl` | `http://localhost:5100/hubs/workplace` | Workplace real-time endpoint. |

Provider-specific options and persisted model resources are described in [Model providers](../concepts/model-providers.md) and [Model profiles](../concepts/model-profiles.md). Do not store secrets in committed settings files.

## PostgreSQL storage profile

PostgreSQL uses the `management`, `work`, `flow`, `runtime`, `identity`, and `scheduler` schemas in one database. It does not move file-backed secrets, Data Protection keys, Pack archives, or Work artifacts. Switching from SQLite does not migrate existing data; retain the SQLite files and use a future supported export/import path. PostgreSQL is currently single-instance only because queues and Quartz are not clustered.

For Compose, copy `.env.postgresql.example` to an uncommitted `.env`, replace its disposable password, then run `docker compose -f docker-compose.yml -f docker-compose.postgresql.yml up --build`. The ordinary `docker compose up --build` command remains SQLite. For Aspire, set `Agentstration:Storage:Provider=PostgreSql`; PostgreSQL data is stored in a slot-scoped Docker volume named `agentstration-<slot>-postgresql` and SQLite remains the default.

### Aspire persistence and development slots

Aspire mounts the slot-scoped Docker volume at `/var/lib/postgresql/data`. PostgreSQL requires Unix ownership and permission changes during `initdb`, so its data directory cannot be a bind mount into the Windows worktree when Docker runs in Linux or WSL. The volume is stored by the Docker daemon, normally below `/var/lib/docker/volumes/agentstration-<slot>-postgresql/_data`, rather than inside `.agentstration/slots/<slot>`.

The AppHost generates the `postgres-password` parameter on first use, marks it secret, and persists it in the AppHost user-secrets store. The persisted password and the volume form one credential set: do not remove or change one while retaining the other. Never commit the generated password or copy it into `appsettings.json`.

To inspect a development volume without changing it:

```powershell
docker volume inspect agentstration-<slot>-postgresql
```

To reset disposable PostgreSQL data, first stop the corresponding AppHost and verify the exact slot name. The following operation permanently removes that slot's PostgreSQL database:

```powershell
docker volume rm agentstration-<slot>-postgresql
dotnet user-secrets remove "Parameters:postgres-password" --project src/Agentstration.AppHost
```

Remove both only for a full reset. Removing only the user-secret produces repeated `password authentication failed` errors against the retained volume. Back up non-disposable data before removing a volume.

### Startup and readiness

The Web host validates the selected provider and PostgreSQL connection string, obtains a bounded advisory lock, creates the six schemas, applies the five EF Core migration sets in deterministic order, and initializes the Quartz schema. Bootstrap and background workers start only after storage initialization. `/health` reports process liveness; `/health/ready` returns success only after storage is ready.

On the first start, PostgreSQL may log that the `agentstration` database or a schema-specific `__EFMigrationsHistory` relation does not exist while Aspire and EF Core probe and create them. These messages are expected only during initialization and are harmless when `/health/ready` subsequently becomes ready. Treat repeated authentication failures, migration exceptions, Quartz SQL errors, or a readiness endpoint that remains unavailable as startup failures.

The Workplace depends on the Console API. If the Console is running but the Workplace displays its unavailable page, inspect the Console response for `/api/workspaces/{workspace}/dashboard` and the Console logs. A `500` response is an application/storage failure rather than a Workplace network failure.

## Storage concurrency benchmark

The opt-in benchmark uses the same workload for both providers: each operation creates and updates a Work Item, appends one Flow event, appends one Runtime event, and stores a Runtime checkpoint. It reports throughput, median and p95 latency, errors, concurrency conflicts, and retries. It has no pass/fail timing threshold and is skipped by the standard test suite.

```powershell
$env:AGENTSTRATION_STORAGE_BENCHMARK_PROVIDER = "Sqlite"
$env:AGENTSTRATION_STORAGE_BENCHMARK_OPERATIONS = "100"
$env:AGENTSTRATION_STORAGE_BENCHMARK_CONCURRENCY = "8"
$env:AGENTSTRATION_STORAGE_BENCHMARK_REPORT = "$env:TEMP\agentstration-storage-benchmark.json"
dotnet test tests/Agentstration.Web.Tests/Agentstration.Web.Tests.csproj --configuration Release --filter "Name=ReportsConcurrentRelationalWriteMetrics" --logger "console;verbosity=detailed"

$env:AGENTSTRATION_STORAGE_BENCHMARK_PROVIDER = "PostgreSql"
$env:AGENTSTRATION_TEST_POSTGRES = "Host=localhost;Database=agentstration;Username=agentstration;Password=<development-only-password>"
dotnet test tests/Agentstration.Web.Tests/Agentstration.Web.Tests.csproj --configuration Release --filter "Name=ReportsConcurrentRelationalWriteMetrics" --logger "console;verbosity=detailed"
```

## Backup and restore

Back up the PostgreSQL database and the file-backed Data Protection keys, local secrets, Pack archives, and Work artifacts as one consistent set. Restoring only the database is insufficient and can invalidate cookies, lifecycle tokens, secret references, Pack content, or Work artifacts.

Use `pg_dump`/`pg_restore` or the equivalent managed PostgreSQL tooling for all six schemas. Stop writes or take a transactionally consistent database snapshot, copy the file-backed state from `Data:Directory`, and record the application version. Restore those components together before starting Agentstration. Do not restore a PostgreSQL volume by copying files between major PostgreSQL versions; use logical dump/restore or a supported PostgreSQL upgrade procedure.

PostgreSQL remains single-instance in this release, and switching providers does not migrate data; a supported export/import path is future work.
