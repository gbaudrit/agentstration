# Configuration

Agentstration uses standard ASP.NET Core configuration. JSON settings can be overridden by environment variables using double underscores, for example `AI__Provider=Deterministic`.

The main verified settings are:

| Setting | Current default | Purpose |
| --- | --- | --- |
| `AI:Provider` | `Managed` | Selects model resolution/execution mode. Use `Deterministic` explicitly for offline or test execution. |
| `LlamaCpp:Endpoint` | `http://localhost:8080` | Native llama.cpp server used by the autonomous llama.cpp extension and Aspire. |
| `Data:Path` | `.agentstration/data.json` | Legacy content store used by the Console host. |
| `Data:MemoryPath` | `.agentstration/memory-plane.db` | Builtin governed Memory SQLite database. |
| `Data:ControlPlanePath` | `.agentstration/control-plane.db` | Management Plane SQLite database. |
| `Data:WorkPlanePath` | `.agentstration/work-plane.db` | Work Plane SQLite database. |
| `Data:FlowPath` | `.agentstration/flow-plane.db` | Flow SQLite database. |
| `ConnectionStrings:Identity` | `.agentstration/identity.db` | ASP.NET Core Identity account database, managed through EF Core migrations. |
| `Agentstration:Authentication:DataProtectionKeysPath` | `.agentstration/data-protection-keys` | Persistent ASP.NET Core Data Protection key ring for cookies and Identity tokens. |
| `Agentstration:WorkApi:BaseAddress` | `http://localhost:5100/` | Console-to-Work-API connection on the authoritative server. |
| `Agentstration:ApiBaseUrl` | `http://localhost:5100/` | Workplace-to-server API connection. |
| `Agentstration:WorkplaceHubUrl` | `http://localhost:5100/hubs/workplace` | Workplace real-time endpoint. |
| `MemorySqlite:Path` | `.agentstration/memory-sqlite-extension.db` in AppHost | Database owned by the autonomous SQLite AEP Memory extension. |
| `Agentstration:Extensions:Agentstration.Extensions.Memory.Sqlite:Endpoint` | unset for direct Web startup | AEP endpoint used by an external SQLite Memory provider; Aspire supplies it automatically. |

Provider-specific options and persisted model resources are described in [Model providers](../concepts/model-providers.md) and [Model profiles](../concepts/model-profiles.md). Do not store secrets in committed settings files.
