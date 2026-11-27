"""Conservative deployment reservations in the dedicated preview scope."""
import os
import re

import preview

CORES = {'Standard_B2ms': 2, 'Standard_B4ms': 4}


class Unavailable(ValueError):
    """A bounded reservation must wait for capacity; it does not authorize extra resources."""


def limit(name, default, maximum):
    value = os.environ.get(name) or str(default)
    preview.require(re.fullmatch(r'[0-9]{1,4}', value) and 1 <= int(value) <= maximum,
                    'Invalid preview capacity limit: ' + name + '.')
    return int(value)


def enforce(request, records, resources):
    prefixes = {item['prefix'] for item in records}
    occupied = set()
    for resource in resources:
        name = resource.get('name', '')
        if re.match(r'^pv-[0-9a-f]{20}(?:-|/)', name):
            prefix = name[:23]
            preview.require(prefix in prefixes, 'Unrecorded preview resources block new capacity; restore ownership before reconciliation.')
            occupied.add(prefix)
    active = {item['record']['request']['environmentId']: item['record']['request']['target']['vmSize']
              for item in records if not item['record']['destroyed'] or item['prefix'] in occupied
              or item['record'].get('deletion', {}).get('completedAt', True) is None}
    active[request['environmentId']] = request['target']['vmSize']
    preview.require(all(size in CORES for size in active.values()), 'Unsupported VM size in capacity inventory.')
    if len(active) > limit('AZURE_PREVIEW_MAX_ENVIRONMENTS', 2, 100):
        raise Unavailable('Preview environment capacity reached; remove an unused preview first.')
    if sum(CORES[size] for size in active.values()) > limit('AZURE_PREVIEW_MAX_VCPUS', 4, 400):
        raise Unavailable('Preview vCPU capacity reached; remove or resize an unused preview first.')
