# Configuration

Agentstration uses standard ASP.NET Core configuration. JSON settings can be overridden by environment variables using double underscores, for example `AI__Provider=Deterministic`.

The main verified settings are:

| Setting | Current default | Purpose |
| --- | --- | --- |
| `AI:Provider` | `Managed` | Selects model resolution/execution mode. Use `Deterministic` explicitly for offline or test execution. |
| `LlamaCpp:Endpoint` | `http://localhost:8080` | Native llama.cpp server used by the autonomous llama.cpp extension and Aspire. |
| `LocalAI:Endpoint` | `http://localhost:8081` | Native LocalAI server used by the autonomous LocalAI extension and Aspire. Port 8081 avoids the llama.cpp default on 8080. |
| `LocalAI:ApiKey` | unset | Optional LocalAI Bearer key. Supply it through environment or secret-backed host configuration; never commit it. |
| `OLLAMA_IMAGE` | `ollama/ollama:0.33.2` | Compose-only Ollama image override. |
| `LLAMA_CPP_IMAGE` | `ghcr.io/ggml-org/llama.cpp:server-b10830` | Compose-only llama.cpp image override. |
| `LLAMA_CPP_MODELS_PATH` | `./.models/llama-cpp` | Compose-only host directory containing GGUF models. |
| `LLAMA_CPP_MODEL` | `model.gguf` | Compose-only GGUF filename passed to llama-server. |
| `LLAMA_CPP_MODEL_ALIAS` | `local-gguf` | Compose-only stable model alias exposed by llama-server. |
| `LOCALAI_IMAGE` | `localai/localai:v4.9.0` | Compose-only LocalAI image override. |
| `LOCALAI_API_KEY` | unset | Compose-only optional LocalAI Bearer key. |
| `AI_PROVIDER` | `Managed` | Compose-only execution-mode override; use `Deterministic` for the offline fallback. |
| `AGENTSTRATION_HTTP_PORT` | `5100` | Compose-only host port published for the authoritative server. |
| `Data:Directory` | `.agentstration` | Base directory for module-owned SQLite databases, key material, Pack archives and Work artifacts. |
| `Agentstration:Storage:Provider` | `Sqlite` | Relational storage profile: `Sqlite` or `PostgreSql` (case-insensitive). |
| `Agentstration:InstanceId` | persisted `.agentstration/instance-id` value | Optional explicit AppHost worktree identity used to isolate PostgreSQL volumes and persisted passwords. |
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
| `Agentstration:Aep:Transport:AllowedHttpHosts` | loopback hosts | Exact local hosts permitted to use clear-text HTTP for AEP development. |
| `Agentstration:Aep:Transport:AllowedPrivateNetworkHosts` | loopback hosts | Exact hosts whose DNS results may use private address ranges. |
| `Agentstration:Aep:Transport:BlockPrivateNetworks` | `true` | Blocks private DNS/IP targets unless their host is explicitly allowed. |

Provider-specific options and persisted model resources are described in [Model providers](../concepts/model-providers.md) and [Model profiles](../concepts/model-profiles.md). Do not store secrets in committed settings files.

## AEP outbound transport

Agentstration disables redirects for authenticated AEP requests and requires remote extensions to use HTTPS. The default local profile permits HTTP and private addresses only for `localhost`, `127.0.0.1`, and `::1`. Aspire resolves its local project endpoints through those loopback origins.

An Extension Registration may set `authenticationMode` to `staticBearer` and reference a visible `Secret` through `credential`. The Secret is resolved in the registration's namespace and scope immediately before every AEP request, so replacing or deleting its value takes effect without changing Model Providers or profiles. `authenticationMode: none` must not include a credential, and `staticBearer` requires one. Tokens are request-scoped and are never installed as default HTTP headers or forwarded to MCP/native provider transports.

```yaml
authenticationMode: staticBearer
credential:
  name: aep-extension-token
  namespace: default
  scopeRef: /tenants/00000000-0000-0000-0000-000000000001
```

For orchestrated development, `enrollmentMode: sharedKeyFile` reads the credential through a read-only Secret provider instead of copying it into Management storage:

```yaml
authenticationMode: staticBearer
enrollmentMode: sharedKeyFile
sharedKeyFile:
  path: /run/aep-keys/ollama/token
```

The extension initiates SharedKeyFile enrollment just like PairingCode. Configure the extension with the authority and target scope in addition to its local key file:

```text
Aep__EnrollmentMode=SharedKeyFile
Aep__SharedKeyFile__Path=/run/aep-keys/ollama/token
Aep__SharedKeyFile__AuthorityUrl=https://agentstration.example/
Aep__SharedKeyFile__PublicEndpoint=https://extension.example/
Aep__SharedKeyFile__StateFile=/var/lib/extension/aep-shared-key.instance
```

The matching Agentstration configuration declares the same extension ID, `enrollmentMode: sharedKeyFile`, and key-file path, but no endpoint or target scope. The extension retries until the authority is available and signs its timestamped identity and endpoint with HMAC-SHA256. The shared key itself is never sent. Agentstration records one unassigned instance-level candidate; a platform administrator assigns it to an Instance, Tenant, or Workspace scope. A valid proof creates the scoped registration without an administrator-entered code once that assignment is confirmed.

The file must be a single UTF-8 token line containing at least 32 bytes and no more than 4096 bytes; only a final LF or CRLF is tolerated. Missing, unreadable, short, multiline, malformed, or oversized files fail closed. Aspire provisions distinct files automatically beneath the ignored slot data directory. Docker Compose provisions distinct persistent volumes and mounts each extension's key read-only; `docker compose down -v` removes those development credentials.

For a manually hosted extension, PairingCode enrollment creates the first credential through an administrator-approved, workspace-bound flow:

```text
Aep__EnrollmentMode=PairingCode
Aep__PairingCode__AuthorityUrl=https://agentstration.example/
Aep__PairingCode__PublicEndpoint=https://extension.example/
Aep__PairingCode__PairingUri=https://extension.example/aep/enrollment/pair
Aep__PairingCode__StateFile=/var/lib/extension/aep-pairing.json
```

The public endpoint and pairing URI must have the exact same origin. In **Extensions → Enrollment inbox**, a platform administrator first assigns the candidate to an Instance, Tenant, or Workspace scope, then selects **Copy code and open extension**. This creates a fresh 60-second code bound to the server-side assignment and invalidates any previously copied value. The code is entered in the extension form and never appears in a path, query, or fragment. `Aep__PairingCode__AllowInsecureHttp=true` is an explicit local-development escape hatch for the authority URL.

After pairing, the same inbox can rotate or revoke the workload credential. Rotation overlaps the old and replacement digests until the replacement Secret and identity-pinned manifest are verified. Revocation closes the extension on its next request, deletes the vault value, disables the registration, and does not reopen enrollment. Back up the control-plane database, Local Vault master key/data, and extension `StateFile` together. To recover from an intentional full reset, stop the extension and invoke `AepPairingLifecycle.ResetToUnpaired(stateFile)` locally before restarting and approving a new request; authentication failures never trigger this operation automatically.

For an intentional Compose service name or private HTTPS extension, add the exact DNS host to the narrowest applicable list. An HTTP service must be present in both lists when it resolves to a private address:

```json
{
  "Agentstration": {
    "Aep": {
      "Transport": {
        "AllowedHttpHosts": [ "localhost", "ollama-extension" ],
        "AllowedPrivateNetworkHosts": [ "localhost", "ollama-extension" ]
      }
    }
  }
}
```

Environment-variable configuration uses numeric array indexes, for example `Agentstration__Aep__Transport__AllowedHttpHosts__0=ollama-extension`. Do not add a broad wildcard or cloud metadata/link-local address. DNS is re-evaluated when bounded pooled connections are renewed; TLS validation retains the platform certificate and hostname checks.

## PostgreSQL storage profile

PostgreSQL uses the `management`, `work`, `flow`, `runtime`, `identity`, and `scheduler` schemas in one database. It does not move file-backed secrets, Data Protection keys, Pack archives, or Work artifacts. Switching from SQLite does not migrate existing data; retain the SQLite files and use a future supported export/import path. PostgreSQL is currently single-instance only because queues and Quartz are not clustered.

For Compose, copy `.env.postgresql.example` to an uncommitted `.env`, replace its disposable password, then run `docker compose -f docker-compose.yml -f docker-compose.postgresql.yml up --build`. The base `docker compose up --build` command remains the minimal SQLite and deterministic topology. Provider-specific AEP launches use `docker-compose.ollama.yml`, `docker-compose.llama-cpp.yml`, or `docker-compose.localai.yml`; each includes the selected model-provider extension and the Utilities extension. For Aspire, set `Agentstration:Storage:Provider=PostgreSql`; PostgreSQL data is stored in a worktree-isolated Docker volume named `agentstration-<slot>-<instance-id>-postgresql` and SQLite remains the default.

### Aspire persistence and development slots

Aspire mounts the worktree-isolated Docker volume at `/var/lib/postgresql/data`. PostgreSQL requires Unix ownership and permission changes during `initdb`, so its data directory cannot be a bind mount into the Windows worktree when Docker runs in Linux or WSL. The volume is stored by the Docker daemon, normally below `/var/lib/docker/volumes/agentstration-<slot>-<instance-id>-postgresql/_data`, rather than inside `.agentstration/slots/<slot>`.

On first use, the AppHost atomically creates a random 12-character identifier in the ignored `.agentstration/instance-id` file. This identifier is independent of the slot, so separate worktrees remain isolated even when direct launches make each one use `main`. It remains stable across restarts and branch changes and follows the worktree when its complete directory is moved. Deleting `.agentstration`, or recreating the worktree without it, creates a new identity. Set `Agentstration:InstanceId` to the previous value when an existing volume must be recovered explicitly.

The AppHost generates the `postgres-password-<instance-id>` parameter on first use, marks it secret, and persists it in the AppHost user-secrets store. The persisted identity, password, and volumes form one credential set: do not remove or change one while retaining the others. Never commit the generated password or copy it into `appsettings.json`.

To inspect a development volume without changing it:

```powershell
$instanceId = (Get-Content .agentstration/instance-id -Raw).Trim()
docker volume inspect "agentstration-<slot>-$instanceId-postgresql"
```

To reset disposable PostgreSQL data, first stop the corresponding AppHost and verify the exact slot name. The following operation permanently removes that slot's PostgreSQL database:

```powershell
$instanceId = (Get-Content .agentstration/instance-id -Raw).Trim()
docker volume rm "agentstration-<slot>-$instanceId-postgresql"
dotnet user-secrets remove "Parameters:postgres-password-$instanceId" --project src/Agentstration.AppHost
```

Remove the matching volume and secret only for a full reset. Keep `.agentstration/instance-id` to reuse the same worktree identity with a newly initialized volume, or remove it only after every associated slot volume and secret has been handled. Removing only the user-secret produces repeated `password authentication failed` errors against retained volumes. Back up non-disposable data before removing a volume.

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
