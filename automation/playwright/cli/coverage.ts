import { applicationSurfaces, summarizeCoverage } from '../src/coverage/application-surfaces.js';

const summary = summarizeCoverage();
const percentage = summary.surfaces === 0 ? 100 : Math.round(summary.coveredSurfaces * 10_000 / summary.surfaces) / 100;

console.log(`Playwright application surface coverage: ${summary.coveredSurfaces}/${summary.surfaces} (${percentage}%)`);
console.log(`Route declarations represented by covered surfaces: ${summary.coveredRoutes}/${summary.routes}`);
console.log('');
console.log('| State | Host | Surface | Routes | Owner | Tracking |');
console.log('|---|---|---|---|---|---|');
for (const surface of applicationSurfaces) {
  const tracking = surface.trackingIssue ? `#${surface.trackingIssue}` : '—';
  console.log(`| ${surface.coverage} | ${surface.host} | ${surface.id} | ${surface.routes.join('<br>')} | ${surface.specification} | ${tracking} |`);
}
