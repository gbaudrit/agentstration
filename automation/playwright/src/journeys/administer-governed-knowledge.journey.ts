import { Checkpoints } from '../contracts/checkpoints.js';
import { ExpectedTextByLocale } from '../locales/expected-text.js';
import type { Journey } from './journey.js';
import { projectKnowledgeSources, type SourceLifecycleInput } from './source-lifecycle.journey.js';

export const administerGovernedKnowledge: Journey<SourceLifecycleInput> = async (context, input) => {
  await projectKnowledgeSources(context, input);
  const fixture = context.pages.sourceLifecycle.projectionFixture;
  const text = ExpectedTextByLocale[input.locale ?? 'en-US'].governedKnowledge;
  await context.pages.governedKnowledge.publishExposure(context.consoleUrl, fixture.knowledge, fixture.scope, text);
  await context.checkpoint({ name: Checkpoints.governedKnowledge.toolsPublished, page: context.pages.page, target: context.pages.governedKnowledge.publishedTools });
  const assignment = await context.pages.governedKnowledge.assignSubset(context.consoleUrl, fixture.knowledge, fixture.scope, text);
  await context.checkpoint({ name: Checkpoints.governedKnowledge.agentPinned, page: context.pages.page, target: context.pages.governedKnowledge.pinnedAssignment });
  await context.pages.governedKnowledge.broadenDraft(assignment, text);
  const snapshots = await context.pages.sourceLifecycle.snapshotEvidence(context.consoleUrl);
  await context.pages.sourceLifecycle.openSnapshotArtifact(context.consoleUrl);
  await context.pages.artifacts.exerciseSnapshotArtifact(context.consoleUrl, fixture.artifactId, text);
  await context.pages.sourceLifecycle.assertSnapshotEvidence(context.consoleUrl, snapshots);
  await context.checkpoint({ name: Checkpoints.governedKnowledge.artifactPurged, page: context.pages.page });
};
