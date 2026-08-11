# Standalone mode

Standalone is the executable default, not a reduced cloud-dependent profile. It runs with the .NET SDK, local files/SQLite, bounded in-process queues, and deterministic AI.

```powershell
$env:AI__Provider = "Deterministic"
dotnet run --project src/Agentstration.Web
```

On startup the Management host seeds local resources and reconciles reconstructible in-process runtime instances. Optional Ollama, Aspire, Docker, remote APIs, and Foundry integrations can extend this mode but are not prerequisites.

Known local tradeoffs include in-process queue durability limits and development-stage authentication/authorization. See [Standalone mode implementation note](../standalone.md).
