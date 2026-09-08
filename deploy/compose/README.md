# Docker Compose topologies

Run these commands from the repository root. Only one provider topology should run at a time because each publishes Agentstration on `http://localhost:5100` by default.

## SQLite

Minimal deterministic topology:

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

## PostgreSQL

Create the ignored environment file once, then replace its disposable development password:

```powershell
Copy-Item deploy/compose/.env.postgresql.example deploy/compose/.env.postgresql
```

Minimal deterministic topology:

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

## Operations

Use the same file selection that started the topology. For example, with Ollama and SQLite:

```powershell
docker compose -f deploy/compose/ollama.yml ps
docker compose -f deploy/compose/ollama.yml logs -f
docker compose -f deploy/compose/ollama.yml down
```

For a PostgreSQL variant, include both the environment file and `postgresql.yml` in operational commands as well.

Named volumes preserve Agentstration, PostgreSQL, Ollama, and LocalAI data across container recreation. Do not add `--volumes` to `down` unless that persisted data should be deleted explicitly.
