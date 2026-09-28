# Standalone mode

Standalone is the executable default, not a reduced cloud-dependent profile. It runs with the .NET SDK, local files/SQLite, bounded in-process queues, and deterministic AI.

```powershell
$env:AI__Provider = "Deterministic"
dotnet run --project src/Agentstration.Web
```

On startup the authoritative server initializes module-owned stores and reconciles reconstructible in-process Runtime instances. Initial Bootstrap is optional. The standard Development launch profiles enable the ordered `development` Bootstrap profile for a fresh local instance and create the documented disposable `admin / admin` fixture; no credential or topology is implicit outside Development.

The direct host embeds the Console and remains the all-in-one executable default. Independently hosted Console and Workplace shells consume the authoritative APIs and own no business state. Aspire development orchestration and provider-specific Compose profiles preserve the same server boundary while optionally adding those shells, AEP extensions, inference services, or PostgreSQL. Deterministic AI remains the offline validation and fallback path; provider integrations are optional.
