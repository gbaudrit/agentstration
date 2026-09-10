# Source registry registrations

Source registries are instance-owned discovery endpoints administered by Platform administrators. They are separate from the static `SourceRegistryIndex`/`SourceRegistry` publication and from imported Sources.

The API supports:

- `GET /api/sourceregistries` and `GET /api/sourceregistries/{name}`;
- `POST /api/sourceregistries` and ETag-protected `PUT`/`DELETE` operations;
- `POST /api/sourceregistries/{name}/refresh` and refresh history.

The official `agentstration-official` registration is created only when absent. Local changes survive restart and product upgrades. It can be disabled or reconfigured but not deleted.

## Private enterprise example

Create an instance-scoped Vault and Secret through the normal Secret APIs, store the Bearer token through the write-only Secret value endpoint, and retain only this reference in the registration:

```json
{
  "name": "contoso-registry",
  "properties": {
    "displayName": "Contoso registry",
    "indexUrl": "https://registry.contoso.internal/v1/index.json",
    "enabled": true,
    "trustPolicy": "trusted",
    "endpointPolicy": {
      "allowHttp": false,
      "allowPrivateNetwork": true
    },
    "authenticationMode": "staticBearer",
    "credential": {
      "name": "contoso-registry-token",
      "scopeRef": "/instance",
      "namespace": "default"
    },
    "refreshPolicy": {
      "periodicEnabled": false,
      "interval": "1.00:00:00"
    },
    "cachePolicy": {
      "retainedObservations": 3
    }
  }
}
```

HTTPS and public addresses are the defaults. `allowHttp` and `allowPrivateNetwork` are independent, explicit opt-ins on one registration; redirects still cannot change origin, and link-local or multicast targets remain denied. The Secret value is resolved only for an exact registry request and is not returned by registry APIs or written to logs, errors, audit records, observations, or cache files.

Disabling or deleting a registration stops future discovery work without deleting Sources, Snapshots, installed Packs, immutable refresh history, or retained provenance. Periodic execution and bounded cache cleanup are delivered separately by the registry scheduling lifecycle.
