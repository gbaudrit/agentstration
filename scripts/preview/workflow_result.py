#!/usr/bin/env python3
"""Render only the public lifecycle result, never provider logs or host configuration."""
import json
import os
from pathlib import Path
import sys

result = json.loads(Path(sys.argv[1]).read_text())
with open(os.environ['GITHUB_OUTPUT'], 'a') as output:
    for name, value in {'result': json.dumps(result), 'state': result['state'], 'url': result['url'] or '',
                        'resource-ids': json.dumps(result['providerResourceIds'])}.items():
        output.write(name + '=' + value + '\n')
with open(os.environ['GITHUB_STEP_SUMMARY'], 'a') as summary:
    summary.write('Preview `' + result['environmentId'] + '`: **' + result['state'] + '**\n\n')
    summary.write('Expiry: ' + (result['expiresAt'] or '(none)') + '\n\n')
    summary.write('Azure ingress is closed. A public URL requires the protected access adapter (#1285).\n')
    if result['state'] == 'destroyed' and result['providerResourceIds'].get('resources'):
        summary.write('Preserved resources remain billable (disks/public IP). Explicit destroy with dataMode reset removes the owned infrastructure.\n')
