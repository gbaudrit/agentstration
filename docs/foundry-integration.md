# Foundry integration

Microsoft Foundry is an optional AEP model-provider extension, not an Agent hosting mode. Agentstration remains authoritative for Agents, revisions, deployments and desired state. `AgentHostingMode.FoundryHosted` remains unsupported. See [ADR-0119](decisions/0119-foundry-model-deployments-use-an-isolated-aep-extension.md) and [FR-185](https://github.com/gbaudrit/agentstration/issues/185).

## Implementation sequence and gates

| Increment | Outcome | Gate |
| --- | --- | --- |
| [FR-445](https://github.com/gbaudrit/agentstration/issues/445) | Isolated AEP host, explicit identity, health and bounded project deployment discovery | Offline tests, .NET 10 build, no Azure in central projects |
| [FR-446](https://github.com/gbaudrit/agentstration/issues/446) | Non-streaming OpenAI/v1 chat through the ordinary Model Profile and durable Runtime path | Offline AEP, Runtime and Flow tests; verify inference route and Entra audience |
| [FR-450](https://github.com/gbaudrit/agentstration/issues/450) | Streaming and governed tool-call round trip | Cancellation, stream bounds, tool-governance tests |
| [FR-448](https://github.com/gbaudrit/agentstration/issues/448) | Deployment-specific structured output and reasoning | Explicit unsupported options fail before inference |
| [FR-449](https://github.com/gbaudrit/agentstration/issues/449) | Complete egress, retries, error mapping and safe observability | Adversarial offline tests before orchestrated/remote preview |
| [FR-451](https://github.com/gbaudrit/agentstration/issues/451) | Complete the opt-in deployment/Compose/bootstrap and localized Console workflow after the local Aspire discovery-only hook | Default offline startup and both functional test lanes pass; optional live suite |
| [FR-452](https://github.com/gbaudrit/agentstration/issues/452) | Consumer-level Agentstration Secret Binding through [FR-439](https://github.com/gbaudrit/agentstration/issues/439) | Scope/isolation and credential-rotation tests; environment key stops being the managed path |

FR-433 adds explicit descendant grants for higher-scope Secrets. It is not required for a same-tenant Model Provider and Secret binding. Foundry-hosted Agents, model deployment administration and non-chat media APIs are outside this integration.

## Configuration for a local test

`Agentstration.Extensions.Foundry` is an autonomous process and is not started by the default Agentstration profile. Configure it explicitly with the project data-plane endpoint, an OpenAI/v1 inference base URL, and one authentication mode:

| Setting | Meaning |
| --- | --- |
| `Foundry:ProjectEndpoint` | HTTPS project endpoint ending in `/api/projects/{project-name}` |
| `Foundry:InferenceEndpoint` | HTTPS resource `/openai/v1` or project `/api/projects/{project-name}/openai/v1` base URL for chat |
| `Foundry:AuthenticationMode` | Required: `ApiKeyEnvironment`, `ManagedIdentity`, `WorkloadIdentity`, or `Development` |
| `Foundry:ManagedIdentityClientId` | Optional user-assigned client ID, only in `ManagedIdentity` mode |
| `FOUNDRY_API_KEY` | Temporary key for `ApiKeyEnvironment` mode: process environment first, or project User Secrets in Development only; never commit or place in appsettings |
| `Foundry:AllowedPrivateHosts` | Optional comma-separated list of configured endpoint DNS hosts allowed to resolve to private addresses; link-local remains blocked |
| `Foundry:MaximumDiscoveryPages`, `Foundry:MaximumDiscoveredModels`, `Foundry:MaximumDiscoveryResponseBytes`, `Foundry:RequestTimeoutSeconds` | Bounded discovery and timeout overrides |

For Visual Studio local testing, select the Foundry project and **Manage User Secrets**. In the local `secrets.json` stored outside the repository, set `Foundry:ProjectEndpoint`, `Foundry:InferenceEndpoint`, `Foundry:AuthenticationMode` (`ApiKeyEnvironment`) and `FOUNDRY_API_KEY`. The launch environment must be `Development`; in another environment only the `FOUNDRY_API_KEY` process variable is accepted. The environment variable takes precedence if both are set. User Secrets are local development storage, not an encrypted production vault or an Agentstration Secret.

### Opt-in Aspire launch for local tests

When starting **Agentstration.AppHost** in Visual Studio, open **Manage User Secrets** on the *AppHost project*. It has a separate User Secrets store from the standalone Foundry extension. Put the following values there (substitute your endpoint and key locally; never commit the resulting `secrets.json`):

```json
{
  "Foundry": {
    "Enabled": true,
    "ProjectEndpoint": "https://<resource>.services.ai.azure.com/api/projects/<project>",
    "InferenceEndpoint": "https://<resource>.services.ai.azure.com/openai/v1",
    "AuthenticationMode": "ApiKeyEnvironment"
  },
  "FOUNDRY_API_KEY": "<local-development-key>"
}
```

Aspire starts `foundry-extension` only when `Foundry:Enabled=true`. It passes the API key as an Aspire secret parameter and provisions the same AEP pairing or SharedKeyFile enrollment used by the other development extensions. The key is not an AEP credential and is not written to Agentstration resources. Leaving `Foundry:Enabled` unset keeps the default offline AppHost unchanged. Managed identity, workload identity and explicit Azure CLI `Development` modes need no `FOUNDRY_API_KEY`; configure the corresponding `AuthenticationMode` instead. This hook supports local discovery and chat tests only; packaged deployment and secure remote/orchestrated preview remain behind their later FR gates. See [ADR-0120](decisions/0120-foundry-local-aspire-discovery-is-explicitly-opt-in.md).

`Development` authentication mode uses Azure CLI identity explicitly; `ManagedIdentity` and `WorkloadIdentity` do not fall back to developer identities. Workload mode requires `AZURE_TENANT_ID`, `AZURE_CLIENT_ID` and an existing `AZURE_FEDERATED_TOKEN_FILE`. Entra discovery and project-routed inference use `https://ai.azure.com/.default`; resource-routed inference uses `https://cognitiveservices.azure.com/.default`. The temporary key is read at process startup, so rotate it by restarting this extension; FR-452 will replace this with late-bound Agentstration Secret access.

The extension publishes AEP identity, provider health, model listing, bounded text chat and SSE streaming. Streaming normalizes text, Tool calls, token usage and finish reason in order, requires a completion marker, and rejects malformed, oversized or interrupted streams. AEP carries Tool definitions and results; MAF and Agentstration's existing Tool pipeline retain invocation and approval. Discovery calls `GET {projectEndpoint}/deployments?api-version=v1&deploymentType=ModelDeployment`, follows only same-origin, same-path bounded continuations, and lists deployments only when their metadata affirmatively reports chat capability. Publisher, model and version are exposed as bounded safe metadata; unknown model features are not advertised. Chat calls `POST {inferenceEndpoint}/chat/completions` with the selected deployment name and no automatic redirects.

Structured JSON object and JSON schema output are available only when deployment metadata explicitly advertises `jsonObject` or `jsonSchema`. Explicit reasoning requires `reasoning=true`; selecting an effort additionally requires that value in `reasoningEfforts`. The extension checks current metadata before each advanced inference call, so an unsupported or changed deployment fails before inference. Strict JSON schema output is forwarded through AEP. Foundry-side Tool execution remains unavailable. A real Foundry call may incur provider charges; the required test suite uses a fake provider only. See [ADR-0122](decisions/0122-foundry-streaming-preserves-governed-tool-execution.md) and [ADR-0123](decisions/0123-foundry-advanced-options-follow-deployment-capabilities.md).

No Azure account, key or live model is needed for the default test suite. The optional live test requires a pre-existing project with read access and never creates, updates or deletes Foundry resources. Do not enable AEP payload capture or log raw provider responses when using real credentials.

## Release stages

1. **Local developer preview:** explicit standalone extension against a test or existing Foundry project; no default bootstrap changes.
2. **Local Aspire discovery and chat test:** explicit AppHost User Secrets opt-in; the existing AEP enrollment path applies. Streaming and governed Tool calls are available for deployments that report Tool calling. This is a local developer test, not the secured orchestrated preview.
3. **Secure orchestrated preview:** only after FR-449 and the remaining topology/Console work of FR-451. AEP enrollment, scope and transport prerequisites #174, #175, #178, #179 and #180 are already delivered.
4. **Manual remote extension:** only after the same security gate plus pairing prerequisites #176 and #177. Managed API-key use awaits FR-439/FR-452; the environment mode remains a documented temporary deployment mechanism.

References: [Foundry project v1 REST API](https://learn.microsoft.com/en-us/rest/api/microsoft-foundry/aiproject), [OpenAI-compatible chat v1 API](https://learn.microsoft.com/en-us/azure/foundry/openai/latest).
