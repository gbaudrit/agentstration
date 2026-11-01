import { Checkpoints } from '../contracts/checkpoints.js';
import { ExpectedTextByLocale } from '../locales/expected-text.js';
import type { Journey } from './journey.js';
import { projectKnowledgeSources, type SourceLifecycleInput } from './source-lifecycle.journey.js';

export const administerGovernedToolSets: Journey<SourceLifecycleInput> = async (context, input) => {
  await projectKnowledgeSources(context, input);
  const fixture = context.pages.sourceLifecycle.projectionFixture;
  const locale = input.locale ?? 'en-US';
  const text = ExpectedTextByLocale[locale].governedKnowledge;
  const tools = context.pages.governedKnowledge;
  await tools.publishExposure(context.consoleUrl, fixture.knowledge, fixture.scope, text);
  await context.checkpoint({ name: Checkpoints.governedKnowledge.toolsPublished, page: context.pages.page, target: tools.publishedTools });
  const assignment = await tools.assignSubset(context.consoleUrl, fixture.knowledge, fixture.scope, text);
  await context.checkpoint({ name: Checkpoints.governedKnowledge.agentPinned, page: context.pages.page, target: tools.pinnedAssignment });
  await tools.exerciseVersionGovernance(context.consoleUrl, fixture.knowledge, fixture.scope, assignment, text);
  await tools.broadenDraft(assignment, text);
  await tools.exerciseExposureFailures(context.consoleUrl, fixture.knowledge, fixture.scope, text, locale);
  await context.checkpoint({ name: Checkpoints.governedKnowledge.governanceVerified, page: context.pages.page, target: tools.versions });
};
