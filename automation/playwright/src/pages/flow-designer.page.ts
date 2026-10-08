import { expect, type Locator, type Page } from '@playwright/test';
import { TestIds } from '../contracts/test-ids.js';

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

  public async dragOutputToInput(sourceName: string, targetName: string): Promise<void> {
    const source = this.node(sourceName).locator('.flow-port-handle.output').first();
    const target = this.node(targetName).locator('.flow-port-handle.input');
    await this.drag(source, target);
  }

  public async expectTransition(text: string): Promise<void> {
    await expect(this.page.locator('.transition-list li > button').filter({ hasText: text })).toBeVisible();
  }

  public async selectTransition(text: string): Promise<void> {
    await this.page.locator('.transition-list li > button').filter({ hasText: text }).click();
    await expect(this.page.getByTestId(TestIds.flowObservability.selectedTransitionEditor)).toBeVisible();
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
    await expect(this.page.locator('.flow-port-handle.locked')).toHaveCount(await this.page.locator('.flow-port-handle').count());
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
