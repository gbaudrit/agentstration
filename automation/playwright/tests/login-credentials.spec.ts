import { expect, test } from '@playwright/test';
import { resolveLoginCredentials } from '../src/fixtures/login-credentials.js';

test('journey credentials override environment credentials', () => {
  expect(resolveLoginCredentials('journey-user', 'journey-password', {
    AGENTSTRATION_USERNAME: 'environment-user',
    AGENTSTRATION_PASSWORD: 'environment-password',
    AGENTSTRATION_CONSOLE_URL: 'https://console.example.test',
  })).toEqual({ username: 'journey-user', password: 'journey-password' });
});

test('environment credentials are used for an external instance', () => {
  expect(resolveLoginCredentials(undefined, undefined, {
    AGENTSTRATION_USERNAME: 'external-user',
    AGENTSTRATION_PASSWORD: 'external-password',
    AGENTSTRATION_CONSOLE_URL: 'https://console.example.test',
  })).toEqual({ username: 'external-user', password: 'external-password' });
});

test('managed Development hosts retain the public fixture credentials', () => {
  expect(resolveLoginCredentials(undefined, undefined, {})).toEqual({ username: 'admin', password: 'admin' });
});

test('credentials must be provided as a complete pair', () => {
  expect(() => resolveLoginCredentials(undefined, undefined, {
    AGENTSTRATION_USERNAME: 'external-user',
  })).toThrow(/must be provided together/);
  expect(() => resolveLoginCredentials('journey-user', undefined, {})).toThrow(/both username and password/);
});

test('external instances do not silently use Development credentials', () => {
  expect(() => resolveLoginCredentials(undefined, undefined, {
    AGENTSTRATION_CONSOLE_URL: 'https://console.example.test',
  })).toThrow(/External Playwright instances require/);
});
