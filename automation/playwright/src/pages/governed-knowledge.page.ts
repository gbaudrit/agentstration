import { expect, type APIResponse, type Locator, type Page } from '@playwright/test';
import { TestIds } from '../contracts/test-ids.js';
import type { ExpectedText } from '../locales/expected-text.js';
import { AgentEditorPage } from './agent-editor.page.js';

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
}

export class GovernedKnowledgePage {
  private publishedHash?: string;
  public constructor(private readonly page: Page) {}
  public get publishedTools(): Locator { return this.page.getByTestId(TestIds.governedKnowledge.toolSetDetails); }
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
    const editor = new AgentEditorPage(this.page);
    await editor.openNew(consoleUrl);
    await editor.fillIdentity({ name: agentName, displayName: agentName, description: 'Offline governed Knowledge reader' });
    await editor.configureBehavior({ instructions: 'Use only the granted Knowledge search tool.',
      modelProfile: '/instance|default|execution-scope-reasoning', runtimeProfile: '/instance|default|maf-builtin' });
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
    return { toolSetName: exposure.toolSet.name, excluded, revisionUrl, revision: await responseRevision.json() };
  }

  public async broadenDraft(evidence: AgentPinEvidence, text: ExpectedText['governedKnowledge']): Promise<void> {
    const editor = new AgentEditorPage(this.page);
    await editor.openTools();
    const card = this.page.getByTestId(TestIds.agentEditor.toolSetConfiguration)
      .locator('article[data-toolset]').filter({ has: this.page.getByRole('heading', { name: evidence.toolSetName, exact: true }) });
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
}

async function status(response: APIResponse, expected: number): Promise<void> {
  expect(response.status(), await response.text()).toBe(expected);
}
