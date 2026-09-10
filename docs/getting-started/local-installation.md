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

## Run with Compose

All Compose definitions live under `deploy/compose`. The colocated [Compose command reference](../../deploy/compose/README.md) provides copy-ready SQLite, PostgreSQL, model-management, log, and shutdown commands. The `base.yml` topology retains the canonical deterministic extension launch without inference servers. Choose one provider-specific topology to run its inference service, AEP extension, and Utilities extension with isolated SharedKeyFile credentials.

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
