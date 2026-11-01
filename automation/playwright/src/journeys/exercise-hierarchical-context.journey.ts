import { Checkpoints } from '../contracts/checkpoints.js';
import { ExpectedTextByLocale } from '../locales/expected-text.js';
import type { Journey } from './journey.js';

export interface ExerciseHierarchicalContextInput {
  username?: string;
  password?: string;
}

export const exerciseHierarchicalContext: Journey<ExerciseHierarchicalContextInput> = async (context, input) => {
  await context.pages.login.signIn(context.consoleUrl, input.username, input.password);
  const executionFixture = await context.pages.executionScopes.ensureFixture(context.consoleUrl);
  const fixture = {
    agentName: executionFixture.agentName,
    agentUid: executionFixture.agentUid,
    agentGeneration: executionFixture.agentGeneration,
    tenantId: executionFixture.tenantId,
    workspaceId: executionFixture.workspaceId,
    scopes: executionFixture.scopes,
  };
  const hierarchy = context.pages.hierarchicalContext;

  await hierarchy.setAgentAccess(context.consoleUrl, fixture, []);
  await hierarchy.selectInstance();
  await hierarchy.assertHierarchy(fixture);
  await context.checkpoint({
    name: Checkpoints.hierarchicalContext.hierarchy,
    page: context.pages.page,
    target: hierarchy.selector,
  });

  await hierarchy.selectWorkspaceWithKeyboard(fixture.workspaceId, 'default');
  await hierarchy.openAgents(context.consoleUrl);
  await hierarchy.expectAgentVisible(fixture.agentName, false);
  await context.checkpoint({
    name: Checkpoints.hierarchicalContext.defaultDenied,
    page: context.pages.page,
    target: hierarchy.agents,
  });

  await hierarchy.selectInstance();
  await hierarchy.openAgents(context.consoleUrl);
  await hierarchy.expectAgentVisible(fixture.agentName, true);
  await hierarchy.selectTenant(fixture.tenantId);
  await hierarchy.assertContextSurvivesNavigation(context.consoleUrl, fixture.scopes.tenant);

  await hierarchy.setAgentAccess(context.consoleUrl, fixture, ['discover']);
  await hierarchy.selectWorkspaceWithKeyboard(fixture.workspaceId, 'default');
  await hierarchy.openAgents(context.consoleUrl);
  await hierarchy.expectAgentVisible(fixture.agentName, true);
  await hierarchy.expectAgentUseDenied(context.consoleUrl, fixture);
  await context.checkpoint({
    name: Checkpoints.hierarchicalContext.discoverOnly,
    page: context.pages.page,
    target: hierarchy.agents,
  });

  await hierarchy.setAgentAccess(context.consoleUrl, fixture, ['discover', 'use']);
  await hierarchy.expectAgentUseAccepted(context.consoleUrl, fixture);
  await context.checkpoint({
    name: Checkpoints.hierarchicalContext.usable,
    page: context.pages.page,
    target: hierarchy.agents,
  });

  await hierarchy.setAgentAccess(context.consoleUrl, fixture, []);
  await hierarchy.openAgents(context.consoleUrl);
  await hierarchy.expectAgentVisible(fixture.agentName, false);
  await hierarchy.selectInstance();
  await hierarchy.openAgents(context.consoleUrl);
  await hierarchy.expectAgentVisible(fixture.agentName, true);
  await context.checkpoint({
    name: Checkpoints.hierarchicalContext.revoked,
    page: context.pages.page,
    target: hierarchy.agents,
  });

  await hierarchy.assertDirtyContextGuard(
    context.consoleUrl,
    fixture,
    ExpectedTextByLocale['en-US'].contextSelector.confirmDiscard,
  );
  await context.checkpoint({
    name: Checkpoints.hierarchicalContext.dirtyGuard,
    page: context.pages.page,
    target: hierarchy.shell,
  });

  await hierarchy.assertRestrictedMember(context.consoleUrl, fixture);
  await context.checkpoint({
    name: Checkpoints.hierarchicalContext.unauthorized,
    page: context.pages.page,
    target: hierarchy.shell,
  });
};
