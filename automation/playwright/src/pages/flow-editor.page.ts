import { expect, type Locator, type Page } from '@playwright/test';
import { TestIds } from '../contracts/test-ids.js';
import { fillAndCommit } from './controls.js';

export interface HandoffRouteDefinition {
  from: string;
  to: string;
}

export interface HandoffFlowDefinition {
  name: string;
  displayName: string;
  description: string;
  version: string;
  enabled: boolean;
  participants: readonly string[];
  initialParticipant: string;
  routes: readonly HandoffRouteDefinition[];
  autonomous: boolean;
  maximumTurnsPerParticipant: number;
  terminationPhrase: string;
}

export class FlowEditorPage {
  public constructor(private readonly page: Page) {}

  public async openNew(consoleUrl: string): Promise<void> {
    const response = await this.page.goto(`${consoleUrl}/flows/new`, { waitUntil: 'domcontentloaded' });
    if (!response?.ok()) throw new Error(`New flow page returned HTTP ${response?.status() ?? 'no response'}.`);
    await this.page.locator(`[data-testid="${TestIds.flowEditor.form}"][data-interactive="true"]`).waitFor({ state: 'visible' });
  }

  public async fillIdentity(flow: Pick<HandoffFlowDefinition, 'name' | 'displayName' | 'description' | 'version'>): Promise<void> {
    await fillAndCommit(this.page.getByTestId(TestIds.flowEditor.name), flow.name);
    await fillAndCommit(this.page.getByTestId(TestIds.flowEditor.displayName), flow.displayName);
    await fillAndCommit(this.page.getByTestId(TestIds.flowEditor.version), flow.version);
    await fillAndCommit(this.page.getByTestId(TestIds.flowEditor.description), flow.description);
  }

  public async chooseOrchestration(): Promise<void> {
    await this.page.getByTestId(TestIds.flowEditor.orchestrationKind).click();
    await this.page.getByTestId(TestIds.flowEditor.continue).click();
    await this.configuration.waitFor({ state: 'visible' });
  }

  public async selectParticipants(participantIds: readonly string[]): Promise<void> {
    for (const participantId of participantIds) {
      const participant = this.page.locator(
        `[data-testid="${TestIds.flowEditor.participant}"][data-agent-id="${attributeValue(participantId)}"]`,
      );
      if (await participant.count() === 0) {
        const available = await this.page.getByTestId(TestIds.flowEditor.participant)
          .evaluateAll(elements => elements.map(element => element.getAttribute('data-agent-id')).filter(Boolean));
        throw new Error(`Flow participant '${participantId}' is unavailable. Available participants: ${available.join(', ') || 'none'}.`);
      }
      await participant.check();
    }
  }

  public async configureHandoff(flow: Pick<HandoffFlowDefinition,
    'enabled' | 'initialParticipant' | 'routes' | 'autonomous' | 'maximumTurnsPerParticipant' | 'terminationPhrase'>): Promise<void> {
    await this.page.getByTestId(TestIds.flowEditor.enabled).setChecked(flow.enabled);
    await this.page.getByTestId(TestIds.flowEditor.strategy).selectOption('Handoff');
    await this.page.getByTestId(TestIds.flowEditor.initialParticipant).selectOption(flow.initialParticipant);

    const routes = this.page.getByTestId(TestIds.flowEditor.route);
    while (await routes.count() < flow.routes.length) {
      const expectedCount = await routes.count() + 1;
      await this.page.getByTestId(TestIds.flowEditor.addRoute).click();
      await expect(routes).toHaveCount(expectedCount);
    }
    while (await routes.count() > flow.routes.length) {
      const expectedCount = await routes.count() - 1;
      await routes.last().getByTestId(TestIds.flowEditor.removeRoute).click();
      await expect(routes).toHaveCount(expectedCount);
    }
    await expect(routes).toHaveCount(flow.routes.length);
    for (let index = 0; index < flow.routes.length; index++) {
      const route = routes.nth(index);
      const from = route.getByTestId(TestIds.flowEditor.routeFrom);
      const to = route.getByTestId(TestIds.flowEditor.routeTo);
      const desired = flow.routes[index]!;
      if (await to.inputValue() === desired.from) {
        await to.selectOption(desired.to);
        await expect(to).toHaveValue(desired.to);
        await from.selectOption(desired.from);
        await expect(from).toHaveValue(desired.from);
      } else {
        await from.selectOption(desired.from);
        await expect(from).toHaveValue(desired.from);
        await to.selectOption(desired.to);
        await expect(to).toHaveValue(desired.to);
      }
    }

    await this.page.getByTestId(TestIds.flowEditor.autonomous).setChecked(flow.autonomous);
    await fillAndCommit(this.page.getByTestId(TestIds.flowEditor.maximumTurns), String(flow.maximumTurnsPerParticipant));
    await fillAndCommit(this.page.getByTestId(TestIds.flowEditor.terminationPhrase), flow.terminationPhrase);
  }

  public async create(name: string): Promise<void> {
    await Promise.all([
      this.page.waitForURL(url => url.pathname === `/flows/${encodeURIComponent(name)}` && url.searchParams.get('view') === 'definition'),
      this.page.getByTestId(TestIds.flowEditor.create).click(),
    ]);
    await this.orchestrationEditor.waitFor({ state: 'visible' });
  }

  public async publish(version: string): Promise<void> {
    await this.page.getByTestId(TestIds.flowEditor.publish).click();
    await this.page.locator(
      `[data-testid="${TestIds.flowEditor.statusMessage}"][data-published-version="${attributeValue(version)}"]`,
    ).waitFor({ state: 'visible' });
  }

  public get form(): Locator { return this.page.getByTestId(TestIds.flowEditor.form); }
  public get identitySection(): Locator { return this.page.getByTestId(TestIds.flowEditor.identitySection); }
  public get configuration(): Locator { return this.page.getByTestId(TestIds.flowEditor.configuration); }
  public get participantsSection(): Locator { return this.page.getByTestId(TestIds.flowEditor.participantsSection); }
  public get strategySection(): Locator { return this.page.getByTestId(TestIds.flowEditor.strategySection); }
  public get preview(): Locator { return this.page.getByTestId(TestIds.flowEditor.preview); }
  public get orchestrationEditor(): Locator { return this.page.getByTestId(TestIds.flowEditor.orchestrationEditor); }
}

function attributeValue(value: string): string {
  if (!/^[a-zA-Z0-9_.:/-]+$/.test(value)) throw new Error(`Unsupported identifier '${value}' in a browser locator.`);
  return value;
}
