import { expect, type Locator, type Page } from '@playwright/test';
import { TestIds } from '../contracts/test-ids.js';
import { fillAndCommit } from './controls.js';

export class ModelProviderAccessPage {
  public constructor(private readonly page: Page) {}

  public get runDetails(): Locator {
    return this.page.getByTestId(TestIds.flowObservability.agentRunDetails);
  }

  public async expectProviderHidden(consoleUrl: string, providerName: string): Promise<void> {
    const headers = await this.contextHeaders();
    const response = await this.page.request.get(
      `${consoleUrl}/api/modelproviders/${encodeURIComponent(providerName)}?scopeRef=${encodeURIComponent('/instance')}`,
      { headers },
    );
    const body = await response.text();
    expect(response.status(), body).toBe(404);
  }

  public async runAgent(consoleUrl: string, agentName: string, prompt: string): Promise<string> {
    const target = new URL(`/agents/${encodeURIComponent(agentName)}/run`, consoleUrl);
    const scopeRef = await this.selectedScopeRef();
    if (scopeRef) target.searchParams.set('context', scopeRef);
    const response = await this.page.goto(
      target.toString(),
      { waitUntil: 'domcontentloaded' },
    );
    if (!response?.ok()) throw new Error(`Agent runner returned HTTP ${response?.status() ?? 'no response'}.`);
    await this.page.locator(
      `[data-testid="${TestIds.flowObservability.agentRunner}"][data-interactive="true"]`,
    ).waitFor({ state: 'visible' });
    const input = this.page.getByTestId(TestIds.flowObservability.agentRunPrompt);
    await fillAndCommit(input, prompt);
    await this.page.getByTestId(TestIds.flowObservability.agentRunSubmit).click();
    await this.runDetails.waitFor({ state: 'visible' });
    await expect.poll(() => this.runDetails.getAttribute('data-run-state'), { timeout: 90_000 }).toBe('Succeeded');
    const runId = await this.runDetails.getAttribute('data-run-id');
    if (!runId) throw new Error(`Agent '${agentName}' did not expose its Runtime Run identifier.`);
    return runId;
  }

  public async readAgent(
    consoleUrl: string,
    agentName: string,
    scopeRef: string,
  ): Promise<AgentExecutionResource> {
    const headers = { 'X-Agentstration-Context': scopeRef };
    const agent = await this.page.request.get(
      `${consoleUrl}/api/agents/${encodeURIComponent(agentName)}`,
      { headers },
    );
    const agentBody = await agent.text();
    expect(agent.status(), agentBody).toBe(200);
    return {
      ...(JSON.parse(agentBody) as Omit<AgentExecutionResource, 'name'>),
      name: agentName,
    };
  }

  public async expectAgentRunRejected(consoleUrl: string, resource: AgentExecutionResource): Promise<void> {
    const headers = { 'X-Agentstration-Context': resource.scopeRef };
    const response = await this.page.request.post(`${consoleUrl}/api/runtime/runs`, {
      headers,
      data: {
        executionScopeRef: resource.scopeRef,
        agent: {
          resourceId: resource.name,
          version: resource.generation,
          namespace: resource.namespace,
          scopeRef: resource.scopeRef,
          resourceUid: resource.uid,
        },
        input: { messages: [{ role: 'User', content: 'This execution must be rejected after Provider revocation.' }] },
        origin: 'Api',
      },
    });
    const body = await response.text();
    expect(response.status(), body).toBe(202);
    let run = JSON.parse(body) as RuntimeRunWire;
    await expect.poll(async () => {
      const current = await this.page.request.get(
        `${consoleUrl}/api/runtime/runs/${encodeURIComponent(run.id)}?scopeRef=${encodeURIComponent(resource.scopeRef)}`,
        { headers },
      );
      const currentBody = await current.text();
      expect(current.status(), currentBody).toBe(200);
      run = JSON.parse(currentBody) as RuntimeRunWire;
      return run.status.state;
    }, { timeout: 90_000 }).toBe('Failed');
    expect(JSON.stringify(run.status)).not.toContain('ollama');
  }

  private async selectedScopeRef(): Promise<string | null> {
    return await this.page.getByTestId(TestIds.console.shell)
      .getAttribute('data-context-scope')
      .catch(() => null);
  }

  private async contextHeaders(): Promise<Record<string, string>> {
    const scopeRef = await this.selectedScopeRef();
    return scopeRef ? { 'X-Agentstration-Context': scopeRef } : {};
  }
}

interface RuntimeRunWire {
  id: string;
  status: {
    state: string;
    error?: string | null;
    errorCode?: string | null;
  };
}

interface AgentExecutionResource {
  uid: string;
  name: string;
  generation: number;
  scopeRef: string;
  namespace: string;
}
