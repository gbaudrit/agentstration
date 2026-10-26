import { expect, type Page } from '@playwright/test';
import { TestIds } from '../contracts/test-ids.js';

interface StagedArtifact {
  artifactId: string;
  fileName: string;
  mediaType: string;
  length: number;
  sha256: string;
}

type ResourceId = string | { value: string };
type StagedArtifactWire = Omit<StagedArtifact, 'artifactId'> & { artifactId: ResourceId };

interface DurableArtifact {
  artifactId: string;
  receipt: {
    storageFlowRunId: string;
    opaqueReference: string;
    mediaType: string;
    length: number;
    sha256: string;
    provenance: Record<string, string>;
  };
}

type DurableArtifactWire = Omit<DurableArtifact, 'artifactId'> & { artifactId: ResourceId };

interface FlowRun {
  id: string;
  status: number;
  output?: Record<string, unknown>;
  error?: { message?: string };
}

export class ArtifactAdministrationPage {
  public constructor(private readonly page: Page) {}

  public async exerciseGovernedContent(consoleUrl: string): Promise<void> {
    const suffix = Date.now().toString(36);
    const smallText = `Governed artifact ${suffix}`;
    const small = await this.createStaged(consoleUrl, `governed-${suffix}.txt`, 'text/plain', smallText);
    const largeText = 'x'.repeat(9_000);
    const large = await this.createStaged(consoleUrl, `large-${suffix}.txt`, 'text/plain', largeText);
    const unsafeMarkup = `<script>window.__artifactExecuted=true</script><img src=x onerror="window.__artifactExecuted=true">${suffix}`;
    const unsafe = await this.createStaged(consoleUrl, `unsafe-${suffix}.html`, 'text/html', unsafeMarkup);

    await this.openStaged(consoleUrl, small.artifactId);
    await this.openStagedContent();
    await this.page.getByTestId(TestIds.artifacts.stagedReadPreview).click();
    await expect(this.page.getByTestId(TestIds.artifacts.stagedContent)).toHaveText(smallText);
    await expect(this.page.getByTestId(TestIds.artifacts.stagedContentStatus)).toBeVisible();
    await this.assertDownload(consoleUrl, small, TestIds.artifacts.stagedDownload, smallText);

    await this.openStaged(consoleUrl, large.artifactId);
    await this.openStagedContent();
    await this.page.getByTestId(TestIds.artifacts.stagedReadPreview).click();
    await expect(this.page.getByTestId(TestIds.artifacts.stagedContent)).toHaveText('x'.repeat(8 * 1024));
    await expect(this.page.getByTestId(TestIds.artifacts.stagedContentStatus)).toBeVisible();

    await this.openStaged(consoleUrl, unsafe.artifactId);
    await this.openStagedContent();
    await this.page.getByTestId(TestIds.artifacts.stagedReadPreview).click();
    await expect(this.page.getByTestId(TestIds.artifacts.stagedContent)).toHaveText(unsafeMarkup);
    await expect(this.page.getByTestId(TestIds.artifacts.stagedContentPanel).locator('script, img')).toHaveCount(0);
    expect(await this.page.evaluate(() => Boolean((window as unknown as Record<string, unknown>).__artifactExecuted))).toBe(false);

    await this.assertMetadataOnlyTokenIsDenied(consoleUrl, small.artifactId);

    const durableText = `durable artifact alpha ${suffix}`;
    const durableSource = await this.createStaged(consoleUrl, `durable-${suffix}.txt`, 'text/plain', durableText);
    const durable = await this.persist(consoleUrl, durableSource);
    await this.openDurable(consoleUrl, durable.artifactId);
    await this.selectTab(TestIds.artifacts.durableContentTab, TestIds.artifacts.durableContentPanel);
    await this.page.getByTestId(TestIds.artifacts.durableMaterialize).click();
    await expect(this.page.getByTestId(TestIds.artifacts.durableOpenMaterialized)).toBeVisible({ timeout: 90_000 });
    await expect(this.page.getByTestId(TestIds.artifacts.durableMaterializationHistory)).toBeVisible();
    await this.assertDownload(consoleUrl, undefined, TestIds.artifacts.durableDownload, durableText);

    const unavailableSource = await this.createStaged(consoleUrl, `unavailable-${suffix}.txt`, 'text/plain', durableText);
    const unavailable = await this.completeWithReceipt(consoleUrl, unavailableSource, {
      ...durable.receipt,
      storageFlowRunId: `missing-storage-${suffix}`,
      opaqueReference: `missing-${suffix}`,
      sha256: unavailableSource.sha256,
      provenance: { fileName: unavailableSource.fileName },
    });
    await this.assertMaterializationFailure(consoleUrl, unavailable.artifactId, [unavailable.receipt.opaqueReference, durableText]);

    const alteredText = `durable artifact bravo ${suffix}`;
    expect(Buffer.byteLength(alteredText)).toBe(Buffer.byteLength(durableText));
    const integritySource = await this.createStaged(consoleUrl, `integrity-${suffix}.txt`, 'text/plain', alteredText);
    const integrity = await this.completeWithReceipt(consoleUrl, integritySource, {
      ...durable.receipt,
      storageFlowRunId: `integrity-storage-${suffix}`,
      sha256: integritySource.sha256,
      provenance: { fileName: integritySource.fileName },
    });
    await this.assertMaterializationFailure(consoleUrl, integrity.artifactId, [integrity.receipt.opaqueReference, alteredText]);
  }

  private async openStaged(consoleUrl: string, artifactId: string): Promise<void> {
    await this.open(`${consoleUrl}/artifacts/staged/${encodeURIComponent(artifactId)}`, TestIds.artifacts.stagedDetails);
  }

  private async openDurable(consoleUrl: string, artifactId: string): Promise<void> {
    await this.open(`${consoleUrl}/artifacts/durable/${encodeURIComponent(artifactId)}`, TestIds.artifacts.durableDetails);
  }

  private async open(url: string, marker: string): Promise<void> {
    const response = await this.page.goto(url, { waitUntil: 'domcontentloaded' });
    if (!response?.ok()) throw new Error(`Artifact route '${url}' returned HTTP ${response?.status() ?? 'no response'}.`);
    await this.page.getByTestId(marker).waitFor({ state: 'visible' });
  }

  private async openStagedContent(): Promise<void> {
    await this.selectTab(TestIds.artifacts.stagedContentTab, TestIds.artifacts.stagedContentPanel);
  }

  private async selectTab(tabTestId: string, panelTestId: string): Promise<void> {
    const tab = this.page.getByTestId(tabTestId);
    const panel = this.page.getByTestId(panelTestId);
    await expect(async () => {
      await tab.click();
      await expect(tab).toHaveAttribute('aria-selected', 'true', { timeout: 2_000 });
      await expect(panel).toBeVisible({ timeout: 2_000 });
    }).toPass({ timeout: 20_000 });
  }

  private async createStaged(consoleUrl: string, fileName: string, mediaType: string, content: string): Promise<StagedArtifact> {
    const created = await this.page.request.post(`${consoleUrl}/api/artifacts/staged`, {
      data: {
        fileName,
        mediaType,
        producer: { kind: 0, id: `playwright-${fileName}`, flowRunId: `playwright-${fileName}`, flowStepId: 'fixture' },
      },
    });
    expect(created.status(), await created.text()).toBe(201);
    const artifactWire = await created.json() as StagedArtifactWire;
    const artifact = { ...artifactWire, artifactId: identifier(artifactWire.artifactId) };
    const written = await this.page.request.post(`${consoleUrl}/api/artifacts/staged/${artifact.artifactId}/content`, {
      data: { offset: 0, contentBase64: Buffer.from(content).toString('base64') },
    });
    expect(written.status(), await written.text()).toBe(200);
    const sealed = await this.page.request.post(`${consoleUrl}/api/artifacts/staged/${artifact.artifactId}/seal`);
    expect(sealed.status(), await sealed.text()).toBe(200);
    const sealedWire = await sealed.json() as StagedArtifactWire;
    return { ...sealedWire, artifactId: identifier(sealedWire.artifactId) };
  }

  private async persist(consoleUrl: string, source: StagedArtifact): Promise<DurableArtifact> {
    const response = await this.page.request.post(`${consoleUrl}/api/namespaces/default/flows/artifact-storage-filesystem-write-builtin/runs`, {
      data: {
        input: { stagedArtifactId: source.artifactId, producerFlowRunId: `playwright-${source.artifactId}`, producerFlowStepId: 'fixture' },
        version: '1.0.0',
      },
    });
    expect(response.status(), await response.text()).toBe(202);
    let run = await response.json() as FlowRun;
    await expect.poll(async () => {
      const current = await this.page.request.get(`${consoleUrl}/api/flowRuns/${encodeURIComponent(run.id)}`);
      expect(current.status(), await current.text()).toBe(200);
      run = await current.json() as FlowRun;
      return [3, 4, 5, 6].includes(run.status);
    }, { timeout: 90_000 }).toBe(true);
    expect(run.status, run.error?.message).toBe(3);
    const artifactId = run.output?.flowRunArtifactId;
    expect(typeof artifactId).toBe('string');
    const durable = await this.page.request.get(`${consoleUrl}/api/artifacts/flow-run-artifacts/${encodeURIComponent(String(artifactId))}`);
    expect(durable.status(), await durable.text()).toBe(200);
    const durableWire = await durable.json() as DurableArtifactWire;
    return { ...durableWire, artifactId: identifier(durableWire.artifactId) };
  }

  private async completeWithReceipt(consoleUrl: string, source: StagedArtifact, receipt: DurableArtifact['receipt']): Promise<DurableArtifact> {
    const response = await this.page.request.post(`${consoleUrl}/api/artifacts/staged/${source.artifactId}/flow-run-artifacts`, {
      data: { producerFlowRunId: `playwright-${source.artifactId}`, producerFlowStepId: 'fixture', receipt },
    });
    expect(response.status(), await response.text()).toBe(201);
    const durableWire = await response.json() as DurableArtifactWire;
    return { ...durableWire, artifactId: identifier(durableWire.artifactId) };
  }

  private async assertDownload(consoleUrl: string, artifact: StagedArtifact | undefined, testId: string, expectedContent: string): Promise<void> {
    const [download] = await Promise.all([
      this.page.waitForEvent('download'),
      this.page.getByTestId(testId).click(),
    ]);
    const stream = await download.createReadStream();
    const chunks: Buffer[] = [];
    for await (const chunk of stream) chunks.push(Buffer.from(chunk));
    expect(Buffer.concat(chunks).toString('utf8')).toBe(expectedContent);
    if (!artifact) return;
    expect(download.suggestedFilename()).toBe(artifact.fileName);
    const response = await this.page.request.get(`${consoleUrl}/api/artifacts/staged/${artifact.artifactId}/download`);
    expect(response.status()).toBe(200);
    expect(response.headers()['content-type']).toContain(artifact.mediaType);
    expect(response.headers()['content-disposition']).toContain(artifact.fileName);
  }

  private async assertMetadataOnlyTokenIsDenied(consoleUrl: string, artifactId: string): Promise<void> {
    const workspaceId = await this.page.getByTestId(TestIds.console.shell).getAttribute('data-workspace-id');
    if (!workspaceId) throw new Error('The authenticated Console shell does not expose its Workspace identifier.');
    const tokenResponse = await this.page.request.post(`${consoleUrl}/api/identity/pat`, {
      data: {
        name: `artifact-metadata-only-${Date.now()}`,
        workspaceId,
        permissions: ['resources/read', 'artifacts/inspect'],
        expiresAt: new Date(Date.now() + 10 * 60_000).toISOString(),
      },
    });
    expect(tokenResponse.status(), await tokenResponse.text()).toBe(201);
    const { token } = await tokenResponse.json() as { token: string };
    expect(token).toMatch(/^agt_pat_[0-9a-f]{32}_[A-Za-z0-9_-]{43,}$/);
    const authorization = { Authorization: `Bearer ${token}` };
    const request = (path: string) => fetch(`${consoleUrl}${path}`, { headers: authorization, redirect: 'manual' });
    const metadata = await request(`/api/artifacts/staged/${artifactId}`);
    expect(metadata.status, `${metadata.headers.get('location') ?? ''} ${await metadata.text()}`).toBe(200);
    await this.expectDenied(await request(`/api/artifacts/staged/${artifactId}/content?offset=0&length=64`));
    await this.expectDenied(await request(`/api/artifacts/staged/${artifactId}/download`));
  }

  private async expectDenied(response: Response): Promise<void> {
    expect(response.status).toBe(403);
    expect(await response.text()).not.toContain('Governed artifact');
  }

  private async assertMaterializationFailure(consoleUrl: string, artifactId: string, secrets: readonly string[]): Promise<void> {
    await this.openDurable(consoleUrl, artifactId);
    await this.selectTab(TestIds.artifacts.durableContentTab, TestIds.artifacts.durableContentPanel);
    await this.page.getByTestId(TestIds.artifacts.durableMaterialize).click();
    const alert = this.page.getByRole('alert');
    await expect(alert).toBeVisible({ timeout: 90_000 });
    const message = await alert.textContent() ?? '';
    expect(message.trim().length).toBeGreaterThan(0);
    for (const secret of secrets) expect(message).not.toContain(secret);
    await expect(this.page.getByTestId(TestIds.artifacts.durableDownload)).toHaveCount(0);
  }
}

function identifier(value: ResourceId): string {
  return (typeof value === 'string' ? value : value.value).replaceAll('-', '');
}
