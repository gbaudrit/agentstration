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

Aspire starts `foundry-extension` only when `Foundry:Enabled=true`. It passes the API key as an Aspire secret parameter and provisions the same AEP pairing or SharedKeyFile enrollment used by the other development extensions. The key is not an AEP credential and is not written to Agentstration resources. Leaving `Foundry:Enabled` unset keeps the default offline AppHost unchanged. Managed identity, workload identity and explicit Azure CLI `Development` modes need no `FOUNDRY_API_KEY`; configure the corresponding `AuthenticationMode` instead. See [ADR-0120](decisions/0120-foundry-local-aspire-discovery-is-explicitly-opt-in.md).

## Operator workflow

1. Select an existing Foundry project and a deployed chat model. Grant the runtime identity only the access needed for project deployment listing and inference; consult [Foundry role assignments](https://learn.microsoft.com/en-us/azure/foundry/concepts/rbac-foundry) for the current project and resource scopes. Do not give Agentstration permission to create Foundry deployments. For Entra, use `ManagedIdentity` or `WorkloadIdentity` in a host that supplies that identity; `Development` explicitly uses Azure CLI credentials. The API-key mode is interim until FR-439/FR-452 supplies managed Secret Binding.
2. Start the optional extension. Aspire uses `Foundry:Enabled=true` and the AppHost User Secrets described above. Compose uses `base.yml` plus `foundry.yml` with values in the ignored `deploy/compose/.env.foundry` file; see [Compose instructions](https://github.com/gbaudrit/agentstration/blob/main/deploy/compose/README.md#sqlite). The Compose example uses the interim environment key. It does not provide an Azure CLI or workload identity to a container. Configure and mount that identity explicitly in the hosting environment if using Entra there.
3. Sign in as a tenant administrator. In **Extensions**, verify that `Agentstration.Extensions.Foundry` is enrolled and available. On its detail page, use **Configure provider** for the `microsoft-foundry` contribution. Alternatively, in **Settings → Bootstrap**, apply the optional `foundry` profile to the intended tenant; it creates a Model Provider named `foundry` only after the extension registration exists. The profile is never part of initial Development bootstrap.
4. In **Model providers**, test the Foundry binding and inspect discovered deployments. Choose **Create profile** beside an available deployment to open the existing Model Profile editor with the provider and deployment selected. Save the profile, then select it in an Agent or Flow through the normal Runtime path. The extension never executes Tools; Agentstration's existing approval pipeline governs them.

The project endpoint receives deployment-list requests. The inference endpoint receives the selected deployment name, prompts, response options, Tool declarations and Tool results, and returns model output and usage. Plan network egress and data classification accordingly. The extension follows no redirects or process proxies. Private endpoint DNS must resolve the configured project and inference hosts from the extension process and each host that resolves to private space must appear in `Foundry:AllowedPrivateHosts`; link-local addresses are always blocked. See [Foundry private DNS guidance](https://learn.microsoft.com/en-us/azure/foundry/how-to/upgrade-azure-openai#private-network-configuration).

Rotate an environment API key at its source, then restart the extension process or recreate the Compose service so it reads the new value. Rotate the independent AEP enrollment credential through the existing Extensions workflow; do not copy the Foundry key into a Model Provider or Model Profile. A `401` points to the key or Entra token, `403` to project/resource access, `404` to the project or deployment name, and throttling/timeouts to the service or network. If discovery is empty, verify the project endpoint and that the deployment advertises chat capability. If the extension is disabled or unreachable, its provider and Model Profile remain visible but resolve as unavailable. Diagnostics include status, latency, retries and token counts without provider bodies or prompts.

`Development` authentication mode uses Azure CLI identity explicitly; `ManagedIdentity` and `WorkloadIdentity` do not fall back to developer identities. Workload mode requires `AZURE_TENANT_ID`, `AZURE_CLIENT_ID` and an existing `AZURE_FEDERATED_TOKEN_FILE`. Entra discovery and project-routed inference use `https://ai.azure.com/.default`; resource-routed inference uses `https://cognitiveservices.azure.com/.default`. The temporary key is read at process startup, so rotate it by restarting this extension; FR-452 will replace this with late-bound Agentstration Secret access.

The extension checks each request target before adding credentials, connects directly to an allowed DNS address, and does not follow redirects or process proxies. Private or shared address space requires a configured `Foundry:AllowedPrivateHosts` entry; link-local and reserved addresses remain blocked. Discovery retries a transient page once within its timeout. Chat requests and streams are never retried. Operation logs contain only the deployment, stable outcome, HTTP status, retry count, latency and token counts; prompts, Tool data, keys and provider bodies are omitted. See [ADR-0124](decisions/0124-foundry-egress-and-diagnostics-are-bounded.md).

The extension publishes AEP identity, provider health, model listing, bounded text chat and SSE streaming. Streaming normalizes text, Tool calls, token usage and finish reason in order, requires a completion marker, and rejects malformed, oversized or interrupted streams. AEP carries Tool definitions and results; MAF and Agentstration's existing Tool pipeline retain invocation and approval. Discovery calls `GET {projectEndpoint}/deployments?api-version=v1&deploymentType=ModelDeployment`, follows only same-origin, same-path bounded continuations, and lists deployments only when their metadata affirmatively reports chat capability. Publisher, model and version are exposed as bounded safe metadata; unknown model features are not advertised. Chat calls `POST {inferenceEndpoint}/chat/completions` with the selected deployment name and no automatic redirects.

Structured JSON object and JSON schema output are available only when deployment metadata explicitly advertises `jsonObject` or `jsonSchema`. Explicit reasoning requires `reasoning=true`; selecting an effort additionally requires that value in `reasoningEfforts`. The extension checks current metadata before each advanced inference call, so an unsupported or changed deployment fails before inference. Strict JSON schema output is forwarded through AEP. Foundry-side Tool execution remains unavailable. A real Foundry call may incur provider charges; the required test suite uses a fake provider only. See [ADR-0122](decisions/0122-foundry-streaming-preserves-governed-tool-execution.md) and [ADR-0123](decisions/0123-foundry-advanced-options-follow-deployment-capabilities.md).

No Azure account, key or live model is needed for the default test suite. Optional live validation requires a pre-existing project and deployment and never creates, updates or deletes Foundry resources. Do not enable AEP payload capture or log raw provider responses when using real credentials.

For an intentional live run, set `AGENTSTRATION_FOUNDRY_LIVE=true`, `AGENTSTRATION_FOUNDRY_LIVE_AUTHENTICATION_MODE` to `ApiKeyEnvironment`, `Development`, `ManagedIdentity` or `WorkloadIdentity`, and set `AGENTSTRATION_FOUNDRY_LIVE_PROJECT_ENDPOINT`, `AGENTSTRATION_FOUNDRY_LIVE_INFERENCE_ENDPOINT` and `AGENTSTRATION_FOUNDRY_LIVE_DEPLOYMENT` to existing resources outside the repository. The API-key mode reads `FOUNDRY_API_KEY` from the process environment; Entra modes use their explicitly configured identity. Run `dotnet test tests/Agentstration.ModelProviders.Tests/Agentstration.ModelProviders.Tests.csproj --filter FullyQualifiedName~FoundryLiveTests`. Run the API-key and Entra modes separately. Each run checks discovery, non-streaming chat, streaming and a declared Tool call/result continuation. The Tool check is inconclusive when the deployment does not affirmatively advertise Tools; manual capability overrides remain #482.

## Release stages

1. **Local developer preview:** explicit standalone extension against a test or existing Foundry project; no default bootstrap changes.
2. **Local Aspire discovery and chat test:** explicit AppHost User Secrets opt-in; the existing AEP enrollment path applies. Streaming and governed Tool calls are available for deployments that report Tool calling. This is a local developer test, not the secured orchestrated preview.
3. **Secure orchestrated preview:** after FR-449 and FR-451 validation, with explicit opt-in and an existing Foundry project. AEP enrollment, scope and transport prerequisites #174, #175, #178, #179 and #180 are delivered.
4. **Manual remote extension:** after the same gates plus pairing prerequisites #176 and #177. Managed API-key use awaits FR-439/FR-452; the environment mode remains a documented temporary deployment mechanism.

References: [Foundry project v1 REST API](https://learn.microsoft.com/en-us/rest/api/microsoft-foundry/aiproject), [OpenAI-compatible chat v1 API](https://learn.microsoft.com/en-us/azure/foundry/openai/latest).
