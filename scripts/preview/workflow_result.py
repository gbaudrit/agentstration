#!/usr/bin/env python3
"""Render only the public lifecycle result, never provider logs or host configuration."""
import json
import os
from pathlib import Path
import sys

def render(result):
    lines = ['Preview `' + result['environmentId'] + '`: **' + result['state'] + '**',
             'Source: `' + result['sourceSha'] + '`; database: `' + result['database'] + '`.',
             'Expiry: ' + (result['expiresAt'] or '(none)'),
             'URL: ' + (result['url'] or 'withheld — Azure ingress is closed until protected access (#1285).')]
    lines += [name + ': `' + image['reference'] + '`' for name, image in result['images'].items()]
    if result.get('deletion'):
        deletion = result['deletion']
        lines += ['Deletion requested: ' + deletion['requestedAt'],
                  'Deletion completed: ' + (deletion['completedAt'] or '(pending)'),
                  'Deletion reason: ' + deletion['reason']]
    identifiers = result['providerResourceIds']
    lines += [name + ': `' + identifiers[name] + '`' for name in ('subscription', 'resourceGroup') if name in identifiers]
    lines += ['Resource: `' + identifier + '`' for identifier in identifiers.get('resources', [])]
    if result['state'] == 'destroyed' and identifiers.get('resources'):
        lines.append('Preserved resources remain billable (disks/public IP). Explicit destroy with dataMode reset removes the owned infrastructure.')
    return '\n\n'.join(lines) + '\n'


def main():
    result = json.loads(Path(sys.argv[1]).read_text())
    with open(os.environ['GITHUB_OUTPUT'], 'a') as output:
        for name, value in {'result': json.dumps(result), 'state': result['state'], 'url': result['url'] or '',
                            'resource-ids': json.dumps(result['providerResourceIds'])}.items():
            output.write(name + '=' + value + '\n')
    with open(os.environ['GITHUB_STEP_SUMMARY'], 'a') as summary:
        summary.write(render(result))


if __name__ == '__main__':
    main()
