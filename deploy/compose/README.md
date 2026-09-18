# Docker Compose topologies

Run these commands from the repository root. Only one provider topology should run at a time because each publishes Agentstration on `http://localhost:5100` by default.

## SQLite

Canonical deterministic extension topology without inference servers:

```powershell
docker compose -f deploy/compose/base.yml up -d --build --remove-orphans
```

Ollama:

```powershell
docker compose -f deploy/compose/ollama.yml up -d --build --remove-orphans
docker compose -f deploy/compose/ollama.yml exec ollama ollama pull qwen3:1.7b
docker compose -f deploy/compose/ollama.yml exec ollama ollama run qwen3:1.7b
```

llama.cpp requires a GGUF model in the ignored `.models/llama-cpp` directory:

```powershell
New-Item -ItemType Directory -Force .models/llama-cpp
Copy-Item C:\path\to\model.gguf .models/llama-cpp/model.gguf
docker compose -f deploy/compose/llama-cpp.yml up -d --build --remove-orphans
```

LocalAI:

```powershell
docker compose -f deploy/compose/localai.yml up -d --build --remove-orphans
docker compose -f deploy/compose/localai.yml exec localai local-ai models install <model>
```

Microsoft Foundry is an opt-in overlay on the deterministic base. Copy the example to the ignored local file, enter an existing project endpoint, inference endpoint and temporary API key there, then start both files:

```powershell
Copy-Item deploy/compose/.env.foundry.example deploy/compose/.env.foundry
docker compose --env-file deploy/compose/.env.foundry -f deploy/compose/base.yml -f deploy/compose/foundry.yml up -d --build --remove-orphans
```

The overlay adds only the Foundry extension, its own generated AEP shared key, the allowlisted internal host and the Bootstrap catalog. It does not deploy models or enable Foundry by default. Apply the `foundry` tenant Bootstrap profile from **Settings → Bootstrap**, or bind the extension manually in **Extensions → Model providers**; then choose a discovered deployment in a Model Profile. Keep `.env.foundry` local and use `docker compose config` only in a private terminal because it expands environment values. See [Foundry integration](../../docs/foundry-integration.md#operator-workflow).

## PostgreSQL

Create the ignored environment file once, then replace its disposable development password:

```powershell
Copy-Item deploy/compose/.env.postgresql.example deploy/compose/.env.postgresql
```

Canonical deterministic extension topology:

```powershell
docker compose --env-file deploy/compose/.env.postgresql -f deploy/compose/base.yml -f deploy/compose/postgresql.yml up -d --build --remove-orphans
```

Ollama:

```powershell
docker compose --env-file deploy/compose/.env.postgresql -f deploy/compose/ollama.yml -f deploy/compose/postgresql.yml up -d --build --remove-orphans
```

llama.cpp:

```powershell
docker compose --env-file deploy/compose/.env.postgresql -f deploy/compose/llama-cpp.yml -f deploy/compose/postgresql.yml up -d --build --remove-orphans
```

LocalAI:

```powershell
docker compose --env-file deploy/compose/.env.postgresql -f deploy/compose/localai.yml -f deploy/compose/postgresql.yml up -d --build --remove-orphans
```

Foundry can use the same PostgreSQL overlay: include `.env.foundry`, `base.yml`, `foundry.yml` and `postgresql.yml` in that order. The default remains SQLite.

## Operations

Use the same file selection that started the topology. For example, with Ollama and SQLite:

```powershell
docker compose -f deploy/compose/ollama.yml ps
docker compose -f deploy/compose/ollama.yml logs -f
docker compose -f deploy/compose/ollama.yml down
```

For a PostgreSQL variant, include both the environment file and `postgresql.yml` in operational commands as well.

Named volumes preserve Agentstration, PostgreSQL, Ollama, and LocalAI data across container recreation. Do not add `--volumes` to `down` unless that persisted data should be deleted explicitly.
