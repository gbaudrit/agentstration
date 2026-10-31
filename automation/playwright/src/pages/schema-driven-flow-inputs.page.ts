import { expect, type Locator, type Page } from '@playwright/test';
import { TestIds } from '../contracts/test-ids.js';

interface DraftWire {
  value: { displayName: string; description?: string; tags?: Record<string, string> };
  eTag: string;
}

interface FlowRunWire {
  id: string;
  input: Record<string, unknown>;
}

interface FlowRunLocation {
  id: string;
  scopeRef?: string;
}

export interface SchemaDrivenFlowInputFixture {
  flowName: string;
}

const firstSchema = {
  type: 'object',
  properties: {
    name: { type: 'string', title: 'Request name', description: 'A descriptive request name.', minLength: 3 },
    mode: { type: 'string', title: 'Execution mode', enum: ['safe', 'fast'], default: 'safe' },
    options: {
      type: 'object', title: 'Options', properties: {
        enabled: { type: 'boolean', title: 'Enabled', default: true },
      }, required: ['enabled'], additionalProperties: false, default: { enabled: true },
    },
    tags: { type: 'array', title: 'Tags', items: { type: 'string' }, minItems: 1, maxItems: 2 },
  },
  required: ['name', 'mode', 'options', 'tags'],
  additionalProperties: false,
};

const secondSchema = {
  type: 'object',
  properties: {
    count: { type: 'integer', title: 'Result count', minimum: 1, maximum: 5 },
  },
  required: ['count'],
  additionalProperties: false,
};

const fallbackSchema = {
  type: 'object',
};

export class SchemaDrivenFlowInputsPage {
  public lastFixture?: SchemaDrivenFlowInputFixture;

  public constructor(private readonly page: Page) {}

  public async provisionAndExercise(consoleUrl: string): Promise<SchemaDrivenFlowInputFixture> {
    this.page.setDefaultTimeout(15_000);
    const flowName = `schema-run-input-${Date.now().toString(36)}`;
    await this.createFixture(consoleUrl, flowName);

    await this.openRunDialog(consoleUrl, flowName, false);
    await this.exerciseGeneratedEditor();
    await this.exerciseVersionChangeAndFallback();
    const run = await this.submit();
    await this.assertSubmittedInput(consoleUrl, run, { count: 2 });

    await this.openRunDialog(consoleUrl, flowName, true);
    await expect(this.editor.getByTestId(TestIds.common.schemaFormMode)).toBeVisible();

    this.lastFixture = { flowName };
    return this.lastFixture;
  }

  public async assertLocalizedModes(consoleUrl: string, flowName: string,
    expected: { formMode: string; rawMode: string }): Promise<void> {
    await this.openRunDialog(consoleUrl, flowName, false);
    await expect(this.editor.getByTestId(TestIds.common.schemaFormMode)).toHaveText(expected.formMode);
    await expect(this.editor.getByTestId(TestIds.common.schemaRawMode)).toHaveText(expected.rawMode);
  }

  public async assertResponsive(): Promise<void> {
    const dialog = this.dialog;
    await expect(dialog).toBeVisible();
    const layout = await this.page.evaluate(() => ({
      scrollWidth: document.documentElement.scrollWidth,
      viewportWidth: window.innerWidth,
      offenders: Array.from(document.querySelectorAll<HTMLElement>('body *')).map(element => {
        const bounds = element.getBoundingClientRect();
        return {
          selector: `${element.tagName.toLowerCase()}${element.dataset.testid ? `[data-testid="${element.dataset.testid}"]` : ''}`,
          right: Math.round(bounds.right), width: Math.round(bounds.width),
        };
      }).filter(item => item.right > window.innerWidth + 1 || item.width > window.innerWidth + 1).slice(-12),
    }));
    expect(layout.scrollWidth, `Overflowing elements: ${JSON.stringify(layout.offenders)}`).toBeLessThanOrEqual(layout.viewportWidth);
    expect((await dialog.boundingBox())?.width ?? 0).toBeLessThanOrEqual(390);
  }

  public get dialog(): Locator {
    return this.page.getByTestId(TestIds.flowObservability.runDialog);
  }

  private get editor(): Locator {
    return this.dialog.getByTestId(TestIds.common.schemaInputEditor);
  }

  private async exerciseGeneratedEditor(): Promise<void> {
    const name = this.field('$.name').locator('input');
    const mode = this.field('$.mode').locator('select');
    const enabled = this.field('$.options.enabled').locator('input');
    const tags = this.field('$.tags');
    const submit = this.page.getByTestId(TestIds.flowObservability.runSubmit);

    await expect(name).toBeVisible();
    await expect(this.field('$.name')).toContainText('A descriptive request name.');
    await expect(mode).toHaveValue('"safe"');
    await expect(enabled).toBeChecked();
    await expect(submit).toBeDisabled();

    await name.fill('ab');
    await expect(this.field('$.name').getByRole('alert')).toBeVisible();
    await expect(submit).toBeDisabled();
    await name.fill('Flow request');
    await mode.selectOption('"fast"');
    await this.field('$.options.enabled').locator('label.schema-toggle').click();
    await expect(enabled).not.toBeChecked();
    await tags.getByRole('button').click();
    await this.field('$.tags[0]').locator('input').fill('browser');
    await expect(submit).toBeEnabled();

    await this.editor.getByTestId(TestIds.common.schemaRawMode).click();
    const raw = this.editor.locator('textarea');
    await expect(raw).toHaveValue(/"name": "Flow request"/);
    await expect(raw).toHaveValue(/"tags": \[/);
    await raw.fill('not-json');
    await expect(this.editor.getByRole('alert')).toBeVisible();
    await expect(submit).toBeDisabled();
    await raw.fill(JSON.stringify({ name: 'Flow request', mode: 'fast', options: { enabled: false }, tags: ['browser'] }, null, 2));
    await expect(submit).toBeEnabled();
    await this.editor.getByTestId(TestIds.common.schemaFormMode).click();
    await expect(name).toHaveValue('Flow request');
    await expect(mode).toHaveValue('"fast"');
    await expect(enabled).not.toBeChecked();
    await expect(this.field('$.tags[0]').locator('input')).toHaveValue('browser');
  }

  private async exerciseVersionChangeAndFallback(): Promise<void> {
    const versions = this.dialog.locator('select').first();
    const submit = this.page.getByTestId(TestIds.flowObservability.runSubmit);
    await versions.selectOption('2.0.0');
    await expect(this.field('$.count').locator('input')).toBeVisible();
    await expect(submit).toBeDisabled();

    await this.editor.getByTestId(TestIds.common.schemaRawMode).click();
    const raw = this.editor.locator('textarea');
    await expect(raw).toHaveValue(/"name": "Flow request"/);

    await versions.selectOption('3.0.0');
    await expect(this.editor.getByRole('status')).toBeVisible();
    await expect(this.editor.getByTestId(TestIds.common.schemaFormMode)).toHaveCount(0);
    await expect(raw).toHaveValue(/"name": "Flow request"/);

    await versions.selectOption('2.0.0');
    await expect(this.field('$.count').locator('input')).toBeVisible();
    await this.editor.getByTestId(TestIds.common.schemaRawMode).click();
    await this.editor.locator('textarea').fill(JSON.stringify({ count: 2 }, null, 2));
    await this.editor.getByTestId(TestIds.common.schemaFormMode).click();
    await expect(this.field('$.count').locator('input')).toHaveValue('2');
    await expect(submit).toBeEnabled();
  }

  private async submit(): Promise<FlowRunLocation> {
    await this.page.getByTestId(TestIds.flowObservability.runSubmit).click();
    await this.page.waitForURL(url => /\/flow-runs\/[^/]+$/.test(url.pathname), { timeout: 30_000 });
    const location = new URL(this.page.url());
    const runId = location.pathname.split('/').at(-1);
    if (!runId) throw new Error('The submitted Flow Run did not expose its identifier.');
    return {
      id: decodeURIComponent(runId),
      scopeRef: location.searchParams.get('scopeRef') ?? undefined,
    };
  }

  private async assertSubmittedInput(consoleUrl: string, run: FlowRunLocation, expectedInput: Record<string, unknown>): Promise<void> {
    const query = run.scopeRef ? `?scopeRef=${encodeURIComponent(run.scopeRef)}` : '';
    const response = await this.page.request.get(`${consoleUrl}/api/flowRuns/${encodeURIComponent(run.id)}${query}`);
    expect(response.status(), await response.text()).toBe(200);
    const flowRun = await response.json() as FlowRunWire;
    expect(flowRun.input).toEqual(expectedInput);
  }

  private async openRunDialog(consoleUrl: string, flowName: string, namespaced: boolean): Promise<void> {
    const path = namespaced
      ? `/namespaces/default/flows/${encodeURIComponent(flowName)}`
      : `/flows/${encodeURIComponent(flowName)}`;
    const response = await this.page.goto(`${consoleUrl}${path}`, { waitUntil: 'domcontentloaded' });
    if (!response?.ok()) throw new Error(`Flow details route '${path}' returned HTTP ${response?.status() ?? 'no response'}.`);
    await this.page.getByTestId(TestIds.flowObservability.flowDetails).waitFor({ state: 'visible' });
    const runTab = this.page.locator('.section-tabs button').nth(2);
    await expect(async () => {
      await runTab.click();
      await expect(runTab).toHaveClass(/active/, { timeout: 2_000 });
    }).toPass({ timeout: 20_000 });
    await this.page.getByTestId(TestIds.flowObservability.runOpen).click();
    await expect(this.dialog).toBeVisible();
  }

  private field(path: string): Locator {
    return this.editor.locator(`[data-schema-path="${path}"]`);
  }

  private async createFixture(consoleUrl: string, flowName: string): Promise<void> {
    const created = await this.page.request.post(`${consoleUrl}/api/flows/drafts`, {
      data: { name: flowName, displayName: 'Schema-driven manual input', template: 'Empty' },
    });
    const createdBody = await created.text();
    expect(created.status(), `create schema-driven Flow: ${createdBody}`).toBe(201);
    let draft = JSON.parse(createdBody) as DraftWire;

    draft = await this.updateDraft(consoleUrl, flowName, draft, firstSchema);
    await this.publish(consoleUrl, flowName, '1.0.0', true);
    draft = await this.updateDraft(consoleUrl, flowName, draft, secondSchema);
    await this.publish(consoleUrl, flowName, '2.0.0', false);
    await this.updateDraft(consoleUrl, flowName, draft, fallbackSchema);
    await this.publish(consoleUrl, flowName, '3.0.0', false);
  }

  private async updateDraft(consoleUrl: string, flowName: string, draft: DraftWire,
    schema: Record<string, unknown>): Promise<DraftWire> {
    const response = await this.page.request.put(`${consoleUrl}/api/flows/${encodeURIComponent(flowName)}/draft`, {
      headers: { 'If-Match': draft.eTag },
      data: {
        displayName: draft.value.displayName, description: draft.value.description, tags: draft.value.tags,
        definition: graph(schema),
      },
    });
    const body = await response.text();
    expect(response.status(), `update Flow '${flowName}' draft: ${body}`).toBe(200);
    return JSON.parse(body) as DraftWire;
  }

  private async publish(consoleUrl: string, flowName: string, version: string, activate: boolean): Promise<void> {
    await expectApi(await this.page.request.post(`${consoleUrl}/api/flows/${encodeURIComponent(flowName)}/publish`, {
      data: { version, activate, releaseNotes: 'Schema-driven Flow input browser fixture' },
    }), 201, `publish Flow '${flowName}' version '${version}'`);
  }
}

function graph(schema: Record<string, unknown>): Record<string, unknown> {
  return {
    entryStep: 'input', inputSchema: schema, outputSchema: schema,
    steps: [
      { type: 'input', name: 'input', displayName: 'Input', schema },
      { type: 'output', name: 'completed', displayName: 'Completed', outcome: 'success', outputMapping: '${transition.output}', schema },
    ],
    transitions: [{ id: 'input-completed', fromStep: 'input', event: 'completed', toStep: 'completed' }],
    designer: { preferredLayout: 'Horizontal', nodePositions: { input: { x: 80, y: 220 }, completed: { x: 520, y: 220 } } },
  };
}

async function expectApi(response: { status(): number; text(): Promise<string> }, status: number, operation: string): Promise<void> {
  const body = await response.text();
  expect(response.status(), `${operation}: ${body}`).toBe(status);
}
