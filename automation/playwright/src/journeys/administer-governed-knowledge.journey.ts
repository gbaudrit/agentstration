import { Checkpoints } from '../contracts/checkpoints.js';
import { ExpectedTextByLocale } from '../locales/expected-text.js';
import type { Journey } from './journey.js';
import type { SourceLifecycleInput } from './source-lifecycle.journey.js';
import { administerGovernedToolSets } from './administer-governed-toolsets.journey.js';

export const administerGovernedKnowledge: Journey<SourceLifecycleInput> = async (context, input) => {
  await administerGovernedToolSets(context, input);
  const fixture = context.pages.sourceLifecycle.projectionFixture;
  const text = ExpectedTextByLocale[input.locale ?? 'en-US'].governedKnowledge;
  const snapshots = await context.pages.sourceLifecycle.snapshotEvidence(context.consoleUrl);
  await context.pages.sourceLifecycle.openSnapshotArtifact(context.consoleUrl);
  await context.pages.artifacts.exerciseSnapshotArtifact(context.consoleUrl, fixture.artifactId, text);
  await context.pages.sourceLifecycle.assertSnapshotEvidence(context.consoleUrl, snapshots);
  await context.checkpoint({ name: Checkpoints.governedKnowledge.artifactPurged, page: context.pages.page });
};
