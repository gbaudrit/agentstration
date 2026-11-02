# Local installation

Clone the repository, restore the solution, and build it:

```powershell
git clone https://github.com/gbaudrit/agentstration.git
cd agentstration
dotnet restore Agentstration.slnx
dotnet build Agentstration.slnx --configuration Release --no-restore
```

Run the operations Console with the offline deterministic provider:

```powershell
$env:AI__Provider = "Deterministic"
dotnet run --project src/Agentstration.Web
```

For the end-user Workplace, use two terminals:

```powershell
# Terminal 1
$env:AI__Provider = "Deterministic"
dotnet run --project src/Agentstration.Web

# Terminal 2
dotnet run --project src/Agentstration.Workplace.Web
```

The same `Agentstration.Web` process is the authoritative server for Console and Workplace APIs. The direct server defaults to the persisted managed provider configuration, which is why the explicit environment override is used in the offline commands above.

## Optional Aspire LAN profile

Select `https-LAN` on the `Agentstration.AppHost` startup project in Visual Studio, or run:

```powershell
dotnet run --project src/Agentstration.AppHost --launch-profile https-LAN
```

Use `https-LAN-NoBootstrap` to omit initial bootstrap. The usual `https` and `https-NoBootstrap` profiles remain local-only. The LAN profiles reuse the same instance and data directory; switching profiles does not create or reset an instance.

All Aspire profiles use the configured Docker endpoint and context without overriding `DOCKER_HOST`. When using container-backed resources such as managed Crawl4AI or PostgreSQL, verify `docker info` succeeds in the launching environment. Docker Desktop's active context can use its normal local connection; the launch profiles do not require a daemon listening on TCP port `2375`.

The LAN profiles add HTTPS listeners on all IPv4 interfaces, with fixed ports:

| Application | Address from another machine |
| --- | --- |
| Console and authoritative API | `https://<host-name-or-LAN-IP>:7101` |
| Workplace | `https://<host-name-or-LAN-IP>:7181` |

The dashboard, telemetry, extensions, Workers, and existing service-discovery endpoints retain their local bindings. The Console's Workplace link uses the host machine name by default. If that name does not resolve on the other machine, set a LAN IP or resolvable name before starting Aspire:

```powershell
$env:Agentstration__Lan__Host = "192.168.1.50"
dotnet run --project src/Agentstration.AppHost --launch-profile https-LAN
```

`Agentstration:Lan:Host` controls the public Workplace link; it does not change the listener binding. The LAN profiles override the Console's `AllowedHosts` with `*` so requests addressed by a LAN IP or host name are accepted. The usual profiles retain the loopback-only host allowlist. Allow inbound TCP ports `7101` and `7181` in the Windows firewall only for the intended private network or test machine. No firewall rule is installed automatically. Keep the ports closed on public networks and do not forward them from an Internet router.

Configure a server certificate covering the chosen host name or IP and trust its issuer on the client machine. The default development certificate is for `localhost` and will produce a certificate error when accessed using the LAN address. The additional endpoints use each application's normal Kestrel HTTPS certificate configuration; for a local PFX, supply `Kestrel__Certificates__Default__Path` and, if required, `Kestrel__Certificates__Default__Password` in the launching environment. Keep certificate files and passwords out of version control.

The Development bootstrap creates the public disposable `admin / admin` fixture. Change that password before granting access to other machines. After stopping the LAN profile and restarting the usual profile, the additional listeners are absent.

## Run with Compose

All Compose definitions live under `deploy/compose`. The colocated [Compose command reference](https://github.com/gbaudrit/agentstration/blob/main/deploy/compose/README.md) provides copy-ready SQLite, PostgreSQL, model-management, log, and shutdown commands. The `base.yml` topology retains the canonical deterministic extension launch without inference servers. Choose one provider-specific topology to run its inference service, AEP extension, and Utilities extension with isolated SharedKeyFile credentials.

For a container-owned Ollama instance:

```powershell
docker compose -f deploy/compose/ollama.yml up --build
```

The Ollama topology builds the authoritative server plus the Ollama and Utilities AEP extensions. It also starts an Ollama inference server with a persistent `ollama-data` volume. Only Agentstration is published to the host, at `http://localhost:5100` by default; extension and Ollama traffic stay on the Compose network.

For llama.cpp, first place a GGUF model in the ignored `.models/llama-cpp` directory. Its filename defaults to `model.gguf` and can be changed through `LLAMA_CPP_MODEL`:

```powershell
New-Item -ItemType Directory -Force .models/llama-cpp
Copy-Item C:\path\to\model.gguf .models/llama-cpp/model.gguf
docker compose -f deploy/compose/llama-cpp.yml up --build
```

For LocalAI:

```powershell
docker compose -f deploy/compose/localai.yml up --build
docker compose -f deploy/compose/localai.yml exec localai local-ai models install <model>
```

Both topologies run their inference server inside Docker; neither needs `aep-host` or `host.docker.internal`. Run only the topology for the provider being tested. The provider-specific files publish the same Agentstration port and share the same default Compose project data volume.

| Variable | Container default | Purpose |
| --- | --- | --- |
| `AGENTSTRATION_HTTP_PORT` | `5100` | Host port bound to the authoritative Agentstration server |
| `OLLAMA_IMAGE` | `ollama/ollama:0.33.2` | Ollama container image |
| `LLAMA_CPP_IMAGE` | `ghcr.io/ggml-org/llama.cpp:server-b10830` | llama.cpp server image |
| `LLAMA_CPP_MODELS_PATH` | `./.models/llama-cpp` | Host directory mounted read-only at `/models` |
| `LLAMA_CPP_MODEL` | `model.gguf` | GGUF filename inside the llama.cpp model directory |
| `LLAMA_CPP_MODEL_ALIAS` | `local-gguf` | Stable model name exposed by llama-server |
| `LOCALAI_IMAGE` | `localai/localai:v4.9.0` | LocalAI server image |
| `LOCALAI_API_KEY` | unset | Optional LocalAI Bearer credential |
| `AI_PROVIDER` | `Managed` | Agentstration execution mode; set `Deterministic` for the offline fallback |

Install a model explicitly after startup:

```powershell
docker compose -f deploy/compose/ollama.yml exec ollama ollama pull qwen3:1.7b
docker compose -f deploy/compose/ollama.yml exec ollama ollama run qwen3:1.7b
docker compose -f deploy/compose/ollama.yml exec ollama ollama ps
```

The model volume survives container recreation. Remove it only through an explicit Compose volume deletion when the cached models are no longer needed.

The default Ollama container is CPU-capable. Ollama's Linux image can use Vulkan when the Docker host exposes `/dev/dri`, but that device is not available in every WSL configuration. Do not add a `/dev/dri` mapping until the device exists on the Docker host; Compose would otherwise fail before Ollama starts. The `PROCESSOR` column from `ollama ps` reports whether a loaded model uses CPU, GPU, or both.

The default images are CPU-capable. GPU variants require the corresponding device to be exposed by the Docker host; keep the CPU images until GPU passthrough is verified.

### PostgreSQL variants

The shared `postgresql.yml` overlay can be combined with the base or any provider-specific topology. Create its ignored environment file once and replace the disposable password:

```powershell
Copy-Item deploy/compose/.env.postgresql.example deploy/compose/.env.postgresql
```

Then select the required variant:

```powershell
# Minimal deterministic + PostgreSQL
docker compose --env-file deploy/compose/.env.postgresql -f deploy/compose/base.yml -f deploy/compose/postgresql.yml up --build

# Ollama + PostgreSQL
docker compose --env-file deploy/compose/.env.postgresql -f deploy/compose/ollama.yml -f deploy/compose/postgresql.yml up --build

# llama.cpp + PostgreSQL
docker compose --env-file deploy/compose/.env.postgresql -f deploy/compose/llama-cpp.yml -f deploy/compose/postgresql.yml up --build

# LocalAI + PostgreSQL
docker compose --env-file deploy/compose/.env.postgresql -f deploy/compose/localai.yml -f deploy/compose/postgresql.yml up --build
```
