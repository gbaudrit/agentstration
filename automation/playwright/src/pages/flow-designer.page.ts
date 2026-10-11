import { expect, type Locator, type Page } from '@playwright/test';
import { TestIds } from '../contracts/test-ids.js';

export interface NamedOutputCompositionFixture {
  childName: string;
  parentName: string;
}

export interface NamedOutputAuthoring {
  name: string;
  displayName: string;
  outcome: 'Success' | 'Error';
  mapping: string;
  schema: string;
  code?: string;
  message?: string;
  details?: string;
}

export class FlowDesignerPage {
  public constructor(private readonly page: Page) {}

  public async createDraftAndOpen(consoleUrl: string): Promise<string> {
    const name = `designer-links-smoke-${Date.now()}`;
    const response = await this.page.request.post(`${consoleUrl}/api/flows/drafts`, {
      data: { name, displayName: 'Designer links smoke', template: 'Empty' },
    });
    expect(response.status(), await response.text()).toBe(201);
    await this.open(consoleUrl, 'default', name);
    return name;
  }

  public async createPositionedDraftAndOpen(consoleUrl: string): Promise<string> {
    const name = `designer-links-smoke-${Date.now()}`;
    const response = await this.page.request.post(`${consoleUrl}/api/flows/drafts`, {
      data: { name, displayName: 'Transition editing UX', template: 'Empty' },
    });
    const responseBody = await response.text();
    expect(response.status(), responseBody).toBe(201);
    const created = JSON.parse(responseBody);
    const draft = created.value;
    draft.definition.steps.push({ type: 'transform', name: 'transform', displayName: 'Transform', mapping: {} });
    draft.definition.transitions = [];
    draft.definition.designer = {
      preferredLayout: 'Horizontal',
      nodePositions: {
        input: { x: 80, y: 220 },
        transform: { x: 400, y: 220 },
        completed: { x: 720, y: 100 },
        error: { x: 720, y: 380 },
      },
    };
    const update = await this.page.request.put(`${consoleUrl}/api/flows/${encodeURIComponent(name)}/draft`, {
      headers: { 'If-Match': created.eTag },
      data: {
        displayName: draft.displayName,
        description: draft.description,
        tags: draft.tags,
        definition: draft.definition,
      },
    });
    expect(update.status(), await update.text()).toBe(200);
    await this.open(consoleUrl, 'default', name);
    await expect(this.node('transform')).toBeVisible();
    await expect(async () => {
      await this.node('transform').click();
      await expect(this.page.getByRole('textbox', { name: 'Step ID' })).toHaveValue('transform', { timeout: 2_000 });
    }).toPass({ timeout: 20_000 });
    return name;
  }

  public async createNamedOutputCompositionAndOpen(consoleUrl: string): Promise<NamedOutputCompositionFixture> {
    const suffix = Date.now();
    // Keep fixtures at the start of the sorted catalog so the Designer's bounded resource picker sees them
    // even when the Development bootstrap contains a large Flow catalog.
    const childName = `aaa-named-output-child-${suffix}`;
    const parentName = `aaa-named-output-parent-${suffix}`;

    const child = await this.createDraft(consoleUrl, childName, 'Named output child');
    const legacyDefinition = {
      entryStep: 'input',
      steps: [
        { type: 'input', name: 'input', displayName: 'Input' },
        {
          type: 'output', name: 'historical-result', displayName: 'Historical result',
          outputMapping: '${transition.output}',
          schema: { type: 'object', properties: { legacyValue: { type: 'string' } } },
        },
        { type: 'failure', name: 'legacy-failure', displayName: 'Legacy failure', code: 'LEGACY_FAILURE', message: 'Legacy failure.' },
      ],
      transitions: [{ id: 'input-completed-historical', fromStep: 'input', event: 'completed', toStep: 'historical-result' }],
      designer: { preferredLayout: 'Horizontal', nodePositions: {
        input: { x: 80, y: 220 }, 'historical-result': { x: 500, y: 120 }, 'legacy-failure': { x: 500, y: 360 },
      } },
    };
    let childEtag = await this.updateDraft(consoleUrl, childName, child, legacyDefinition);
    await this.publishDraft(consoleUrl, childName, '1.0.0', false);

    const modernDefinition = {
      entryStep: 'input',
      inputSchema: { type: 'object', properties: { approved: { type: 'boolean' } }, required: ['approved'] },
      steps: [
        { type: 'input', name: 'input', displayName: 'Input', schema: { type: 'object', properties: { approved: { type: 'boolean' } }, required: ['approved'] } },
        { type: 'condition', name: 'decision', displayName: 'Decision', mode: 'Simple', left: '${input.approved}', operator: 'equals', right: 'true' },
        {
          type: 'output', name: 'approved', displayName: 'Approved', outcome: 'success', outputMapping: '${transition.output}',
          schema: { type: 'object', properties: { decision: { type: 'string' } }, required: ['decision'] },
        },
        {
          type: 'output', name: 'rejected', displayName: 'Rejected', outcome: 'success', outputMapping: '${transition.output}',
          schema: { type: 'object', properties: { reason: { type: 'string' } }, required: ['reason'] },
        },
        {
          type: 'output', name: 'provider-error', displayName: 'Provider error', outcome: 'error', code: 'PROVIDER_ERROR', message: 'Provider failed.',
          schema: { type: 'object', properties: { retryable: { type: 'boolean' } }, required: ['retryable'] },
        },
      ],
      transitions: [
        { id: 'input-completed-decision', fromStep: 'input', event: 'completed', toStep: 'decision' },
        { id: 'decision-true-approved', fromStep: 'decision', event: 'true', toStep: 'approved' },
        { id: 'decision-false-rejected', fromStep: 'decision', event: 'false', toStep: 'rejected' },
      ],
      designer: { preferredLayout: 'Horizontal', nodePositions: {
        input: { x: 60, y: 240 }, decision: { x: 360, y: 240 }, approved: { x: 700, y: 60 },
        rejected: { x: 700, y: 260 }, 'provider-error': { x: 700, y: 460 },
      } },
    };
    const childDraft = await this.getDraft(consoleUrl, childName);
    childEtag = childDraft.eTag ?? childEtag;
    await this.updateDraft(consoleUrl, childName, { ...childDraft, eTag: childEtag }, modernDefinition);
    await this.publishDraft(consoleUrl, childName, '2.0.0', true);

    const parent = await this.createDraft(consoleUrl, parentName, 'Named output parent');
    const parentDefinition = {
      entryStep: 'input',
      steps: [
        { type: 'input', name: 'input', displayName: 'Input' },
        { type: 'flow', name: 'review', displayName: 'Child review', flow: { resourceId: childName, versionStrategy: 'active' }, inputMapping: '${transition.output}' },
      ],
      transitions: [{ id: 'input-completed-review', fromStep: 'input', event: 'completed', toStep: 'review' }],
      designer: { preferredLayout: 'Horizontal', nodePositions: { input: { x: 60, y: 260 }, review: { x: 420, y: 260 } } },
    };
    await this.updateDraft(consoleUrl, parentName, parent, parentDefinition);
    await this.open(consoleUrl, 'default', parentName);
    await expect(this.node('Child review')).toBeVisible();
    return { childName, parentName };
  }

  public async expectPublishedOutputContracts(consoleUrl: string, childName: string): Promise<void> {
    const response = await this.page.request.get(`${consoleUrl}/api/flows/${encodeURIComponent(childName)}/versions/2.0.0`);
    const body = await response.text();
    expect(response.status(), body).toBe(200);
    const published = JSON.parse(body);
    const outputs = published.graph.steps.filter((step: { type: string }) => step.type === 'output');
    expect(outputs.map((output: { name: string; outcome: string }) => [output.name, output.outcome])).toEqual([
      ['approved', 'success'], ['rejected', 'success'], ['provider-error', 'error'],
    ]);
    expect(outputs.every((output: { schema?: unknown }) => output.schema)).toBe(true);
  }

  public async expectNamedPorts(nodeDisplayName: string, ports: readonly { event: string; outcome: 'success' | 'error' }[]): Promise<void> {
    const handles = this.node(nodeDisplayName).locator('.flow-port-handle.output');
    await expect(handles).toHaveCount(ports.length);
    for (const port of ports) {
      const handle = handles.filter({ hasText: port.event });
      await expect(handle).toHaveCount(1);
      await expect(handle).toHaveClass(new RegExp(`outcome-${port.outcome}`));
      await expect(handle.locator('.flow-port-label')).toContainText(port.outcome === 'success' ? `✓ ${port.event}` : `! ${port.event}`);
    }
  }

  public async selectStep(displayName: string): Promise<void> {
    await expect(async () => {
      await this.node(displayName).locator('.flow-node-copy').click();
      await expect(this.page.getByTestId(TestIds.flowObservability.stepDisplayName)).toBeVisible({ timeout: 2_000 });
    }).toPass({ timeout: 20_000 });
  }

  public async useFlowCallVersion(strategy: 'Active' | 'Exact', version?: string): Promise<void> {
    await this.page.getByTestId(TestIds.flowObservability.flowCallVersionStrategy).selectOption(strategy);
    if (strategy === 'Exact') {
      const versionSelect = this.page.getByTestId(TestIds.flowObservability.flowCallExactVersion);
      await expect(versionSelect).toBeVisible();
      if (version) await versionSelect.selectOption(version);
    }
    // Persist each version switch before the next authoring action so a slower autosave from the
    // previous strategy cannot restore stale ports while the scenario edits another node.
    await this.save();
    await this.expectAutosaved();
  }

  public async addNamedOutput(output: NamedOutputAuthoring): Promise<void> {
    await this.page.getByTestId(TestIds.flowObservability.paletteOutput).click();
    await expect(this.page.getByTestId(TestIds.flowObservability.stepDisplayName)).toHaveValue('Output');
    // Complete the add-step save before editing the new node. On a busy smoke run, letting that
    // request overlap the first field change can reapply the initial "Output" projection.
    await this.save();
    await this.expectAutosaved();
    await this.changeField(TestIds.flowObservability.stepDisplayName, output.displayName);
    await this.changeField(TestIds.flowObservability.stepName, output.name);
    await this.page.getByTestId(TestIds.flowObservability.outputOutcome).selectOption(output.outcome);
    await this.changeField(TestIds.flowObservability.outputMapping, output.mapping);
    await this.changeField(TestIds.flowObservability.outputSchema, output.schema);
    if (output.outcome === 'Error') {
      if (output.code) await this.changeField(TestIds.flowObservability.outputErrorCode, output.code);
      if (output.message) await this.changeField(TestIds.flowObservability.outputErrorMessage, output.message);
      if (output.details) await this.changeField(TestIds.flowObservability.outputErrorDetails, output.details);
    }
    await expect(this.node(output.displayName)).toBeVisible();
  }

  public async dragNamedOutputToInput(sourceDisplayName: string, event: string, targetDisplayName: string): Promise<void> {
    const source = this.namedOutputHandle(sourceDisplayName, event);
    const target = this.node(targetDisplayName).locator('.flow-port-handle.input');
    await this.drag(source, target);
  }

  public async setSelectedTransitionPriority(priority: number): Promise<void> {
    await this.changeField(TestIds.flowObservability.transitionPriority, priority.toString());
  }

  public async changeSelectedTransitionEvent(event: string): Promise<void> {
    await this.page.getByTestId(TestIds.flowObservability.transitionEvent).selectOption(event);
  }

  public async expectSelectedTransitionPriority(priority: number): Promise<void> {
    await expect(this.page.getByTestId(TestIds.flowObservability.transitionPriority)).toHaveValue(priority.toString());
  }

  public async renameSelectedOutput(name: string): Promise<void> {
    await this.changeField(TestIds.flowObservability.stepName, name);
  }

  public async dragOutputToInput(sourceName: string, targetName: string): Promise<void> {
    const source = this.node(sourceName).locator('.flow-port-handle.output').first();
    const target = this.node(targetName).locator('.flow-port-handle.input');
    await this.drag(source, target);
  }

  public async expectTransition(text: string): Promise<void> {
    await expect(this.page.locator('.transition-list li > button').filter({ hasText: text })).toBeVisible();
  }

  public async selectTransition(text: string): Promise<void> {
    await expect(async () => {
      await this.page.locator('.transition-list li > button').filter({ hasText: text }).click();
      await expect(this.page.getByTestId(TestIds.flowObservability.selectedTransitionEditor)).toBeVisible({ timeout: 2_000 });
    }).toPass({ timeout: 20_000 });
  }

  public async reconnectSelectedTransitionSource(targetName: string): Promise<void> {
    const sourceTarget = this.node(targetName).locator('.flow-port-handle.output');
    const sourceBox = await sourceTarget.boundingBox();
    if (!sourceBox) throw new Error(`Output handle for '${targetName}' is not visible.`);
    const controls = this.page.locator('svg .diagram-control');
    const count = await controls.count();
    if (count < 2) throw new Error('The selected transition does not expose two endpoint controls.');
    const from = await this.page.getByTestId(TestIds.flowObservability.transitionFrom).inputValue();
    const linkSource = await this.nearestControlToNodeOutput(controls, from);
    await this.drag(linkSource, sourceTarget);
  }

  public async reconnectSelectedTransitionTarget(targetName: string): Promise<void> {
    const target = this.node(targetName).locator('.flow-port-handle.input');
    const targetBox = await target.boundingBox();
    if (!targetBox) throw new Error(`Input handle for '${targetName}' is not visible.`);
    const controls = this.page.locator('svg .diagram-control');
    if (await controls.count() < 2) throw new Error('The selected transition does not expose two endpoint controls.');
    const to = await this.page.getByTestId(TestIds.flowObservability.transitionTo).inputValue();
    const linkTarget = await this.nearestControlToNodeInput(controls, to);
    await this.drag(linkTarget, target);
  }

  public async expectSelectedTransition(from: string, event: string, to: string, id: string): Promise<void> {
    await expect(this.page.getByTestId(TestIds.flowObservability.transitionFrom)).toHaveValue(from);
    await expect(this.page.getByTestId(TestIds.flowObservability.transitionEvent)).toHaveValue(event);
    await expect(this.page.getByTestId(TestIds.flowObservability.transitionTo)).toHaveValue(to);
    await expect(this.page.locator('.transition-editor input[readonly]').first()).toHaveValue(id);
  }

  public async undo(): Promise<void> {
    await this.page.getByRole('button', { name: 'Undo', exact: true }).click();
  }

  public async redo(): Promise<void> {
    await this.page.getByRole('button', { name: 'Redo', exact: true }).click();
  }

  public async expectInvalidDropDoesNotChangeTransitionCount(sourceName: string): Promise<void> {
    const before = await this.page.locator('.transition-list li').count();
    const source = this.node(sourceName).locator('.flow-port-handle.output').first();
    const canvas = this.page.locator('.flow-diagram-canvas');
    const sourceBox = await source.boundingBox();
    const canvasBox = await canvas.boundingBox();
    if (!sourceBox || !canvasBox) throw new Error('The designer canvas is not measurable.');
    await this.page.mouse.move(sourceBox.x + sourceBox.width / 2, sourceBox.y + sourceBox.height / 2);
    await this.page.mouse.down();
    await this.page.mouse.move(canvasBox.x + canvasBox.width - 24, canvasBox.y + canvasBox.height - 24, { steps: 8 });
    await this.page.mouse.up();
    await expect(this.page.locator('.transition-list li')).toHaveCount(before);
  }

  public async expectUnsupportedConnectionDoesNotChangeTransitionCount(sourceName: string, targetName: string): Promise<void> {
    const before = await this.page.locator('.transition-list li').count();
    const source = this.node(sourceName).locator('.flow-port-handle.output').first();
    const incompatibleTarget = this.node(targetName).locator('.flow-port-handle.output').first();
    await this.drag(source, incompatibleTarget);
    await expect(this.page.locator('.transition-list li')).toHaveCount(before);
  }

  public async expectValidationFeedback(): Promise<void> {
    await this.validate();
    await expect(this.page.locator('.validation-dock li').first()).toBeVisible();
  }

  public async expectAutosaved(): Promise<void> {
    await expect(this.page.locator('.save-state.saved')).toBeVisible();
  }

  public async publishAsReadOnlyFixture(consoleUrl: string, name: string): Promise<void> {
    const publish = await this.page.request.post(`${consoleUrl}/api/flows/${encodeURIComponent(name)}/publish`, {
      data: { version: '1.0.0', activate: true },
    });
    expect(publish.status(), await publish.text()).toBe(201);

    const current = await this.page.request.get(`${consoleUrl}/api/flows/${encodeURIComponent(name)}`);
    const currentBody = await current.text();
    expect(current.status(), currentBody).toBe(200);
    const flow = JSON.parse(currentBody);
    const update = await this.page.request.put(`${consoleUrl}/api/flows/${encodeURIComponent(name)}`, {
      headers: { 'If-Match': current.headers()['etag'] },
      data: {
        description: flow.description,
        version: flow.version,
        enabled: flow.enabled,
        definition: flow.definition,
        metadata: { ...flow.metadata, 'agentstration.io/pack.name': 'flow-transition-ux-fixture' },
        displayName: flow.displayName,
      },
    });
    expect(update.status(), await update.text()).toBe(200);
  }

  public async expectReadOnlyPublished(consoleUrl: string, namespace: string, name: string): Promise<void> {
    await this.open(consoleUrl, namespace, name);
    await expect(this.page.locator('.read-only-badge')).toBeVisible();
    await expect(this.page.locator('.flow-port-handle.locked').first()).toBeVisible();
    await expect(this.page.locator('.flow-port-handle:not(.locked)')).toHaveCount(0);
    await expect(this.page.locator('svg .diagram-control')).toHaveCount(0);
  }

  public async open(consoleUrl: string, namespace: string, name: string): Promise<void> {
    const response = await this.page.goto(`${consoleUrl}/namespaces/${encodeURIComponent(namespace)}/flows/${encodeURIComponent(name)}/designer`, { waitUntil: 'domcontentloaded' });
    if (!response?.ok()) throw new Error(`Flow designer returned HTTP ${response?.status() ?? 'no response'}.`);
    await this.page.getByTestId(TestIds.flowObservability.designer).waitFor({ state: 'visible' });
  }

  public async validate(): Promise<void> {
    await this.page.getByTestId(TestIds.flowObservability.designerValidate).click();
  }

  public async save(): Promise<void> {
    await this.page.getByTestId(TestIds.flowObservability.designerSave).click();
  }

  public async expectExistingLinksVisible(): Promise<void> {
    const links = this.page.locator('svg .diagram-link');
    await expect(links.first()).toBeVisible();
  }

  public async expectCanvasFitsViewportAndWheelZoomContract(): Promise<void> {
    const canvas = this.page.locator('.flow-canvas-wrap');
    await expect(canvas).toBeVisible();
    const bounds = await canvas.boundingBox();
    const viewport = this.page.viewportSize();
    expect(bounds).not.toBeNull();
    expect(viewport).not.toBeNull();
    expect(bounds!.y + bounds!.height).toBeLessThanOrEqual(viewport!.height + 1);

    const zoom = this.page.locator('.flow-zoom-controls span');
    const initialZoom = await zoom.textContent();
    await canvas.hover();
    await this.page.mouse.wheel(0, 480);
    await expect(zoom).toHaveText(initialZoom ?? '');

    await canvas.hover();
    await this.page.keyboard.down('Control');
    await this.page.mouse.wheel(0, -480);
    await this.page.keyboard.up('Control');
    await expect(zoom).not.toHaveText(initialZoom ?? '');
  }

  public async selectFirstInspectorTransition(): Promise<void> {
    const linkPath = this.page.locator('svg .diagram-link path').first();
    const initialStroke = await linkPath.getAttribute('stroke');
    await this.page.locator('.transition-list li > button:first-child').click();
    await expect(this.page.locator('.transition-editor')).toBeVisible();
    await expect.poll(async () => await linkPath.getAttribute('stroke')).not.toBe(initialStroke);
  }

  public get designer() { return this.page.getByTestId(TestIds.flowObservability.designer); }

  private node(name: string): Locator {
    return this.page.locator('.flow-node').filter({ has: this.page.locator('strong').filter({ hasText: new RegExp(`^${escapeRegExp(name)}$`, 'i') }) }).first();
  }

  private namedOutputHandle(nodeDisplayName: string, event: string): Locator {
    return this.node(nodeDisplayName).locator('.flow-port-handle.output').filter({
      has: this.page.locator('.flow-port-label').filter({ hasText: new RegExp(`(?:✓|!)\\s*${escapeRegExp(event)}$`) }),
    });
  }

  private async changeField(testId: string, value: string): Promise<void> {
    const field = this.page.getByTestId(testId);
    await field.fill(value);
    await field.blur();
    await expect(field).toHaveValue(value);
  }

  private async createDraft(consoleUrl: string, name: string, displayName: string): Promise<{ value: Record<string, unknown>; eTag: string }> {
    const response = await this.page.request.post(`${consoleUrl}/api/flows/drafts`, {
      data: { name, displayName, template: 'Empty' },
    });
    const body = await response.text();
    expect(response.status(), body).toBe(201);
    return JSON.parse(body);
  }

  private async getDraft(consoleUrl: string, name: string): Promise<{ value: Record<string, unknown>; eTag: string }> {
    const response = await this.page.request.get(`${consoleUrl}/api/flows/${encodeURIComponent(name)}/draft`);
    const body = await response.text();
    expect(response.status(), body).toBe(200);
    return JSON.parse(body);
  }

  private async updateDraft(
    consoleUrl: string,
    name: string,
    draftResponse: { value: Record<string, unknown>; eTag: string },
    definition: Record<string, unknown>,
  ): Promise<string> {
    const response = await this.page.request.put(`${consoleUrl}/api/flows/${encodeURIComponent(name)}/draft`, {
      headers: { 'If-Match': draftResponse.eTag },
      data: {
        displayName: draftResponse.value.displayName,
        description: draftResponse.value.description,
        tags: draftResponse.value.tags,
        definition,
      },
    });
    const body = await response.text();
    expect(response.status(), body).toBe(200);
    return JSON.parse(body).eTag;
  }

  private async publishDraft(consoleUrl: string, name: string, version: string, activate: boolean): Promise<void> {
    const response = await this.page.request.post(`${consoleUrl}/api/flows/${encodeURIComponent(name)}/publish`, {
      data: { version, activate },
    });
    expect(response.status(), await response.text()).toBe(201);
  }

  private async drag(source: Locator, target: Locator): Promise<void> {
    const sourceBox = await source.boundingBox();
    const targetBox = await target.boundingBox();
    if (!sourceBox || !targetBox) throw new Error('A transition endpoint is not visible.');
    await this.page.mouse.move(sourceBox.x + sourceBox.width / 2, sourceBox.y + sourceBox.height / 2);
    await this.page.mouse.down();
    await this.page.mouse.move(targetBox.x + targetBox.width / 2, targetBox.y + targetBox.height / 2, { steps: 10 });
    await this.page.mouse.up();
  }

  private async nearestControlToNodeOutput(controls: Locator, nodeName: string): Promise<Locator> {
    const nodeOutput = this.node(nodeName).locator('.flow-port-handle.output');
    const outputBox = await nodeOutput.first().boundingBox();
    if (!outputBox) throw new Error(`The source output for '${nodeName}' is not visible.`);
    const outputCenter = { x: outputBox.x + outputBox.width / 2, y: outputBox.y + outputBox.height / 2 };
    const all = await controls.all();
    const boxes = await Promise.all(all.map(control => control.boundingBox()));
    const nearest = boxes
      .map((box, index) => ({ box, index }))
      .filter(item => item.box)
      .sort((a, b) => distance(a.box!, outputCenter) - distance(b.box!, outputCenter))[0];
    if (!nearest) throw new Error('The selected transition endpoint controls are not measurable.');
    return controls.nth(nearest.index);
  }

  private async nearestControlToNodeInput(controls: Locator, nodeName: string): Promise<Locator> {
    const nodeInput = this.node(nodeName).locator('.flow-port-handle.input');
    const inputBox = await nodeInput.boundingBox();
    if (!inputBox) throw new Error(`The target input for '${nodeName}' is not visible.`);
    const inputCenter = { x: inputBox.x + inputBox.width / 2, y: inputBox.y + inputBox.height / 2 };
    const all = await controls.all();
    const boxes = await Promise.all(all.map(control => control.boundingBox()));
    const nearest = boxes
      .map((box, index) => ({ box, index }))
      .filter(item => item.box)
      .sort((a, b) => distance(a.box!, inputCenter) - distance(b.box!, inputCenter))[0];
    if (!nearest) throw new Error('The selected transition endpoint controls are not measurable.');
    return controls.nth(nearest.index);
  }

}

function distance(box: { x: number; y: number; width: number; height: number }, point: { x: number; y: number }): number {
  const center = { x: box.x + box.width / 2, y: box.y + box.height / 2 };
  return Math.hypot(center.x - point.x, center.y - point.y);
}

function escapeRegExp(value: string): string {
  return value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}
