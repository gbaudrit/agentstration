# Foundry integration

Microsoft Foundry is an optional AEP model-provider extension, not an Agent hosting mode. One `Agentstration.Extensions.Foundry` process can serve several independently configured Model Providers. Agentstration remains authoritative for Agents, Model Profiles, revisions, deployments and Tool governance. See [ADR-0134](decisions/0134-foundry-connections-are-provider-owned-aep-values.md).

## Start the optional extension

Foundry is never required by the deterministic SQLite default.

- Aspire: set only `Foundry:Enabled=true` on AppHost. The extension starts with the normal development AEP enrollment path.
- Compose: start `base.yml` with the `foundry.yml` overlay. The overlay creates a separate AEP shared key and registers the extension.
- Standalone extension: start `src/Agentstration.Extensions.Foundry` and configure its AEP enrollment as for any autonomous extension.

Project endpoints, authentication mode and credentials are not extension startup settings. Do not put them in AppHost User Secrets, Compose environment files, extension `appsettings`, or process environment variables.

The remaining `Foundry` settings are operator-owned technical policy:

| Setting | Purpose |
| --- | --- |
| `Foundry:AllowedPrivateHosts` | Comma-separated DNS hosts explicitly trusted to resolve to private/shared address space. A Model Provider endpoint never adds itself to this allowlist. |
| `Foundry:MaximumDiscoveryPages` | Bounded deployment-list pages, 1–50. |
| `Foundry:MaximumDiscoveredModels` | Bounded discovered deployments, 1–1000. |
| `Foundry:MaximumDiscoveryResponseBytes` | Per-page response bound, 1 KiB–8 MiB. |
| `Foundry:RequestTimeoutSeconds` | Whole-operation timeout, 1–120 seconds. |

## Configure a Model Provider

1. In **Parameters**, create three string Parameters for the project endpoint, inference endpoint and authentication mode.
2. For `ApiKey`, create a Secret containing the Foundry key. The Secret value is never copied into the Model Provider.
3. In **Extensions**, verify `Agentstration.Extensions.Foundry`, then create a Model Provider for contribution `microsoft-foundry`.
4. Bind `projectEndpoint`, `inferenceEndpoint` and `authenticationMode` to the three Parameters. A standard requirement may instead bind a Secret.
5. Bind secured `credential` to the Secret. A Parameter is rejected for this requirement.
6. Test the connection and inspect discovered deployments. Create a Model Profile from the selected deployment.
7. Assign the Model Profile to an Agent or Flow and run streaming chat. Token usage and Tool calls continue through the ordinary Runtime, approval and governed Tool-execution pipeline.

The optional tenant Bootstrap profile `foundry` asks for those three Parameters and one Secret, then creates the bound Model Provider. It does not create a model, Azure resource, credential or Model Profile.

## Bound values

The contribution publishes these values:

| Requirement | Protection | Required | Validation |
| --- | --- | --- | --- |
| `projectEndpoint` | standard | yes | String URI; HTTPS DNS URL ending in `/api/projects/{project}`. |
| `inferenceEndpoint` | standard | yes | String URI; resource `/openai/v1` or matching project `/openai/v1` route. |
| `authenticationMode` | standard | yes | `ApiKey`, `ManagedIdentity`, `WorkloadIdentity`, or `Development`. |
| `credential` | secured | for `ApiKey` | One-use Secret grant redeemed through `/api/aep/secrets/redeem`. |
| `managedIdentityClientId` | standard | for user-assigned `ManagedIdentity` | Client ID UUID; rejected in other modes. |
| `workloadIdentityTenantId` | standard | for `WorkloadIdentity` | Tenant UUID. |
| `workloadIdentityClientId` | standard | for `WorkloadIdentity` | Client ID UUID. |
| `workloadIdentityTokenFile` | standard | for `WorkloadIdentity` | Absolute token-file path visible to the extension host. |

`Development` explicitly uses Azure CLI identity and accepts no downstream credential or identity values. Each discovery, capability check, chat and stream resolves current bindings again. Parameter edits, Secret rotation, deletion, revocation or access-policy changes therefore affect the next operation.

## Security and runtime behavior

The extension accepts only credential-free HTTPS DNS endpoints without query or fragment and validates project and inference paths. It disables redirects and process proxies, resolves DNS at connection time, filters every address, and adds credentials only after exact method, origin and path validation. Link-local, multicast and reserved destinations remain blocked. Private endpoints require an operator allowlist entry; a bound endpoint cannot self-authorize.

Discovery is bounded and may retry one transient read-only page once. Chat and streaming requests are never replayed. Streaming requires a valid completion marker and finish reason, preserves text/usage/finish ordering, propagates cancellation, and rejects malformed, interrupted or oversized flows. Structured output and reasoning are enabled only when current deployment metadata explicitly advertises them. Foundry never executes Tools itself.

Logs and diagnostics contain stable operation outcomes, HTTP status, retry count, duration, deployment identity and token counts only. They exclude prompts, Tool arguments/results, endpoint values, credentials, Secret names, grants, provider bodies and resolved values. AEP tracing and Inspector redact bound values as a unit.

## Optional live validation

The required suite is fully offline. Intentional Azure validation uses an existing project and deployment and may incur charges. Set `AGENTSTRATION_FOUNDRY_LIVE=true`, select one `AGENTSTRATION_FOUNDRY_LIVE_AUTHENTICATION_MODE`, and provide the `AGENTSTRATION_FOUNDRY_LIVE_PROJECT_ENDPOINT`, `AGENTSTRATION_FOUNDRY_LIVE_INFERENCE_ENDPOINT` and `AGENTSTRATION_FOUNDRY_LIVE_DEPLOYMENT` values outside the repository. `ApiKey` additionally uses `AGENTSTRATION_FOUNDRY_LIVE_API_KEY`; identity modes use the documented identity-specific variables.

Run:

```powershell
dotnet test tests/Agentstration.ModelProviders.Tests/Agentstration.ModelProviders.Tests.csproj --filter FullyQualifiedName~FoundryLiveTests
```

No live test creates, updates or deletes a Foundry resource.
