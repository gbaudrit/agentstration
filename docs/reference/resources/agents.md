# Agent resource

Type: `Agentstration.Agents/agents`  
Current schema: `2026-08-01`  
Canonical ID: `/resourceGroups/{resourceGroup}/providers/Agentstration.Agents/agents/{name}`

```yaml
type: Agentstration.Agents/agents
apiVersion: 2026-08-01
name: sql-expert
resourceGroup: default
location: local
tags:
  domain: database
properties:
  displayName: SQL Expert
  description: Specialized agent for database questions.
  agentType:
    resourceId: /resourceGroups/default/providers/Agentstration.Agents/agentTypes/readonly-expert
    version: 1
  additionalInstructions: |
    Focus on SQL Server.
  modelProfile:
    resourceId: /resourceGroups/default/providers/Agentstration.Models/modelProfiles/reasoning-default
```

## Properties

| Field | Required | Default/notes |
| --- | --- | --- |
| `displayName` | Yes | Human-readable name. |
| `description` | No | Optional description. |
| `agentType.resourceId` | Yes | Canonical Agent Type identifier. |
| `agentType.version` | No | Pins an Agent Type version when supplied. |
| `additionalInstructions` | No | Allowed and bounded by the referenced Agent Type policy. |
| `modelProfile.resourceId` | Yes | Canonical Model Profile identifier. |
| `tools` | No | Empty list by default; entries are resource references. |
| `settings` | No | Empty object by default; extension settings validated by the application boundary. |

`PUT` is idempotent: an identical functional declaration preserves generation, while a functional change increments it. Revision creation resolves the Agent Type and profiles into an immutable deployable definition. ETags protect concurrent writes, and referenced resources are validated by the Management service.

Automated YAML manifest import is **not implemented yet**; the YAML above documents the provider-neutral shape rather than a claim that every host currently accepts YAML uploads.
