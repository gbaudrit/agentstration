import fs from 'node:fs';
import path from 'node:path';
import { expect, test } from '@playwright/test';
import { applicationSurfaces } from '../src/coverage/application-surfaces.js';
import { discoverProductRoutes } from '../src/coverage/discover-routes.js';
import { automationRoot, repositoryRoot } from '../src/fixtures/repository.js';

test('every product route is represented exactly once in the coverage catalog', async () => {
  const discovered = await discoverProductRoutes();
  const catalog = applicationSurfaces.flatMap(surface => surface.routes.map(route => ({ source: surface.source, route }))).sort(compareRoute);
  expect(catalog).toEqual(discovered);
  expect(new Set(catalog.map(route => `${route.source}\n${route.route}`)).size).toBe(catalog.length);
});

test('surface identities and fixture keys are explicit and unique', () => {
  expect(new Set(applicationSurfaces.map(surface => surface.id)).size).toBe(applicationSurfaces.length);
  for (const surface of applicationSurfaces) {
    expect(surface.id).toMatch(/^[a-z0-9]+(?:-[a-z0-9]+)*$/);
    expect(surface.routes.length).toBeGreaterThan(0);
    expect(new Set(surface.fixtureKeys).size).toBe(surface.fixtureKeys.length);
    for (const route of surface.routes) {
      const parameters = [...route.matchAll(/{([^}:]+)(?::[^}]+)?}/g)].map(match => match[1]);
      for (const parameter of parameters) {
        expect(surface.fixtureKeys.some(key => key.toLocaleLowerCase() === parameter.toLocaleLowerCase())).toBe(true);
      }
    }
  }
});

test('coverage ownership points to implemented files or a tracking issue', () => {
  for (const surface of applicationSurfaces) {
    expect(fs.existsSync(path.join(repositoryRoot, surface.source))).toBe(true);
    if (surface.coverage !== 'covered') {
      expect(surface.trackingIssue).toBeGreaterThan(0);
      if (surface.coverage === 'partial') {
        expect(fs.existsSync(path.join(automationRoot, surface.pageObject))).toBe(true);
        expect(fs.existsSync(path.join(automationRoot, surface.specification))).toBe(true);
      }
      continue;
    }
    expect(surface.trackingIssue).toBeUndefined();
    expect(fs.existsSync(path.join(automationRoot, surface.pageObject))).toBe(true);
    expect(fs.existsSync(path.join(automationRoot, surface.specification))).toBe(true);
  }
});

function compareRoute(left: { source: string; route: string }, right: { source: string; route: string }): number {
  return left.source.localeCompare(right.source) || left.route.localeCompare(right.route);
}
