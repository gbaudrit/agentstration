import fs from 'node:fs';
import path from 'node:path';
import { test, expect } from '@playwright/test';
import { collectionManifest, collectionSurfaces } from '../src/coverage/collection-surfaces.js';
import { automationRoot, repositoryRoot } from '../src/fixtures/repository.js';

test('every collection adapter and native renderer has a unique assertion mapping', () => {
  expect(new Set(collectionSurfaces.map(surface => surface.id)).size).toBe(collectionSurfaces.length);
  expect(collectionSurfaces.length).toBe(collectionManifest.frames.length + collectionManifest.specialized.length);
  for (const surface of collectionSurfaces) {
    expect(fs.existsSync(path.join(repositoryRoot, surface.source)), surface.id).toBe(true);
    expect(surface.contract).toBe(surface.renderer === 'CollectionView' ? 'shared-frame' : 'native-applicable');
    for (const file of [surface.pageObject, surface.specification, surface.populatedSpecification,
      surface.identitySpecification, ...surface.domainSpecifications, ...surface.sharedHostSpecifications]) {
      expect(fs.existsSync(path.join(automationRoot, file)), `${surface.id}: ${file}`).toBe(true);
    }
    expect(new Set(surface.routes).size).toBe(surface.routes.length);
  }
});

test('repeated-item source audit has no unclassified functional renderer', () => {
  expect(collectionManifest.sourceAudit.length).toBeGreaterThan(0);
  const inventoried = new Set(collectionSurfaces.map(surface => surface.source));
  for (const source of collectionManifest.sourceAudit) {
    expect(source.reason.trim()).not.toBe('');
    expect(['inventoried', 'non-collection-control']).toContain(source.classification);
    if (source.classification === 'inventoried') expect(inventoried.has(source.source), source.source).toBe(true);
  }
});
