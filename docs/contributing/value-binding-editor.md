# Reusing the Console value-binding editor

`ValueBindingRequirementEditor` in `Agentstration.Console.Components` edits one typed requirement. Its UI models live in `Agentstration.Web.Console` and do not reference Model Provider, Connected Service or Storage contracts. The Model Provider page is the reference consumer (#1051).

Supply:

- A `ValueBindingRequirement` with the invariant identifier and scalar type (`string`, `integer`, `number` or `boolean`), required/protection state, description, optional format and optional allowed JSON values. Format is displayed as requirement metadata; the consumer's normal server validation remains authoritative.
- Explicit `AllowedSources`: `Parameter`, `Secret`, `Literal` and/or `Caller`. Include `Caller` only when the consumer supports values supplied at invocation time; omit it to disable that choice. Secured requirements always allow only Secrets, even if the consumer includes other sources. Consumers may impose narrower restrictions; Model Providers do not enable `Caller`, and their requirements with allowed values exclude Secrets, as required by their server contract.
- Compatible `ValueBindingOption` entries containing a source kind, display name and exact `ScopedResourceAddress`. The consumer filters by type, allowed values, scope, authorization and use policy. The component never retrieves resource inventories or resolves Secret values.
- The current `ValueBindingSelection` and a `SelectionChanged` callback. Resource selections preserve scope, namespace and name; literal selections carry typed JSON with no resource reference. Missing persisted references remain visible as unavailable rather than being replaced. Switching source clears the previous payload. Consumers convert the selection to their own persisted contract.
- An optional `ContextualValueCreationContext` with the exact consumer scope, namespace and suggested resource identity. The component projects the requirement's type, format and allowed values into the existing Parameter/Secret creator. Omit the context to disable contextual creation. The current creators support the default namespace only.
- An optional `ResourceCreated` callback to refresh the consumer's compatible options after creation. `SelectionChanged` first returns the newly created exact reference. No Secret value is included in either callback.

When the editor is inside an `EditForm`, supply a unique, stable `DialogSectionId` object and place a matching Blazor `SectionOutlet` **outside** the form. This keeps contextual creator forms outside the consumer form, while the editor retains responsibility for their lifecycle. Standalone editors can omit the section ID and render dialogs inline.

```razor
<EditForm Model="model" OnValidSubmit="SaveAsync">
    <ValueBindingRequirementEditor Requirement="requirement"
        AllowedSources="sources" Options="options"
        Selection="selection" SelectionChanged="SelectValue"
        CreationContext="creationContext" ResourceCreated="ReloadOptionsAsync"
        DialogSectionId="dialogSection" Disabled="readOnly" />
</EditForm>
<Microsoft.AspNetCore.Components.Sections.SectionOutlet SectionId="dialogSection" />
```

Missing required selections, disallowed sources and invalid literal types/allowed values participate in the enclosing `EditContext` validation. Boolean literals use a localized selector, while technical allowed values retain their original representation. Callers must still validate commands on the server; UI filtering is not an authorization boundary. Disable the editor for read-only consumers.

`Caller` displays **Provided by caller** (French: **Fourni par l’appelant**) without a value field, resource selector or creation action. Selecting it emits `new ValueBindingSelection(ValueBindingSourceKind.Caller)` with no resource or literal payload and counts as a configured binding, including for required requirements. The consuming editor owns persistence, invocation-time resolution and validation of the eventual value; the shared component does not implement those behaviors.

The consumer can toggle this choice by adding or removing `ValueBindingSourceKind.Caller` in `AllowedSources`. Removing it while a Caller binding is selected preserves that selection as unavailable and invalidates the enclosing form; it does not silently replace it. `Disabled` independently makes the entire editor read-only.

Component-owned presentation is localized through the existing `ContextualValueCreatorStrings` catalog in English and French. `TestIdPrefix` defaults to `value-binding`; existing consumers may supply a prefix to preserve browser selector contracts.

Literals are ordinary visible configuration persisted with the consumer resource. Use a Secret for protected values. Model Provider literals use the existing AEP inline bound-value transport at discovery and execution, with scalar type, allowed-value, protection and payload-size validation on the server. No resource or schema migration is required for existing Parameter/Secret bindings.

For example, a Model Provider definition can contain the following binding, without a Parameter resource:

```json
{
  "valueBindings": [
    { "requirementId": "projectEndpoint", "kind": "literal", "literal": "https://foundry.example.test/api/projects/example" }
  ]
}
```

Use JSON numbers or booleans for requirements of those types; a quoted number or boolean is a string and fails type validation.
