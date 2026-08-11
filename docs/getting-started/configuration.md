# Configuration

Agentstration uses standard ASP.NET Core configuration. JSON settings can be overridden by environment variables using double underscores, for example `AI__Provider=Deterministic`.

The main verified settings are:

| Setting | Current default | Purpose |
| --- | --- | --- |
| `AI:Provider` | `Managed` in Console; `Deterministic` in Work API | Selects model resolution/execution mode. |
| `Data:Path` | `.agentstration/data.json` | Content and memory store used by the Console host. |
| `Data:ControlPlanePath` | `.agentstration/control-plane.db` | Management Plane SQLite database. |
| `Data:WorkPlanePath` | `.agentstration/work-plane.db` | Work Plane SQLite database. |
| `Data:FlowPath` | `.agentstration/flow-plane.db` | Flow SQLite database. |
| `Agentstration:WorkApi:BaseAddress` | `http://localhost:5080/` | Console-to-Work-API connection. |
| `Agentstration:ApiBaseUrl` | `http://localhost:5080/` | Workplace-to-Work-API connection. |
| `Agentstration:WorkplaceHubUrl` | `http://localhost:5080/hubs/workplace` | Workplace real-time endpoint. |

Provider-specific options and persisted model resources are described in [Model providers](../concepts/model-providers.md) and [Model profiles](../concepts/model-profiles.md). Do not store secrets in committed settings files.
