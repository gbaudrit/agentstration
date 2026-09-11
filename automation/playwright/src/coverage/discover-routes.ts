import fs from 'node:fs/promises';
import path from 'node:path';
import { repositoryRoot } from '../fixtures/repository.js';

export interface DiscoveredRoute { source: string; route: string; }

const routeRoots = ['src/Agentstration.Web', 'src/Agentstration.Workplace.Web'] as const;

export async function discoverProductRoutes(root = repositoryRoot): Promise<readonly DiscoveredRoute[]> {
  const files = (await Promise.all(routeRoots.map(routeRoot => collectFiles(path.join(root, routeRoot)))))
    .flat().filter(file => file.endsWith('.razor') || file.endsWith('.cshtml'));
  const routes: DiscoveredRoute[] = [];
  for (const file of files) {
    const content = await fs.readFile(file, 'utf8');
    for (const match of content.matchAll(/^\s*@page\s+"([^"]+)"/gm)) {
      routes.push({ source: path.relative(root, file).replaceAll(path.sep, '/'), route: match[1] });
    }
  }
  return routes.sort(compareRoute);
}

async function collectFiles(directory: string): Promise<string[]> {
  const entries = await fs.readdir(directory, { withFileTypes: true });
  return (await Promise.all(entries.map(async entry => {
    const target = path.join(directory, entry.name);
    return entry.isDirectory() ? await collectFiles(target) : [target];
  }))).flat();
}

function compareRoute(left: DiscoveredRoute, right: DiscoveredRoute): number {
  return left.source.localeCompare(right.source) || left.route.localeCompare(right.route);
}
