/** Ordinary controls in each editor; specialized adapters have separate cases. */
export const SharedFormCases = [
  { id: 'agent', path: '/agents/new', source: 'AgentEditor.razor' },
  { id: 'model-profile', path: '/modelprofiles/new', source: 'ModelProfileEditor.razor' },
  { id: 'runtime-profile', path: '/runtimeprofiles/new', source: 'RuntimeProfileEditor.razor' },
  { id: 'trigger', path: '/triggers/new', source: 'TriggerEditor.razor' },
  { id: 'parameter', path: '/parameters/new', source: 'ParameterEditor.razor' },
  { id: 'secret', path: '/secrets/new', source: 'SecretEditor.razor' },
  { id: 'vault', path: '/vaults/new', source: 'VaultEditor.razor' },
  { id: 'model-provider', path: '/modelproviders/new', source: 'ModelProviderDetails.razor' },
  { id: 'source-provider', path: '/sourceproviders/new', source: 'SourceProviderDetails.razor' },
  { id: 'tool-provider', path: '/tools/providers/new', source: 'ToolProviderEditor.razor' },
  { id: 'tool-category', path: '/tools/categories/new', source: 'ToolCategoryEditor.razor' },
  { id: 'tool-definition', path: '/tools/definitions/new', source: 'ToolDefinitionEditor.razor' },
  { id: 'data-source', path: '/data-sources/new', source: 'DataSourceEditor.razor' },
  { id: 'data-source-profile', path: '/data-source-profiles/new', source: 'DataSourceProfileEditor.razor' },
  { id: 'knowledge-source', path: '/knowledge-sources/new', source: 'KnowledgeSourceEditor.razor' },
  { id: 'entry', path: '/entries/new?view=definition', source: 'EntryEditor.razor' },
  { id: 'schema', path: '/schemas/new', source: 'SchemaEditor.razor' },
  { id: 'source-registry', path: '/settings/source-registries/new', source: 'SourceRegistries.razor' },
  { id: 'pack-composer', path: '/pack-projects/new', source: 'PackComposer.razor' },
] as const;
export type SharedFormCase = typeof SharedFormCases[number];
export type FormAppearance = 'light' | 'dark';
