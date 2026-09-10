# Source registry registrations

Source registries are instance-owned discovery endpoints administered by Platform administrators. They are separate from the static `SourceRegistryIndex`/`SourceRegistry` publication and from imported Sources.

The API supports:

- `GET /api/sourceregistries` and `GET /api/sourceregistries/{name}`;
- `POST /api/sourceregistries` and ETag-protected `PUT`/`DELETE` operations;
- `POST /api/sourceregistries/{name}/refresh` and refresh history.
- `GET /api/sourceregistries/{name}/trust` for current origin classification and local policy;
- `GET /api/sourceregistries/trust/sources/{publisher}/{source}/versions/{version}?manifestDigest=sha256:...` for publisher and exact-version evidence.
- `GET /api/sourceregistries/discovery` with bounded filters and paging, plus `/discovery/publishers` and `/discovery/sources/{publisher}/{source}`;
- `POST /api/sourceregistries/discovery/imports` to import one retained observation exactly.

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

## Four independent evidence dimensions

Registry trust is not one boolean:

1. **Origin classification** describes where an observation came from. The exact persisted built-in `agentstration-official` registration is classified as official by local identity. Boundary-safe HTTPS matching for `agentstration.io` and its subdomains is informational only; it never grants trust, publisher status, or byte verification. External and private/internal origins remain explicit.
2. **Publisher status** is asserted by each shard as `Declared`, `Verified`, `Official`, or `Revoked`, then gated by the local registration policy. An untrusted registration contributes only declared evidence; trusted registrations may verify an editor; only an authoritative registration may contribute official status. An accepted revocation dominates other assertions.
3. **SourceVersion verification** still requires an exact publisher, Source name, opaque version, and canonical manifest digest match through the existing Source verification service. Equal observations retain all provenance. Different accepted digests for one identity/version produce a blocking conflict.
4. **Snapshot verification** independently requires the exact Channel, resolved revision, and materialized archive digest. A verified SourceVersion never verifies current or future mutable Channel content.

Trust policy changes are ETag-protected Platform-administrator mutations and are audited with the existing registration configuration action. Evaluation is recomputed from current policy, so downgrade, disablement, removal, or revocation changes the current answer without modifying the cached observation or historical refresh record. Evidence responses contain registration and observation identifiers, canonical index/catalog digests, asserted and accepted states, reason codes, and evaluation time; they never contain credentials or cached documents.

## Merged discovery and exact import

`GET /api/sourceregistries/discovery` reads cached shards and performs no network request. Results are grouped by `publisher/name`, then by the opaque Source version. Equal identity/version/digest observations remain individually visible with their registration, observation, index, shard, freshness, compatibility, trust policy, and origin evidence. Different digests are reported as a conflict. `latest` is an `isCatalogLatest` flag on each observation; Agentstration never invents a global latest version or semantically orders opaque Source versions.

The query accepts `search`, `publisher`, `registry`, `compatibleOnly`, `freshOnly`, `conflictsOnly`, `trustPolicy`, `publisherStatus`, `verificationStatus`, `skip`, and `take`. `take` is limited to 100. Publisher summaries and an exact Source detail are available from the two discovery subroutes above.

The import request repeats the `selection` returned by discovery and may supply the normal Source `scopeRef`:

```json
{
  "selection": {
    "registrationUid": "00000000-0000-0000-0000-000000000000",
    "observationId": "00000000-0000-0000-0000-000000000000",
    "catalogName": "agentstration-0.2",
    "publisher": "agentstration",
    "sourceName": "bootstrap-samples",
    "version": "1"
  },
  "scopeRef": "/instance"
}
```

The server resolves the retained tuple again. It rejects missing or evicted observations, disabled or untrusted registrations, revoked publishers, and digest conflicts. Only then does it retrieve the selected manifest with the registration's same-origin network and credential policy. The parsed publisher/name/version and canonical digest must match the shard. A successful import uses the regular immutable Source lifecycle and is idempotent; it retrieves no other manifest and materializes no Channel.

Imported Source Versions retain the complete Registry observation and trust snapshot. A normal Source URL refresh is intentionally refused for that origin, because moving it outside the exact-observation workflow would silently change provenance. A later Registry definition is imported by selecting another observation. Packs installed from a resulting Snapshot also retain this Registry provenance while continuing to validate their independent provider, revision, Snapshot, catalog, and archive evidence.
