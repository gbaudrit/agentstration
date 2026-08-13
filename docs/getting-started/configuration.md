# Configuration

Agentstration uses standard ASP.NET Core configuration. JSON settings can be overridden by environment variables using double underscores, for example `AI__Provider=Deterministic`.

The main verified settings are:

| Setting | Current default | Purpose |
| --- | --- | --- |
| `AI:Provider` | `Managed` | Selects model resolution/execution mode. Use `Deterministic` explicitly for offline or test execution. |
| `Data:Path` | `.agentstration/data.json` | Content and memory store used by the Console host. |
| `Data:ControlPlanePath` | `.agentstration/control-plane.db` | Management Plane SQLite database. |
| `Data:WorkPlanePath` | `.agentstration/work-plane.db` | Work Plane SQLite database. |
| `Data:FlowPath` | `.agentstration/flow-plane.db` | Flow SQLite database. |
| `Agentstration:WorkApi:BaseAddress` | `http://localhost:5100/` | Console-to-Work-API connection on the authoritative server. |
| `Agentstration:ApiBaseUrl` | `http://localhost:5100/` | Workplace-to-server API connection. |
| `Agentstration:WorkplaceHubUrl` | `http://localhost:5100/hubs/workplace` | Workplace real-time endpoint. |

Provider-specific options and persisted model resources are described in [Model providers](../concepts/model-providers.md) and [Model profiles](../concepts/model-profiles.md). Do not store secrets in committed settings files.
