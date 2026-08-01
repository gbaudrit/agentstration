# Standalone mode

Standalone mode requires only .NET 10. It uses SQLite for control-plane resources, the local runtime registry for reconstructible instances, and the deterministic `IChatClient` by default.

```powershell
dotnet run --project src/Agentstration.Web
```

On first start, the host creates `.agentstration/control-plane.db`, registers the `readonly-expert` type, creates `dotnet-expert` and `sql-expert`, publishes one revision for each, deploys both in-process, and reconciles them to `Ready`.

The files under `samples/manifests/` document the provider-neutral JSON/YAML manifest shape. Automated manifest import and YAML parsing remain a subsequent increment.
