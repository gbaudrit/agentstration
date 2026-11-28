# Resource and contribution access administration

The Console Access tab administers the displayed saved resource. Its **Allow all descendant scopes** switch grants discovery and use to valid current and future descendants of the exact owner. An Instance rule covers Tenants and their Workspaces; a Tenant rule covers its Workspaces. Workspace resources have no descendants and omit the switch. Turning it off removes only the general rule and preserves targeted permissions and explicit exceptions.

Targeted actions can allow discovery without use, deny either action, or propagate a Tenant rule to its Workspaces. A Workspace rule overrides Tenant propagation for the same action. These permissions permit consumption; ordinary RBAC still controls administration and definition changes.

## Related resources

Extensions, ToolSets and ToolProviders compose a separate related-resource panel after their own Access panel. Extension access makes a Tenant/Workspace eligible for contribution access; it does not automatically enable or authorize its contributions. Save the parent policy before administering its eligible descendants. A valid direct Instance-to-Workspace grant does not need a Tenant grant.

Use a Tenant switch to administer its branch and future Workspaces, or a Workspace switch to create an exception. Restore inheritance removes the collection's explicit scoped override. Partial counts and resource exceptions remain visible. A bulk grant preserves explicit denials configured independently on individual resources.

ToolSet member Tools inherit access from verified membership. An explicit denial on a Tool prevents all ToolSet-derived routes; removing that exception restores valid inheritance. Governed AEP drafts carry exact member UID/generation evidence. Operator sets use exact published membership. A consumer-created set cannot delegate foreign ancestor Tools. Access administration does not publish sets or expand pinned Agent/Flow assignments.

Revoking the source ExtensionRegistration makes affected contribution rules ineffective on subsequent discovery/use, including direct invocation. Saved contribution rules remain available for authorized reactivation. A parent discovery-only grant cannot permit contribution use.

## Managed Tools and inventory

Resources managed includes directly materialized and bundle-governed resources for the exact registration, including Flow resources from their separate store. Provider Tools are grouped by verified ToolSet membership, with an ungrouped section. Use individual, group or selection commands to change administrative activation. Managed definitions remain read-only.

Activation is stored separately from the definition. An administrator's choice survives refresh and restart without changing Tool identity, generation or published pins. A Tool can be authorized but disabled or unavailable; these are separate blockers. Bootstrap continues to report `contribution_disabled` for an effectively disabled Tool and never enables it implicitly to satisfy a prerequisite.

## API behavior

Existing exact-resource access policy routes retain their purpose under `/api/resource-access-policies/{resourceUid}`. Policies include `allDescendantActions`; targeted grants include disjoint `actions` and `deniedActions`. Collection-authored grants record `sourceResourceUid` for administrative provenance, which is not proof of ToolSet membership.

Related-resource inspection, evaluation and batch administration use the `/related` subresource of the exact policy route. Batch requests provide the reviewed membership token and individual policy ETags. Conflicts or preserved explicit denials appear in per-resource results. Authoritative membership is computed by family application services rather than accepted as caller-supplied authority.

Tool operational state is read separately through `/api/tools/{toolName}/activation` with exact `scopeRef` and `namespace`. The enablement command writes the operational overlay and uses that overlay's ETag; its Tool definition response remains unchanged. Provider administrative inventory is available through its `/administration` subresource. Group activation verifies the same exact reviewed collection and individual state ETags.

Development databases created with the older schema must be recreated. No pre-release compatibility or upgrade migration is supplied. See [ADR-0308](../decisions/0308-resource-access-composes-descendant-rules-and-verified-contributions.md).
