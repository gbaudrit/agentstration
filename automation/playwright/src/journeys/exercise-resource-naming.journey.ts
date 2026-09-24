import { Checkpoints } from '../contracts/checkpoints.js';
import type { Journey } from './journey.js';
import { prepareConsoleJourney } from './prepare-console.journey.js';

export interface ExerciseResourceNamingInput {
  username?: string;
  password?: string;
  workspaceName?: string;
}

export const exerciseResourceNaming: Journey<ExerciseResourceNamingInput> = async (context, input) => {
  await prepareConsoleJourney(context, input);
  await context.pages.consoleAdministration.openBootstrapRedirect(context.consoleUrl);

  await context.pages.resourceNaming.openAgent(context.consoleUrl);
  await context.pages.resourceNaming.assertIdentityOrderAndFocus();
  await context.pages.resourceNaming.deriveAgentName('Café / Welcome Agent', 'cafe-welcome-agent');
  await context.pages.resourceNaming.overrideAgentName('manual-welcome-agent', 'Café / Welcome Agent renamed');
  await context.checkpoint({
    name: Checkpoints.resourceNaming.agentIdentity,
    page: context.pages.page,
    target: context.pages.resourceNaming.agentIdentity,
  });
  await context.pages.agentEditor.configureBehavior({
    instructions: 'Welcome the user and answer concisely.',
    modelProfile: ['default:missing-profile', 'default:reasoning-default'],
    runtimeProfile: 'default:maf-builtin',
  });
  await context.pages.agentEditor.createAndDeploy('manual-welcome-agent');

  await context.pages.resourceNaming.openParameter(context.consoleUrl);
  await context.pages.resourceNaming.deriveParameterName('Retry.Count / bounded', 'retry.count-bounded');
  await context.checkpoint({
    name: Checkpoints.resourceNaming.parameterDerived,
    page: context.pages.page,
    target: context.pages.resourceNaming.parameterForm,
  });
  await context.pages.resourceNaming.overrideAndClearParameterName('retry.count.prefilled', 'Retry.Count / bounded');
  const parameterUrl = await context.pages.resourceNaming.saveParameter('retry.count.final');
  await context.checkpoint({
    name: Checkpoints.resourceNaming.parameterPersisted,
    page: context.pages.page,
    target: context.pages.resourceNaming.parameterForm,
  });

  await context.pages.resourceNaming.reopenParameter(parameterUrl, 'retry.count.final');
  await context.checkpoint({
    name: Checkpoints.resourceNaming.immutable,
    page: context.pages.page,
    target: context.pages.resourceNaming.parameterForm,
  });

  await context.pages.resourceNaming.openParameter(context.consoleUrl);
  await context.pages.resourceNaming.deriveParameterName('Duplicate retry parameter', 'duplicate-retry-parameter');
  await context.pages.resourceNaming.prepareDuplicateParameter('retry.count.final');
  await context.pages.resourceNaming.saveDuplicateParameter();
  await context.checkpoint({
    name: Checkpoints.resourceNaming.conflict,
    page: context.pages.page,
    target: context.pages.resourceNaming.parameterForm,
  });
};
