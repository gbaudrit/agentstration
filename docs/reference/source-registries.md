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
      "periodicEnabled": true,
      "interval": "1.00:00:00",
      "timeout": "00:00:30",
      "maximumAttempts": 3,
      "initialBackoff": "00:00:30",
      "maximumBackoff": "00:15:00",
      "jitter": "00:00:15",
      "staleAfter": "2.00:00:00"
    },
    "cachePolicy": {
      "retainedObservations": 3
    }
  }
}
```

HTTPS and public addresses are the defaults. `allowHttp` and `allowPrivateNetwork` are independent, explicit opt-ins on one registration; redirects still cannot change origin, and link-local or multicast targets remain denied. The Secret value is resolved only for an exact registry request and is not returned by registry APIs or written to logs, errors, audit records, observations, or cache files.

## Refresh lifecycle

Periodic refresh is disabled by default, so a fresh local installation remains offline. When `periodicEnabled` is true, the existing local Source refresh worker also evaluates Registry registrations every 30 seconds. It uses the persisted last attempt and consecutive failure count, so interval and retry decisions survive restart. Manual and scheduled requests enter the same application service and are serialized; a scheduled scan revalidates its observed state after acquiring the lock so it cannot publish over a newer manual result.

Each scheduled attempt has a finite timeout. Failures retain the last-known-good observation and use bounded exponential backoff up to `maximumAttempts`; after that window, the normal interval applies again. Jitter is deterministic for a registration UID. A successful attempt after one or more failures reports `recovered`, resets the failure count, and records the recovery time. A usable observation becomes `stale` when `staleAfter` elapses without a successful validation.

Index requests reuse the cached `ETag` and `Last-Modified` validators. HTTP 304 is a successful validation of the existing observation. Refresh history records the registration UID, manual or scheduled trigger, outcome, duration, retry count, correlation ID, digest, and error code without storing credentials or response payloads.

Successful publications retain the configured number of complete cache observations. Cleanup never removes the current observation and does not delete immutable refresh history. Deleting a registration preserves its existing cache and history for provenance; disabling it prevents future scheduled work. Source and Channel refresh/materialization are separate lifecycles.
