import { test, expect } from '../src/fixtures/test.js';
import { ProductPages } from '../src/pages/product.pages.js';
import { withCollectionAssertions } from '../src/journeys/inspect-collections.journey.js';
import { ignoreCheckpoints, type Journey } from '../src/journeys/journey.js';
import { administerDataSources, projectKnowledgeSources } from '../src/journeys/source-lifecycle.journey.js';
import { administerGovernedToolSets } from '../src/journeys/administer-governed-toolsets.journey.js';
import { exerciseOperationalTriggers } from '../src/journeys/exercise-operational-triggers.journey.js';
import { inspectConsoleAdministration } from '../src/journeys/inspect-console-administration.journey.js';
import { exerciseSchemaAdministration } from '../src/journeys/exercise-schema-administration.journey.js';
import { exerciseModelAdministration } from '../src/journeys/exercise-model-administration.journey.js';
import { authenticateConsole } from '../src/journeys/authenticate-console.journey.js';
import { CollectionContractPage } from '../src/pages/collection-contract.page.js';

test.use({ authenticationMode: 'Local', collectionCampaign: true });

const cases: ReadonlyArray<{ name: string; run: Journey<Record<string, never>> }> = [
  { name: 'acquisition history and profile revisions', run: context => administerDataSources(context, {}) },
  { name: 'projections, input evidence and snapshots', run: context => projectKnowledgeSources(context, {}) },
  { name: 'ToolSets, provider groups and pinned Agent members', run: context => administerGovernedToolSets(context, {}) },
  { name: 'Trigger occurrences and operational ownership', run: context => exerciseOperationalTriggers(context, {}) },
  { name: 'members, personal tokens and command results', run: async context => {
    await authenticateConsole(context, {});
    const workspaceId = await context.pages.organizationWorkspaces.selectByName('default');
    await new CollectionContractPage(context.pages.page).prepareMemberFixture(context.consoleUrl, workspaceId);
    await inspectConsoleAdministration(context, {});
  } },
  { name: 'Schema versions', run: context => exerciseSchemaAdministration(context, {}) },
  { name: 'provider models and Model Profile capabilities', run: context => exerciseModelAdministration(context, {
    providerName: 'collection-ollama', providerDisplayName: 'Collection Ollama',
    profileName: 'collection-model', profileDisplayName: 'Collection model',
    runtimeName: 'collection-runtime', runtimeDisplayName: 'Collection runtime',
  }) },

];

for (const scenario of cases) {
  test(`populated collection contract: ${scenario.name} @collections @smoke`, async ({ page, product }, testInfo) => {
    test.setTimeout(420_000);
    const observations = await withCollectionAssertions(
      { ...product, pages: new ProductPages(page), checkpoint: async checkpoint => { await testInfo.attach(checkpoint.name, { body: await checkpoint.page.screenshot({ fullPage: true }), contentType: 'image/png' }); } }, scenario.run, {},
    );
    expect(observations.length).toBeGreaterThan(0);
    await testInfo.attach('collection-observations', { body: JSON.stringify(observations, null, 2), contentType: 'application/json' });
  });
}
