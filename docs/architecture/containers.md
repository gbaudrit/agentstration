# Deployable containers and processes

Agentstration remains one modular codebase but has several executable hosts:

| Process | Responsibility | Default URL |
| --- | --- | --- |
| `Agentstration.Web` | Operations Console plus Management, Runtime, Flow, Work, content, and MCP surfaces | `http://localhost:5100` |
| `Agentstration.Work.Api` | Autonomous Work API, Workplace API, Flow API, workers, and SignalR hub | `http://localhost:5080` |
| `Agentstration.Workplace.Web` | End-user Blazor UI; communicates only through Work API/SignalR | `http://localhost:5180` |
| `Agentstration.AppHost` | Aspire development orchestration of the local processes and optional dependencies | Aspire-assigned |

Workplace Web does not reference Console, runtime, provider, application, or storage implementations. Console supervision reads Work API rather than Work SQLite. This separation is deployable locally without turning the codebase into microservices.
