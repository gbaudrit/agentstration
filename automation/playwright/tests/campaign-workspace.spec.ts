import { expect, test } from '@playwright/test';
import { resolveCampaignWorkspaceName } from '../src/fixtures/campaign-workspace.js';

test('journey workspace overrides the campaign environment', () => {
  expect(resolveCampaignWorkspaceName(' journey-workspace ', {
    AGENTSTRATION_WORKSPACE_NAME: 'environment-workspace',
  })).toBe('journey-workspace');
});

test('campaign workspace can be selected from the environment', () => {
  expect(resolveCampaignWorkspaceName(undefined, {
    AGENTSTRATION_WORKSPACE_NAME: ' environment-workspace ',
  })).toBe('environment-workspace');
});

test('workspace selection remains optional without campaign configuration', () => {
  expect(resolveCampaignWorkspaceName(undefined, {})).toBeUndefined();
});

test('an explicit empty journey workspace is rejected', () => {
  expect(() => resolveCampaignWorkspaceName('   ', {
    AGENTSTRATION_WORKSPACE_NAME: 'environment-workspace',
  })).toThrow(/must not be empty/);
});
