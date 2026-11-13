import fs from 'node:fs';
import path from 'node:path';
import { test, expect } from '@playwright/test';
import { collectionManifest, collectionSurfaces } from '../src/coverage/collection-surfaces.js';
import { automationRoot, repositoryRoot } from '../src/fixtures/repository.js';

test('every collection adapter and native renderer has a unique assertion mapping @smoke', () => {
  expect(new Set(collectionSurfaces.map(surface => surface.id)).size).toBe(collectionSurfaces.length);
  expect(collectionSurfaces.length).toBe(collectionManifest.frames.length + collectionManifest.specialized.length);
  for (const surface of collectionSurfaces) {
    expect(fs.existsSync(path.join(repositoryRoot, surface.source)), surface.id).toBe(true);
    expect(surface.contract).toBe(surface.mounting === 'unmounted-legacy' ? 'unmounted-legacy' : surface.renderer === 'CollectionView' ? 'shared-frame' : 'native-applicable');
    if (!surface.routes.length && surface.mounting !== 'unmounted-legacy')
      expect(surface.sharedHostSpecifications.length, `${surface.id}: explicit live host mapping`).toBeGreaterThan(0);
    if (surface.mounting === 'unmounted-legacy') expect(surface.sharedHostSpecifications).toEqual([]);
    for (const file of [surface.pageObject, surface.specification, surface.populatedSpecification,
      surface.identitySpecification, ...surface.domainSpecifications, ...surface.sharedHostSpecifications]) {
      expect(fs.existsSync(path.join(automationRoot, file)), `${surface.id}: ${file}`).toBe(true);
    }
    expect(new Set(surface.routes).size).toBe(surface.routes.length);
  }
});

test('repeated-item source audit has no unclassified functional renderer @smoke', () => {
  expect(collectionManifest.sourceAudit.length).toBeGreaterThan(0);
  const inventoried = new Set(collectionSurfaces.map(surface => surface.source));
  for (const source of collectionManifest.sourceAudit) {
    expect(source.reason.trim()).not.toBe('');
    expect(['inventoried', 'non-collection-control']).toContain(source.classification);
    if (source.classification === 'inventoried') expect(inventoried.has(source.source), source.source).toBe(true);
  }
});


test('new repeated-item sources and shared frames cannot silently escape the inventory @smoke', () => {
  const roots = ['Agentstration.Console.Components', 'Agentstration.Web.Components', 'Agentstration.Web.FlowDesigner',
    'Agentstration.Workplace.Components', 'Agentstration.Workplace.Web', 'Agentstration.Web/Pages'];
  const audited = new Set([...collectionManifest.sourceAudit.map(item => item.source), ...collectionSurfaces.map(item => item.source)]);
  const discoveredFrames: Array<{ source: string; itemType: string }> = [];
  for (const root of roots) {
    for (const file of files(path.join(repositoryRoot, 'src', root))) {
      if (!/\.(razor|cshtml)$/.test(file)) continue;
      const source = path.relative(repositoryRoot, file).replaceAll(path.sep, '/');
      const markup = fs.readFileSync(file, 'utf8');
      if (markup.includes('@foreach')) expect(audited.has(source), source).toBe(true);
      for (const frame of markup.matchAll(/<CollectionView\b[^>]*\bTItem="([^"]+)"/g))
        discoveredFrames.push({ source, itemType: frame[1] });
    }
  }
  const expected = collectionManifest.frames.map(item => ({ source: item.source, itemType: (item as typeof item & { itemType: string }).itemType }));
  const sort = (left: typeof expected[number], right: typeof expected[number]) =>
    left.source.localeCompare(right.source) || left.itemType.localeCompare(right.itemType);
  expect(discoveredFrames.sort(sort)).toEqual(expected.sort(sort));
});

function* files(directory: string): Generator<string> {
  for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
    if (entry.name === 'bin' || entry.name === 'obj') continue;
    const file = path.join(directory, entry.name);
    if (entry.isDirectory()) yield* files(file);
    else if (entry.isFile()) yield file;
  }
}
