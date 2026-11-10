import fs from 'node:fs';
import path from 'node:path';
import { repositoryRoot } from '../fixtures/repository.js';
import { applicationSurfaces } from './application-surfaces.js';

interface ImplementationCollection {
  id: string;
  source: string;
  host: 'console' | 'workplace';
  routes: string[];
  renderer: string;
}
interface CollectionManifest {
  frames: ImplementationCollection[];
  specialized: ImplementationCollection[];
  sourceAudit: Array<{ source: string; classification: string; reason: string }>;
}

export const collectionManifest: CollectionManifest = JSON.parse(fs.readFileSync(
  path.join(repositoryRoot, 'docs/ux/collection-surfaces-1121.json'), 'utf8',
));

/** Source/route reconciliation is an assertion map, not an automatic coverage claim. */
export const collectionSurfaces = [...collectionManifest.frames, ...collectionManifest.specialized].map(surface => ({
  ...surface,
  contract: surface.renderer === 'CollectionView' ? 'shared-frame' : 'native-applicable',
  pageObject: 'src/pages/collection-contract.page.ts',
  specification: 'tests/collection-contract.spec.ts',
  populatedSpecification: 'tests/collection-workflows.spec.ts',
  identitySpecification: 'tests/collection-long-identities.spec.ts',
  domainSpecifications: [...new Set(applicationSurfaces.filter(routeSurface =>
    routeSurface.coverage !== 'planned' && routeSurface.host === surface.host && (routeSurface.source === surface.source
      || routeSurface.routes.some(route => surface.routes.includes(route))),
  ).map(routeSurface => routeSurface.specification))],
  preexistingTrackingIssues: [...new Set(applicationSurfaces.filter(routeSurface =>
    routeSurface.host === surface.host && routeSurface.trackingIssue !== undefined
      && (routeSurface.source === surface.source || routeSurface.routes.some(route => surface.routes.includes(route))),
  ).map(routeSurface => routeSurface.trackingIssue!))],
  // Shared shell widgets/timelines do not have their own route declaration.
  sharedHostSpecifications: surface.routes.length ? [] : [surface.host === 'workplace'
    ? 'tests/workplace.spec.ts' : 'tests/console-administration.spec.ts'],
}));
