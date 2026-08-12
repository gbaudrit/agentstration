# Resource identifiers

Management resources have two distinct identities:

```text
stable identity: uid (server-generated GUID)
logical identity: Workspace + Kind + metadata.name
```

References are readable and structured:

```yaml
modelProfile:
  name: reasoning-default
# workspaceRef is optional; omission means the current workspace.
```
