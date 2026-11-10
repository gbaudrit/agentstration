import fs from 'node:fs';
import path from 'node:path';
import { expect, test } from '@playwright/test';
import { formAdoptionCoverage } from '../src/coverage/form-adoption-coverage.js';
import { applicationSurfaces } from '../src/coverage/application-surfaces.js';
import { discoverProductRoutes } from '../src/coverage/discover-routes.js';
import { automationRoot, repositoryRoot } from '../src/fixtures/repository.js';

test('every product route is represented exactly once in the coverage catalog @smoke', async () => {
  const discovered = await discoverProductRoutes();
  const catalog = applicationSurfaces.flatMap(surface => surface.routes.map(route => ({ source: surface.source, route }))).sort(compareRoute);
  expect(catalog).toEqual(discovered);
  expect(new Set(catalog.map(route => `${route.source}\n${route.route}`)).size).toBe(catalog.length);
});

test('surface identities and fixture keys are explicit and unique @smoke', () => {
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

test('coverage ownership points to implemented files or a tracking issue @smoke', () => {
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

// Inventory belongs to the implementation; coverage declarations are independent so
// adding a product form cannot silently claim a browser case that does not exist.
test('all-form inventory has explicit field or family regression ownership @smoke', () => {
  const inventory = JSON.parse(fs.readFileSync(path.join(repositoryRoot, 'docs/ux/formulaires-tous-ecrans/inventory.json'), 'utf8')) as { screens: { path: string }[] };
  expect(formAdoptionCoverage.map(value => value.source).sort()).toEqual(inventory.screens.map(value => value.path).sort());
  for (const form of formAdoptionCoverage) {
    expect(form.specifications.length).toBeGreaterThan(0);
    for (const specification of form.specifications) expect(fs.existsSync(path.join(automationRoot, specification))).toBe(true);
    const surfaces = applicationSurfaces.filter(surface => surface.source === form.source);
    if (form.kind === 'server-adapter') {
      expect(form.source).toBe('src/Agentstration.Console.Web/Pages/Login.cshtml');
      continue;
    }
    expect(surfaces.length, form.source).toBeGreaterThan(0);
    for (const surface of surfaces) expect(surface.formContract).toEqual(form);
  }
});
