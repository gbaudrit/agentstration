import { expect, test } from '@playwright/test';
import { resolveExternalProductAddresses } from '../src/fixtures/external-product.js';

test('a Console URL selects an external product instance', () => {
  expect(resolveExternalProductAddresses({
    AGENTSTRATION_CONSOLE_URL: ' http://localhost:53400/ ',
  })).toEqual({
    consoleUrl: 'http://localhost:53400',
    workplaceUrl: 'http://localhost:53400',
  });
});

test('an explicit Workplace URL is normalized independently', () => {
  expect(resolveExternalProductAddresses({
    AGENTSTRATION_CONSOLE_URL: 'https://console.example.test/',
    AGENTSTRATION_WORKPLACE_URL: 'https://workplace.example.test///',
  })).toEqual({
    consoleUrl: 'https://console.example.test',
    workplaceUrl: 'https://workplace.example.test',
  });
});

test('a Workplace URL cannot select an instance by itself', () => {
  expect(() => resolveExternalProductAddresses({
    AGENTSTRATION_WORKPLACE_URL: 'https://workplace.example.test',
  })).toThrow(/AGENTSTRATION_CONSOLE_URL/);
});

test('external product URLs must use HTTP', () => {
  expect(() => resolveExternalProductAddresses({
    AGENTSTRATION_CONSOLE_URL: 'file:///agentstration',
  })).toThrow(/http or https/);
});
