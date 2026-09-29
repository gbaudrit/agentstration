import { readFile, readdir } from 'node:fs/promises';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const scriptDirectory = dirname(fileURLToPath(import.meta.url));
const decisionsDirectory = resolve(scriptDirectory, '..', '..', 'decisions');
const indexPath = resolve(decisionsDirectory, 'index.md');
const decisionFiles = (await readdir(decisionsDirectory))
  .filter((file) => file.endsWith('.md') && file !== 'index.md')
  .sort();
const index = await readFile(indexPath, 'utf8');
const errors = [];
const identifiers = new Map();

for (const file of decisionFiles) {
  const filenameMatch = /^(\d{4})-.+\.md$/.exec(file);
  if (!filenameMatch) {
    errors.push(`${file}: filename must start with a four-digit ADR identifier`);
    continue;
  }

  const contents = await readFile(resolve(decisionsDirectory, file), 'utf8');
  const headingMatch = /^# ADR-(\d{4})(?::| —)/m.exec(contents);
  if (!headingMatch) {
    errors.push(`${file}: first ADR heading must use '# ADR-NNNN: …' or '# ADR-NNNN — …'`);
    continue;
  }

  const filenameIdentifier = filenameMatch[1];
  const headingIdentifier = headingMatch[1];
  if (filenameIdentifier !== headingIdentifier) {
    errors.push(`${file}: heading ADR-${headingIdentifier} does not match filename ADR-${filenameIdentifier}`);
  }

  const filesForIdentifier = identifiers.get(headingIdentifier) ?? [];
  filesForIdentifier.push(file);
  identifiers.set(headingIdentifier, filesForIdentifier);
}

for (const [identifier, files] of identifiers) {
  if (files.length > 1) {
    errors.push(`ADR-${identifier}: duplicate identifier used by ${files.join(', ')}`);
  }
}

const indexEntries = [...index.matchAll(/\[ADR-(\d{4})[^\]]*\]\((\d{4}-[^)]+\.md)\)/g)].map((match) => ({
  identifier: match[1],
  file: match[2],
}));
const indexLinks = indexEntries.map((entry) => entry.file);
for (const entry of indexEntries) {
  const filenameIdentifier = entry.file.slice(0, 4);
  if (entry.identifier !== filenameIdentifier) {
    errors.push(`index.md: ADR-${entry.identifier} label points to ADR-${filenameIdentifier} file ${entry.file}`);
  }
}

for (const file of decisionFiles) {
  const occurrences = indexLinks.filter((link) => link === file).length;
  if (occurrences !== 1) {
    errors.push(`${file}: ADR index must link this decision exactly once (found ${occurrences})`);
  }
}

for (const link of new Set(indexLinks)) {
  if (!decisionFiles.includes(link)) {
    errors.push(`index.md: link target does not exist: ${link}`);
  }
}

if (errors.length > 0) {
  console.error(`ADR validation failed with ${errors.length} error(s):`);
  for (const error of errors) {
    console.error(`- ${error}`);
  }
  process.exitCode = 1;
} else {
  console.log(`Validated ${decisionFiles.length} ADRs: identifiers are unique and the index is complete.`);
}
