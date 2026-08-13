# Deployable containers and processes

Agentstration remains one modular codebase but has several executable hosts:

| Process | Responsibility | Default URL |
| --- | --- | --- |
| `Agentstration.Web` | Operations Console plus Management, Runtime, Flow, Work, content, and MCP surfaces | `http://localhost:5100` |
| `Agentstration.Workplace.Web` | End-user Blazor UI; communicates only through the APIs and SignalR hosted by `Agentstration.Web` | `http://localhost:5180` |
| `Agentstration.AppHost` | Aspire development orchestration of the local processes and optional dependencies | Aspire-assigned |

Workplace Web does not reference the server host, runtime, provider, application, or storage implementations. Console supervision uses the public Work API rather than Work SQLite. The independently deployable UI does not create a second server-side authority.
