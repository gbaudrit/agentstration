# Reusing the Console value-binding editor

`ValueBindingRequirementEditor` in `Agentstration.Console.Components` edits one typed requirement. Its UI models live in `Agentstration.Web.Console` and do not reference Model Provider, Connected Service or Storage contracts. The Model Provider page is the reference consumer (#1051).

Supply:

- A `ValueBindingRequirement` with the invariant identifier, type/presentation metadata, required/protection state, description, optional format and optional allowed JSON values. Without `LiteralSchema`, literals use the scalar editor (`string`, `integer`, `number` or `boolean`). An optional `JsonElement? LiteralSchema` enables schema-backed literal editing. Format is displayed as requirement metadata; the consumer's normal server validation remains authoritative.
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

## Schema-backed literals

Supply the complete effective field schema in the optional final `ValueBindingRequirement.LiteralSchema` argument. The shared editor delegates to `JsonSchemaInputEditor` with `AllowAnyRoot = true` and forwards the requirement's `Required` and the editor's `Disabled` flags. There is no Tool or Model Provider schema contract dependency and no second validator in the binding component.

```csharp
var schema = JsonSerializer.Deserialize<JsonElement>("""
{
  "type": "object",
  "properties": {
    "tags": { "type": "array", "items": { "type": "string" }, "minItems": 1 },
    "attempts": { "type": "integer", "minimum": 1, "maximum": 5 },
    "note": { "type": ["string", "null"], "maxLength": 200 }
  },
  "required": ["tags"],
  "additionalProperties": false
}
""");

var requirement = new ValueBindingRequirement(
    "options", Required: true, ValueType: "object", LiteralSchema: schema);
var selection = new ValueBindingSelection(
    ValueBindingSourceKind.Literal,
    Literal: JsonSerializer.Deserialize<JsonElement>(
        """{"tags":["docs"],"attempts":2,"note":null}"""));
```

The scalar metadata remains useful for requirement presentation and compatibility filtering by the consumer. When `LiteralSchema` is supplied, that schema governs the Literal editor's shape and supported constraint validation; `AllowedValues`, when also supplied, remains an additional allowlist. Without a schema, object/array/null payloads still fail the scalar editor's validation. Model Providers supply no `LiteralSchema` and retain their scalar server contract.

### Presence, empty strings and null

The existing `JsonElement?` payload has three distinct states:

| UI state | `ValueBindingSelection.Literal` | `IsConfigured` |
| --- | --- | --- |
| Missing | CLR `null` (a default/Undefined element is also unconfigured) | `false` |
| Empty JSON string | A present `JsonElement` with `ValueKind.String` and `GetString() == ""` | `true` |
| Explicit JSON null | A present `JsonElement` with `ValueKind.Null` | `true` |

`IsConfigured` describes presence, not schema validity. A present null is accepted by the form only if its schema allows null. A present empty string is accepted only if its supported string/enum constraints permit it. A required binding rejects missing configuration; an optional missing binding remains valid.

The shared binding editor places the source selector above a full-width value editor. It uses `JsonSchemaInputEditor.Compact` with a localized `ValueLabel`: simple values are editable immediately, without an initialization step, repeated description or JSON terminology in form mode. Boolean values explicitly choose Yes/No without configuring an absent value as false. An allowed empty string can be chosen explicitly; **Clear value** removes configuration. Missing simple values show a neutral hint while still invalidating required bindings. Entered invalid values show the schema field's error once. **Advanced editing** opens raw JSON and **Back to value** returns to the field. Objects/arrays retain the Form/Raw controls and explicit initialization for empty containers; nullable unions retain explicit branch selection. Other consumers of `JsonSchemaInputEditor` keep their existing presentation by default.

Optional nested fields have separate clearing actions, so entering an empty string or selecting JSON null does not remove their keys. Raw mode edits the actual JSON value without an envelope: `""` is an empty string, `null` is JSON null, and clearing the raw input removes the value. Schema defaults do not silently configure an absent literal.

The schema-less scalar editor also preserves an explicitly entered empty string. Its **Clear literal** action removes the value. Source switching always clears both resource and literal payloads, independently of their previous kind.

### Reused JSON Schema capabilities and limits

`JsonSchemaInputEditor.AllowAnyRoot` is opt-in; its existing consumers keep the object-root behavior by default. The opt-in mode adds object, array, scalar and null roots and explicit presence handling. Its optional `Required` parameter defaults to `true`. `ValueChanged` emits a default/Undefined `JsonElement` when cleared; `ValidationChanged.Value` is CLR null for absence and a present null element for JSON null. The shared binding component maps those signals to the nullable literal payload above.

The existing bounded schema model/validator handles nested fields, enum, numeric bounds, lengths, patterns, URI format and array counts. In opt-in mode it also handles `type` arrays and standalone `anyOf`/`oneOf` alternatives composed from that supported subset, including nullable variants. `oneOf` requires exactly one matching alternative. Alternative titles can label the type picker. Schema constructs outside the supported subset, including unsupported combinations of applicators and sibling constraints, retain an explicit raw-JSON fallback; consumers must not treat client validation as full JSON Schema validation. Known constraints remain checked for parsed free-form arrays/objects even when a form cannot be generated.

Existing schema/value byte, nesting, field and rendered-item bounds remain in effect. Invalid raw JSON blocks enclosing form validation without replacing the last parsed literal; it must be corrected or cleared before saving. The consumer's server remains authoritative for all accepted values.

### Handoff to Tool relay consumers

The agent implementing #1044 can pass each input's effective schema as `LiteralSchema`, adapt the unchanged `JsonElement?` callback payload into its relay binding contract, and preserve CLR-null/JSON-null distinctions. ToolRelayEditor migration, relay persistence and runtime resolution are outside #1054. Keep `AllowedSources` explicit, preserve secured Secret-only requirements, and retain exact reference authorization/filtering in the consuming vertical. Deferred browser assertions are coordinated by #1055 with #1050 after UX validation and consumer adoption.

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
