# Agent resource

Current schema: `agentstration.io/v1`

```yaml
apiVersion: agentstration.io/v1
kind: Agent
metadata:
  name: sql-expert
  tags:
    domain: data
  annotations: {}
definition:
  displayName: SQL Expert
  description: Helps diagnose SQL queries.
  handler: prompt-agent
  instructions: |
    Answer as a read-only SQL specialist.
  modelProfile:
    name: reasoning-default
  tools:
    - name: sql-readonly
  behaviors: []
  middleware: []
  memory:
    readOwnMemory: true
    sharedScopes: []
    maximumRecords: 10
  settings: {}
```

On creation, the server adds an immutable GUID `uid`. The logical key is `Workspace + Agent + sql-expert`. Publishing produces an immutable `AgentRevision`; deployments reference revision and runtime-profile names.
