# Model providers

A Model Provider describes how Agentstration reaches an out-of-process AEP extension contribution and what that provider can do. Provider declarations are durable Management resources with connectivity testing, dynamic model discovery, ETag concurrency, usage visibility, and deletion protection.

Ollama and llama.cpp are implemented as independent AEP contributions in `Agentstration.Extensions.Ollama` and `Agentstration.Extensions.LlamaCpp`. The persisted endpoint is always the extension URL rather than the native inference-server URL. Cloud services are optional. Provider endpoints and credentials do not belong in portable Agent definitions.

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
