import { expect } from '@playwright/test';
import { Checkpoints } from '../contracts/checkpoints.js';
import { TestIds } from '../contracts/test-ids.js';
import type { Journey } from './journey.js';
import { prepareConsoleJourney } from './prepare-console.journey.js';

export interface ExerciseBootstrapProvenanceInput {
  username?: string;
  password?: string;
  packPublisher: string;
  packName: string;
  packVersion: string;
}

export const exerciseBootstrapProvenance: Journey<ExerciseBootstrapProvenanceInput> = async (context, input) => {
  await prepareConsoleJourney(context, input);
  // Apply a real, isolated Bootstrap fixture before checking provenance in the resource editors.
  const page = context.pages.page;
  const identityResponse = await page.request.get(`${context.consoleUrl}/api/identity/context`);
  expect(identityResponse.status(), await identityResponse.text()).toBe(200);
  const identity = await identityResponse.json() as { context: { tenantId: string; workspaceId: string } };
  const target = { tenantId: identity.context.tenantId, workspaceId: identity.context.workspaceId };
  const profiles = ['playwright-bootstrap-provenance'];
  const bindings = await Promise.all([
    { kind: 'modelProfile', binding: 'assistant-model', name: 'reasoning-default' },
    { kind: 'runtimeProfile', binding: 'assistant-runtime', name: 'maf-builtin' },
  ].map(async definition => {
    const response = await page.request.post(`${context.consoleUrl}/api/bootstrap/binding-targets`, {
      data: { target, targetKind: definition.kind, profiles },
    });
    expect(response.status(), await response.text()).toBe(200);
    const options = await response.json() as Array<{ name: string; namespace: string; scopeRef: string | null; planned: boolean }>;
    const matches = options.filter(option => option.name === definition.name
      && option.namespace === 'default' && option.scopeRef === '/instance' && !option.planned);
    expect(matches, `Authorized exact ${definition.kind} for Bootstrap provenance`).toHaveLength(1);
    const selection = matches[0]!;
    return { profile: profiles[0]!, name: definition.binding, target: { name: selection.name, namespace: selection.namespace, scopeRef: selection.scopeRef } };
  }));
  const selection = { profiles, target, bindings };
  const previewResponse = await page.request.post(`${context.consoleUrl}/api/bootstrap/profiles/preview`, { data: selection });
  expect(previewResponse.status(), await previewResponse.text()).toBe(200);
  const preview = await previewResponse.json() as { digest: string; canApply: boolean; resources: Array<{ kind: string; name: string }> };
  expect(preview.canApply).toBe(true);
  expect(preview.resources.some(resource => resource.kind === 'Entry' && resource.name === 'ask-agentstration')).toBe(true);
  const applicationResponse = await page.request.post(`${context.consoleUrl}/api/bootstrap/applications`, {
    data: { ...selection, expectedDigest: preview.digest },
  });
  expect(applicationResponse.status(), await applicationResponse.text()).toBe(201);
  const application = await applicationResponse.json() as { definition: { status: string; error: string | null; profiles: string[] } };
  expect(application.definition.status).toBe('succeeded');
  expect(application.definition.error).toBeNull();
  expect(application.definition.profiles).toEqual(profiles);
  const namespace = 'agentstration.assistant';

  await context.pages.entryEditor.open(context.consoleUrl, namespace, 'ask-agentstration');
  await context.pages.entryEditor.fillIdentity({ name: 'ask-agentstration', displayName: 'Ask Agentstration (edited)', description: 'Deterministic bootstrap entry' });
  await context.pages.entryEditor.saveDraft();
  await context.checkpoint({ name: Checkpoints.bootstrapProvenance.entryEditable, page: context.pages.page, target: context.pages.entryEditor.status });

  await context.pages.flowDesigner.open(context.consoleUrl, namespace, 'assistant-diagnostics');
  await context.pages.flowDesigner.validate();
  await context.pages.flowDesigner.save();
  await context.checkpoint({ name: Checkpoints.bootstrapProvenance.flowEditable, page: context.pages.page, target: context.pages.flowDesigner.designer });

  await context.pages.agentEditor.open(context.consoleUrl, namespace, 'assistant-diagnostics');
  await context.pages.agentEditor.fillIdentity({ name: 'assistant-diagnostics', displayName: 'Assistant diagnostics (edited)', description: 'Deterministic bootstrap agent' });
  await context.pages.agentEditor.saveDraft();
  await context.checkpoint({ name: Checkpoints.bootstrapProvenance.agentEditable, page: context.pages.page, target: context.pages.agentEditor.form });

  // Composer currently exports only default-namespace resources. The real Bootstrap
  // fixture provides a distinct default-namespace homonym for the Pack provenance check.
  const executionScope = `/workspaces/${target.workspaceId}`;
  const headers = { 'X-Agentstration-Context': executionScope };
  const packEntryResponse = await page.request.get(`${context.consoleUrl}/api/management/entries/ask-agentstration`, { headers });
  expect(packEntryResponse.status(), await packEntryResponse.text()).toBe(200);
  const packEntry = await packEntryResponse.json() as {
    value: { workspaceId: { value: string }; name: string; id: { value: string; namespace: string } };
    published: { workspaceId: { value: string }; name: string; id: { value: string; namespace: string } };
    managedByPack: boolean;
  };
  expect(packEntry.value.workspaceId.value).toBe(target.workspaceId);
  expect(packEntry.value.name).toBe('ask-agentstration');
  expect(packEntry.value.id).toEqual({ value: 'ask-agentstration', namespace: 'default' });
  expect(packEntry.published.workspaceId.value).toBe(target.workspaceId);
  expect(packEntry.published.name).toBe('ask-agentstration');
  expect(packEntry.published.id).toEqual({ value: 'ask-agentstration', namespace: 'default' });
  expect(packEntry.managedByPack).toBe(false);
  const catalogResponse = await page.request.get(`${context.consoleUrl}/api/pack-projects/composer/resources`, { headers });
  expect(catalogResponse.status(), await catalogResponse.text()).toBe(200);
  // Catalog keys have no ScopeRef field; Work Entry ownership is verified by its
  // exact WorkspaceId above and the selected execution context below.
  const catalog = await catalogResponse.json() as Array<{ resource: { kind: string; name: string; namespace: string }; availability: string }>;
  const packEntries = catalog.filter(item => item.resource.kind === 'Entry'
    && item.resource.name === 'ask-agentstration' && item.resource.namespace === 'default');
  expect(packEntries).toHaveLength(1);
  expect(packEntries[0]!.availability).toBe('Selectable');
  await expect(page.getByTestId(TestIds.console.shell)).toHaveAttribute('data-context-scope', executionScope);
  await context.pages.distribution.createPackProject(context.consoleUrl, {
    publisher: input.packPublisher,
    name: input.packName,
    version: input.packVersion,
    displayName: 'Bootstrap provenance fixture',
    resourceKind: 'Entry',
    resourceName: 'ask-agentstration',
    resourceNamespace: 'default',
  });
  await context.pages.distribution.buildPackProject();
  const packNamespace = await context.pages.distribution.installPackProject(bindings.map(binding => binding.target));
  await context.pages.entryEditor.open(context.consoleUrl, packNamespace, 'ask-agentstration');
  await expect(context.pages.entryEditor.definitionFields).toHaveAttribute('disabled', '');
  await context.checkpoint({ name: Checkpoints.bootstrapProvenance.packReadOnly, page: context.pages.page, target: context.pages.entryEditor.definitionFields });
};
