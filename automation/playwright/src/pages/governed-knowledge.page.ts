import { expect, type APIResponse, type Locator, type Page } from '@playwright/test';
import { TestIds } from '../contracts/test-ids.js';
import type { ExpectedText, SupportedTestLocale } from '../locales/expected-text.js';
import { AgentEditorPage } from './agent-editor.page.js';
import { ConsoleAdministrationPage } from './console-administration.page.js';
import { LoginPage } from './login.page.js';

interface Exposure {
  toolSet: { name: string };
  toolSetVersion: string;
  operations: { route: string; tool: { name: string } }[];
}

export interface AgentPinEvidence {
  toolSetName: string;
  excluded: string[];
  revisionUrl: string;
  revision: unknown;
  agentUrl: string;
  selected: string;
}

export class GovernedKnowledgePage {
  private publishedHash?: string;
  public constructor(private readonly page: Page) {}
  public get publishedTools(): Locator { return this.page.getByTestId(TestIds.governedKnowledge.toolSetDetails); }
  public get versions(): Locator { return this.page.getByTestId(TestIds.governedKnowledge.toolSetVersions); }
  public get pinnedAssignment(): Locator { return this.page.getByTestId(TestIds.governedKnowledge.activeAssignments); }

  public async publishExposure(consoleUrl: string, knowledge: string, scope: string, text: ExpectedText['governedKnowledge']): Promise<void> {
    const url = new URL(`/knowledge-sources/${knowledge}`, consoleUrl);
    url.search = new URLSearchParams({ namespace: 'default', context: scope, scopeRef: scope, view: 'tools' }).toString();
    await this.page.goto(url.toString());
    const exposurePanel = this.page.getByTestId(TestIds.governedKnowledge.exposure);
    await exposurePanel.getByRole('button', { name: text.publishExposure, exact: true }).click();
    await expect(exposurePanel.getByTestId(TestIds.governedKnowledge.toolLink)).toHaveCount(3);
    const response = await this.page.request.get(`${consoleUrl}/api/knowledgesources/${knowledge}/tool-exposure?scopeRef=${encodeURIComponent(scope)}`);
    await status(response, 200);
    const exposure = await response.json() as Exposure;
    expect(exposure.toolSetVersion).toBe('1.0.0');
    expect(exposure.operations).toHaveLength(3);
    await exposurePanel.getByRole('link', { name: text.openToolSet, exact: true }).click();
    await expect(this.publishedTools).toBeVisible();
    const version = this.page.getByTestId(TestIds.governedKnowledge.toolSetVersion);
    await expect(version).toHaveCount(1);
    this.publishedHash = await version.locator('code').first().innerText();
    expect(this.publishedHash).toMatch(/^[a-f0-9]{64}$/i);
    for (const operation of exposure.operations) {
      await expect(version).toContainText(operation.tool.name);
      await expect(version).toContainText(`knowledge.${operation.route}`);
    }
    await expect(this.page.getByText('agentstration.io/category=knowledge-source', { exact: true })).toBeVisible();
    await expect(this.page.getByText(`agentstration.io/knowledge-source=${knowledge}`, { exact: false })).toBeVisible();
    await this.page.goto(`${consoleUrl}/tools/sets?context=${encodeURIComponent(scope)}`);
    await expect(this.page.getByTestId(TestIds.resourceAdministration.toolSets)).toBeVisible();
    await this.page.getByTestId(TestIds.governedKnowledge.toolSetRow).filter({ hasText: exposure.toolSet.name }).getByRole('link').click();
    await expect(this.publishedTools).toBeVisible();
  }

  public async assignSubset(consoleUrl: string, knowledge: string, scope: string, text: ExpectedText['governedKnowledge']): Promise<AgentPinEvidence> {
    const response = await this.page.request.get(`${consoleUrl}/api/knowledgesources/${knowledge}/tool-exposure?scopeRef=${encodeURIComponent(scope)}`);
    await status(response, 200);
    const exposure = await response.json() as Exposure;
    // The materialized search member is chosen by its invariant capability route.
    const search = exposure.operations.find(item => item.route === 'search');
    expect(search).toBeDefined();
    const selected = search!.tool.name;
    const excluded = exposure.operations.filter(item => item.tool.name !== selected).map(item => item.tool.name);
    const agentName = `knowledge-reader-${Date.now().toString(36)}`;
    // Keep the shared execution-scope profile's usage graph unchanged for other journeys.
    const template = await this.page.request.get(`${consoleUrl}/api/modelprofiles/execution-scope-reasoning?scopeRef=${encodeURIComponent('/instance')}`);
    await status(template, 200);
    const model = await template.json() as { definition: Record<string, unknown> };
    const profileName = `${agentName}-model`;
    const profile = await this.page.request.post(`${consoleUrl}/api/modelprofiles`, {
      data: { name: profileName, namespace: 'default', scopeRef: scope,
        properties: { ...model.definition, displayName: profileName } },
    });
    await status(profile, 201);
    const editor = new AgentEditorPage(this.page);
    await editor.openNew(consoleUrl);
    await editor.fillIdentity({ name: agentName, displayName: agentName, description: 'Offline governed Knowledge reader' });
    await editor.configureBehavior({ instructions: 'Use only the granted Knowledge search tool.',
      modelProfile: `${scope}|default|${profileName}`, runtimeProfile: '/instance|default|maf-builtin' });
    await editor.openTools();
    const card = this.page.getByTestId(TestIds.agentEditor.toolSetConfiguration)
      .locator('article[data-toolset]').filter({ has: this.page.getByRole('heading', { name: exposure.toolSet.name, exact: true }) });
    await expect(card).toHaveCount(1);
    await expect(card).toContainText(text.pinnedHelp);
    await card.getByRole('checkbox').check();
    for (const member of excluded) await card.getByRole('checkbox', { name: member, exact: false }).uncheck();
    await expect(card.getByRole('checkbox', { name: selected, exact: false })).toBeChecked();
    await editor.createAndDeploy(agentName);
    const pinned = this.pinnedAssignment;
    await expect(pinned).toContainText(text.activeHelp);
    await expect(pinned).toContainText(exposure.toolSet.name);
    await expect(pinned).toContainText('1.0.0');
    expect(this.publishedHash).toBeTruthy();
    await expect(pinned).toContainText(this.publishedHash!);
    await expect(pinned).toContainText(selected);
    for (const member of excluded) await expect(pinned).not.toContainText(member);
    const evidence = await pinned.textContent();
    expect(evidence).toBeTruthy();
    await this.page.reload();
    await expect(pinned).toHaveText(evidence!);
    const revisionName = await pinned.locator('code').first().innerText();
    const revisionUrl = `${consoleUrl}/api/agents/${agentName}/revisions/${revisionName}?scopeRef=${encodeURIComponent(scope)}`;
    const responseRevision = await this.page.request.get(revisionUrl);
    await status(responseRevision, 200);
    const revision = await responseRevision.json() as { definition: { effectiveToolNames: string[] } };
    expect(revision.definition.effectiveToolNames).toEqual([selected]);
    await editor.openTools();
    await expect(this.page.getByTestId(TestIds.agentEditor.toolConfiguration).locator('input[type="checkbox"]:checked')).toHaveCount(1);
    return { toolSetName: exposure.toolSet.name, excluded, revisionUrl, revision, agentUrl: this.page.url(), selected };
  }

  public async broadenDraft(evidence: AgentPinEvidence, text: ExpectedText['governedKnowledge']): Promise<void> {
    const editor = new AgentEditorPage(this.page);
    await editor.openTools();
    const card = this.page.getByTestId(TestIds.agentEditor.toolSetConfiguration)
      .locator('article[data-toolset][data-version="1.0.0"]').filter({ has: this.page.getByRole('heading', { name: evidence.toolSetName, exact: true }) });
    for (const member of evidence.excluded) await card.getByRole('checkbox', { name: member, exact: false }).check();
    await editor.saveDraft();
    await expect(this.page.getByRole('status').filter({ hasText: text.draftSaved })).toBeVisible();
    await this.page.reload();
    await editor.openTools();
    for (const member of evidence.excluded) await expect(card.getByRole('checkbox', { name: member, exact: false })).toBeChecked();
    const revision = await this.page.request.get(evidence.revisionUrl);
    await status(revision, 200);
    expect(await revision.json()).toEqual(evidence.revision);
  }

  public async exerciseVersionGovernance(consoleUrl: string, knowledge: string, scope: string,
    evidence: AgentPinEvidence, text: ExpectedText['governedKnowledge']): Promise<void> {
    const path = `/api/toolsets/${evidence.toolSetName}`;
    const originalVersion = await (await this.get(consoleUrl, `${path}/versions/1.0.0`, scope)).json() as unknown;
    const current = await this.get(consoleUrl, path, scope);
    const toolSet = await current.json() as { definition: { members: unknown[] } & Record<string, unknown> };
    const definition = await (await this.get(consoleUrl, `/api/tooldefinitions/${knowledge}.search`, scope)).json() as { definition: Record<string, unknown> };
    const additionalName = `${knowledge}.additional-search`;
    await this.post(consoleUrl, '/api/tooldefinitions', scope, {
      name: additionalName, namespace: 'default', properties: { ...definition.definition, displayName: additionalName, publish: true },
    }, 201);
    const additionalTool = `agentstration.${additionalName}.v${String(definition.definition.version)}`;
    const updated = await this.page.request.put(this.url(consoleUrl, path, scope), {
      headers: { 'X-Agentstration-Context': scope, 'If-Match': current.headers()['etag']! },
      data: { properties: { ...toolSet.definition, version: '2.0.0', publish: true,
        members: [...toolSet.definition.members, { tool: { name: additionalTool, namespace: 'default', scopeRef: scope },
          capability: 'knowledge.search', route: 'additional-search' }] } },
    });
    await status(updated, 200);
    const latest = await (await this.get(consoleUrl, `${path}/versions/2.0.0`, scope)).json() as { members: { toolName: string }[] };
    expect(latest.members.map(member => member.toolName)).toContain(additionalTool);
    expect(latest.members).toHaveLength(4);
    expect(await (await this.get(consoleUrl, `${path}/versions/1.0.0`, scope)).json()).toEqual(originalVersion);

    await this.openToolSet(consoleUrl, evidence.toolSetName, scope);
    const versions = this.page.getByTestId(TestIds.governedKnowledge.toolSetVersion);
    await expect(versions).toHaveCount(2);
    await expect(versions.filter({ has: this.page.getByRole('heading', { level: 3, name: /\b2\.0\.0$/ }) })).toContainText(additionalTool);
    await expect(versions.filter({ has: this.page.getByRole('heading', { level: 3, name: /\b1\.0\.0$/ }) })).not.toContainText(additionalTool);
    const beforeDelete = await (await this.get(consoleUrl, path, scope)).json() as unknown;
    await this.publishedTools.getByRole('button', { name: text.deleteToolSet, exact: true }).click();
    await this.page.getByRole('dialog').getByRole('button', { name: text.confirmToolSetDelete, exact: true }).click();
    await expect(this.page.getByRole('alert')).toContainText(evidence.toolSetName);
    await expect(this.page.getByRole('dialog')).toHaveCount(0);
    expect(await (await this.get(consoleUrl, path, scope)).json()).toEqual(beforeDelete);
    const blocked = await this.page.request.delete(this.url(consoleUrl, path, scope), {
      headers: { 'X-Agentstration-Context': scope, 'If-Match': updated.headers()['etag']! },
    });
    await status(blocked, 422);
    expect((await blocked.json() as { type: string }).type).toContain('tool_set_in_use_by_agent');

    await this.page.goto(evidence.agentUrl);
    await expect(this.pinnedAssignment).toContainText('1.0.0');
    await expect(this.pinnedAssignment).toContainText(this.publishedHash!);
    await expect(this.pinnedAssignment).toContainText(evidence.selected);
    await expect(this.pinnedAssignment).not.toContainText(additionalTool);
    expect(await (await this.get(consoleUrl, new URL(evidence.revisionUrl).pathname, scope)).json()).toEqual(evidence.revision);
    const editor = new AgentEditorPage(this.page);
    await editor.openTools();
    const laterCard = this.page.getByTestId(TestIds.agentEditor.toolSetConfiguration)
      .locator('article[data-version="2.0.0"]').filter({ has: this.page.getByRole('heading', { name: evidence.toolSetName, exact: true }) });
    await expect(laterCard.getByRole('checkbox')).not.toBeChecked();
    await expect(this.page.getByTestId(TestIds.agentEditor.toolConfiguration).locator('input[type="checkbox"]:checked')).toHaveCount(1);
  }

  public async exerciseExposureFailures(consoleUrl: string, knowledge: string, scope: string,
    text: ExpectedText['governedKnowledge'], locale: SupportedTestLocale): Promise<void> {
    const source = await (await this.get(consoleUrl, `/api/knowledgesources/${knowledge}`, scope)).json() as { definition: Record<string, unknown> };
    const createSource = async (name: string, enabled: boolean) => this.post(consoleUrl, '/api/knowledgesources', scope, {
      name, namespace: 'default', scopeRef: scope, properties: { ...source.definition, displayName: name, enabled },
    }, 201);
    const unavailable = `${knowledge}-not-ready`;
    await createSource(unavailable, false);
    await this.openTools(consoleUrl, unavailable, scope);
    const exposure = this.page.getByTestId(TestIds.governedKnowledge.exposure);
    await expect(exposure.getByRole('button', { name: text.publishExposure, exact: true })).toBeDisabled();
    await expect(exposure.locator('.inline-warning')).toBeVisible();
    const rejected = await this.post(consoleUrl, `/api/knowledgesources/${unavailable}/tool-exposure`, scope, { version: '1.0.0' }, 422);
    expect((await rejected.json() as { type: string }).type).toContain('knowledge_source_not_ready');

    const conflict = `${knowledge}-conflict`;
    await createSource(conflict, true);
    const current = await (await this.get(consoleUrl, `/api/toolsets/${knowledge}`, scope)).json() as { definition: Record<string, unknown> };
    // A foreign unpublished ToolSet deliberately owns this otherwise-ready source's name.
    await this.post(consoleUrl, '/api/toolsets', scope, {
      name: conflict, namespace: 'default', properties: { ...current.definition, displayName: conflict, version: '1.0.0', publish: false },
    }, 201);
    const foreign = await (await this.get(consoleUrl, `/api/toolsets/${conflict}`, scope)).json() as unknown;
    await this.openTools(consoleUrl, conflict, scope);
    await exposure.getByRole('button', { name: text.publishExposure, exact: true }).click();
    await expect(this.page.locator('.form-alert-danger[role="status"]')).toContainText(conflict);
    const conflicting = await this.post(consoleUrl, `/api/knowledgesources/${conflict}/tool-exposure`, scope, { version: '1.0.0' }, 422);
    expect((await conflicting.json() as { type: string }).type).toContain('knowledge_source_tool_set_conflict');
    expect(await (await this.get(consoleUrl, `/api/toolsets/${conflict}`, scope)).json()).toEqual(foreign);

    const restricted = `${knowledge}-restricted`;
    await createSource(restricted, true);
    const userName = `toolset-viewer-${Date.now().toString(36)}`;
    const password = 'ToolSet-Viewer-640!'; // Public, disposable local browser fixture.
    await this.post(consoleUrl, '/api/identity/accounts', scope, {
      userName, password, displayName: 'ToolSet browser viewer', email: null, workspaceId: scope.split('/')[2], role: 'Viewer',
    }, 201);
    const browser = this.page.context().browser();
    if (!browser) throw new Error('A browser is required for the ToolSet authorization fixture.');
    const readerContext = await browser.newContext({ viewport: this.page.viewportSize() ?? { width: 1440, height: 1000 }, locale, colorScheme: 'dark' });
    try {
      const reader = await readerContext.newPage();
      await new LoginPage(reader).signIn(consoleUrl, userName, password);
      await new ConsoleAdministrationPage(reader).restoreLanguage(consoleUrl, locale);
      await reader.goto(this.toolsUrl(consoleUrl, restricted, scope));
      await reader.getByTestId(TestIds.governedKnowledge.exposure).getByRole('button', { name: text.publishExposure, exact: true }).click();
      await expect(reader.locator('.form-alert-danger[role="status"]')).toContainText(/403|Forbidden/i);
      await status(await reader.request.post(this.url(consoleUrl, `/api/knowledgesources/${restricted}/tool-exposure`, scope), {
        data: { version: '1.0.0' },
      }), 403);
      await reader.goto(this.toolSetUrl(consoleUrl, knowledge, scope));
      await reader.getByTestId(TestIds.governedKnowledge.toolSetDetails).getByRole('button', { name: text.deleteToolSet, exact: true }).click();
      await reader.getByRole('dialog').getByRole('button', { name: text.confirmToolSetDelete, exact: true }).click();
      await expect(reader.getByRole('alert')).toContainText(/403|Forbidden/i);
      await status(await reader.request.delete(this.url(consoleUrl, `/api/toolsets/${knowledge}`, scope)), 403);
      await expect(reader.locator('#blazor-error-ui')).not.toBeVisible();
    } finally { await readerContext.close(); }
    expect(await (await this.get(consoleUrl, `/api/toolsets/${knowledge}`, scope)).json()).toEqual(current);
    const noExposure = await this.page.request.get(this.url(consoleUrl, `/api/knowledgesources/${restricted}/tool-exposure`, scope));
    await status(noExposure, 422);
    expect((await noExposure.json() as { type: string }).type).toContain('knowledge_source_tool_exposure_not_found');
    await this.openToolSet(consoleUrl, knowledge, scope);
  }

  private url(consoleUrl: string, path: string, scope: string): string {
    const url = new URL(path, consoleUrl);
    url.search = new URLSearchParams({ namespace: 'default', resourceNamespace: 'default', scopeRef: scope }).toString();
    return url.toString();
  }
  private toolsUrl(consoleUrl: string, knowledge: string, scope: string): string {
    const url = new URL(`/knowledge-sources/${knowledge}`, consoleUrl);
    url.search = new URLSearchParams({ namespace: 'default', context: scope, scopeRef: scope, view: 'tools' }).toString();
    return url.toString();
  }
  private toolSetUrl(consoleUrl: string, name: string, scope: string): string {
    return `${consoleUrl}/tools/sets/${name}?context=${encodeURIComponent(scope)}`;
  }
  private async openToolSet(consoleUrl: string, name: string, scope: string): Promise<void> {
    await this.page.goto(this.toolSetUrl(consoleUrl, name, scope));
    await expect(this.publishedTools).toBeVisible();
  }
  private async openTools(consoleUrl: string, knowledge: string, scope: string): Promise<void> {
    await this.page.goto(this.toolsUrl(consoleUrl, knowledge, scope));
    await expect(this.page.getByTestId(TestIds.governedKnowledge.exposure)).toBeVisible();
  }
  private async get(consoleUrl: string, path: string, scope: string): Promise<APIResponse> {
    const response = await this.page.request.get(this.url(consoleUrl, path, scope), { headers: { 'X-Agentstration-Context': scope } });
    await status(response, 200);
    return response;
  }
  private async post(consoleUrl: string, path: string, scope: string, data: object, expected: number): Promise<APIResponse> {
    const response = await this.page.request.post(this.url(consoleUrl, path, scope), { headers: { 'X-Agentstration-Context': scope }, data });
    await status(response, expected);
    return response;
  }

}

async function status(response: APIResponse, expected: number): Promise<void> {
  expect(response.status(), await response.text()).toBe(expected);
}
