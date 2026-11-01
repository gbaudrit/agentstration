import { Checkpoints } from '../contracts/checkpoints.js';
import type { Journey } from './journey.js';
import { prepareConsoleJourney } from './prepare-console.journey.js';

export interface ExerciseOperationalTriggersInput {
  username?: string;
  password?: string;
}

export const exerciseOperationalTriggers: Journey<ExerciseOperationalTriggersInput> = async (context, input) => {
  await prepareConsoleJourney(context, input);
  const execution = await context.pages.executionScopes.ensureFixture(context.consoleUrl);
  const triggers = context.pages.operationalTriggers;
  const fixture = triggers.fixture(execution);

  await triggers.openCreate(context.consoleUrl, fixture);
  await context.checkpoint({
    name: Checkpoints.operationalTriggers.editor,
    page: context.pages.page,
    target: triggers.editor,
  });

  const trigger = await triggers.createDisabled(context.consoleUrl, fixture);
  const operationalWorkspaceId = await triggers.assertOperationalOwnership(
    context.consoleUrl,
    fixture,
    trigger,
  );
  await context.checkpoint({
    name: Checkpoints.operationalTriggers.created,
    page: context.pages.page,
    target: triggers.details,
  });

  const evidence = await triggers.enableAndRun(context.consoleUrl, fixture, operationalWorkspaceId);
  await context.checkpoint({
    name: Checkpoints.operationalTriggers.submitted,
    page: context.pages.page,
    target: triggers.occurrences,
  });

  await triggers.assertBusinessWorkspaceIsolation(context.consoleUrl, fixture, evidence);
  await context.checkpoint({
    name: Checkpoints.operationalTriggers.isolation,
    page: context.pages.page,
    target: triggers.list,
  });

  await triggers.assertDeniedAccessFailures(context.consoleUrl, fixture);
};
