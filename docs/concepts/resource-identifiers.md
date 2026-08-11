# Resource identifiers

Canonical Management resource identifiers follow this implemented shape:

```text
/resourceGroups/{resourceGroup}/providers/{providerNamespace}/{resourceType}/{name}
```

For example:

```text
/resourceGroups/default/providers/Agentstration.Agents/agents/sql-expert
```

Identifiers are stable references. The resource schema version (`apiVersion`) and the revision/generation of a particular resource instance are separate concepts.
