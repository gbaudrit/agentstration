# Prerequisites

## Required

- Git, to clone and version the repository.
- The .NET SDK selected by the repository's `global.json`: currently `10.0.300`, with `latestFeature` roll-forward.

Verify the SDK:

```powershell
dotnet --version
```

## Optional

- Node.js 20 or later and npm, only to build or serve this documentation site.
- A locally installed and running Ollama server, to execute the seeded managed Ollama profile. Aspire connects to it but does not install or provision it.
- A `llama-server` executable and local GGUF model, only when using the `llamacpp` provider. Agentstration can connect to an existing server and does not require Docker.

No cloud subscription or remote model credential is required for deterministic local operation.
