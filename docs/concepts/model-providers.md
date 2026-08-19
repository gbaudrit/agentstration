# Model providers

A Model Provider describes how Agentstration reaches an out-of-process AEP extension contribution and what that provider can do. Provider declarations are durable Management resources with connectivity testing, dynamic model discovery, ETag concurrency, usage visibility, and deletion protection.

Ollama, llama.cpp, and LocalAI are implemented as independent AEP contributions in their corresponding `Agentstration.Extensions.*` hosts. The persisted endpoint is always the extension URL rather than the native inference-server URL. Cloud services are optional. Provider endpoints and credentials do not belong in portable Agent definitions.

## LocalAI

Start an existing LocalAI server on host port 8081. This avoids colliding with the default llama.cpp endpoint on port 8080. A container that listens on 8080 can be published with a host mapping such as `8081:8080`.

Aspire starts only the Agentstration extension. For direct startup:

```powershell
$env:LocalAI__Endpoint = "http://localhost:8081"
# Optional: $env:LocalAI__ApiKey = "..."
dotnet run --project src/Agentstration.Extensions.LocalAI
```

The extension listens on `http://localhost:5280` with its development launch profile. Declare that AEP endpoint as the Model Provider endpoint:

```yaml
apiVersion: agentstration.io/v1
kind: ModelProvider
metadata:
  name: localai-local
definition:
  displayName: LocalAI local
  providerType: localai
  endpoint: http://localhost:5280
  managementMode: external
```

Model discovery requires LocalAI's additive `/v1/models/capabilities` endpoint and exposes only entries that report `chat`. Tool and thinking flags are mapped per model. Vision is not effective through the current AEP adapter, and structured output is not advertised because LocalAI support varies by backend. Streaming uses OpenAI-compatible SSE Chat Completions.

Only `frequencyPenalty` and `presencePenalty` are accepted under the `localai` provider-options key. Arbitrary LocalAI metadata is rejected: in particular, `metadata.mcp_servers` cannot activate provider-owned MCP tools and bypass Agentstration governance. Portable generation options remain canonical Model Profile fields.

The default tests use fake HTTP. To run the optional real-server smoke test:

```powershell
$env:AGENTSTRATION_LOCALAI_ENDPOINT = "http://localhost:8081"
$env:AGENTSTRATION_LOCALAI_MODEL = "your-chat-model"
# Optional: $env:AGENTSTRATION_LOCALAI_API_KEY = "..."
dotnet test tests/Agentstration.ModelProviders.Tests --filter TestCategory=Integration
```

## llama.cpp

Start an existing `llama-server` with a local GGUF model. Giving the model a stable alias makes the Model Profile independent from its filesystem path:

```powershell
llama-server -m C:\models\model.gguf --alias local-gguf --host 127.0.0.1 --port 8080 --jinja
```

Aspire starts the Agentstration extension and points it at `http://localhost:8080` by default; it does not install llama.cpp, download a model, or require Docker. For direct startup, run the extension separately:

```powershell
$env:LlamaCpp__Endpoint = "http://localhost:8080"
dotnet run --project src/Agentstration.Extensions.LlamaCpp
```

The extension listens on `http://localhost:5270` with its development launch profile. Declare that AEP endpoint—not port 8080—as the Model Provider endpoint:

```yaml
apiVersion: agentstration.io/v1
kind: ModelProvider
metadata:
  name: llama-cpp-local
definition:
  displayName: llama.cpp local
  providerType: llamacpp
  endpoint: http://localhost:5270
  managementMode: external
```

Then create a normal Model Profile:

```yaml
apiVersion: agentstration.io/v1
kind: ModelProfile
metadata:
  name: local-gguf
definition:
  displayName: Local GGUF
  provider:
    name: llama-cpp-local
  model:
    name: local-gguf
  generation:
    temperature: 0.2
    maxOutputTokens: 1024
  providerOptions:
    llamacpp:
      minP: 0.05
      repeatPenalty: 1.1
```

Supported functional capabilities are chat completions, true SSE streaming, model discovery, readiness, schema-constrained structured output, and tool calling when `/props` reports a tool-capable chat template. Reasoning controls are mapped when the model/template supports them, but reasoning remains partial because AEP does not yet expose reasoning content as a distinct content kind. Vision may be reported by llama.cpp discovery but is not effective through the current text/tool AEP adapter. The native `/completion` endpoint is intentionally not exposed as chat; the managed Runtime currently consumes chat completions through `IChatClient`.

The Model Provider page can test the AEP connection and shows discovered models with their reported capabilities. The Model Profile page adds a runtime-independent compatibility diagnosis: it intersects provider, selected model, and AEP adapter capabilities, then checks the profile's reasoning and structured-output requirements. Runtime capabilities and an agent's tool requirements are evaluated later when that agent is resolved, so the profile diagnosis deliberately does not claim full execution compatibility.

Provider-specific keys currently mapped by the extension include `minP`, `typicalP`, `repeatPenalty`, `repeatLastN`, `mirostat`, `mirostatTau`, `mirostatEta`, `reasoningFormat`, `reasoningEffort`, `chatTemplateKwargs`, and `additionalOptions`. Portable temperature, top-p, top-k, seed, stop sequences, maximum output tokens, reasoning intent, and output format remain canonical Model Profile fields.

The default tests use fake HTTP and require no model. To run the optional real-server smoke test:

```powershell
$env:AGENTSTRATION_LLAMA_CPP_ENDPOINT = "http://localhost:8080"
$env:AGENTSTRATION_LLAMA_CPP_MODEL = "local-gguf"
dotnet test tests/Agentstration.ModelProviders.Tests --filter TestCategory=Integration
```
