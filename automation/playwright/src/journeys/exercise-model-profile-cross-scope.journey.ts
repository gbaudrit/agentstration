import { Checkpoints } from '../contracts/checkpoints.js';
import type { Journey } from './journey.js';

export interface ExerciseModelProfileCrossScopeInput {
  username?: string;
  password?: string;
}

export const exerciseModelProfileCrossScope: Journey<ExerciseModelProfileCrossScopeInput> = async (
  context,
  input,
) => {
  await context.pages.login.signIn(context.consoleUrl, input.username, input.password);
  const baseFixture = await context.pages.executionScopes.ensureFixture(context.consoleUrl);
  const profile = context.pages.modelProfileCrossScope;
  const fixture = await profile.ensureFixture(context.consoleUrl, baseFixture);

  await profile.resetWorkspaceAgents(context.consoleUrl, fixture);
  await profile.ensureWorkspaceAgent(context.consoleUrl, fixture);
  await profile.openUsages(context.consoleUrl, fixture);
  await profile.assertImpactCount(2);
  await profile.assertUsages([fixture.agentName, profile.primaryWorkspaceAgentName]);
  await context.checkpoint({
    name: Checkpoints.modelProfileCrossScope.usages,
    page: context.pages.page,
    target: profile.usagesPanel,
  });

  await profile.assertNewTabIsolation(fixture);
  await profile.openWorkspaceUsage(fixture);
  await context.checkpoint({
    name: Checkpoints.modelProfileCrossScope.workspaceAgent,
    page: context.pages.page,
  });
  await profile.assertReloadKeepsWorkspaceContext(fixture);
  await profile.returnToProfileUsages(fixture);
  await profile.assertUsages([fixture.agentName, profile.primaryWorkspaceAgentName]);
  await profile.assertHistoryRoundTrip(fixture);
  await context.checkpoint({
    name: Checkpoints.modelProfileCrossScope.restored,
    page: context.pages.page,
    target: profile.usagesPanel,
  });

  await profile.deleteWorkspaceAgent(context.consoleUrl, fixture);
  await profile.openUsages(context.consoleUrl, fixture);
  await profile.assertImpactCount(1);
  await profile.assertUsages([fixture.agentName], [profile.primaryWorkspaceAgentName]);
  await context.checkpoint({
    name: Checkpoints.modelProfileCrossScope.updated,
    page: context.pages.page,
    target: profile.usagesPanel,
  });

  await profile.ensureWorkspaceAgent(context.consoleUrl, fixture);
  await profile.ensureWorkspaceAgent(
    context.consoleUrl,
    fixture,
    `/workspaces/${fixture.siblingWorkspaceId}`,
    profile.hiddenSiblingAgentName,
  );
  await profile.assertRestrictedVisibility(context.consoleUrl, fixture);
  await context.checkpoint({
    name: Checkpoints.modelProfileCrossScope.isolated,
    page: context.pages.page,
    target: profile.usagesPanel,
  });
};
