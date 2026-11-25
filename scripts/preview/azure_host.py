#!/usr/bin/env python3
"""Root-only Azure guest transport; reuse the existing owned Docker lifecycle."""
import argparse
import copy
import json
import os
from pathlib import Path
import subprocess
import time

import contract
import lifecycle
import preview

GUEST_ROOT = Path('/srv/agentstration-preview')
REGISTRY = 'localhost:5001'
REGISTRY_NAME = 'agentstration-preview-transfer'


def local_request(request):
    value = copy.deepcopy(request)
    value['provider'] = 'docker'
    value['registry'] = REGISTRY
    value['target'] = {'port': 5080, 'daemonId': lifecycle.docker('info', '--format', '{{.ID}}')}
    return contract.validate(value)


def run(action, request, registry_image=None, root=GUEST_ROOT):
    contract.operation(action, request)
    preview.require(request['provider'] == 'azure-vm', 'The guest accepts Azure requests only.')
    saved = root / 'azure-request.json'
    if saved.exists():
        previous = contract.loads(saved.read_text())
        preview.require(previous['environmentId'] == request['environmentId']
                        and {k: v for k, v in contract.azure_target(previous).items() if k != 'vmSize'}
                        == {k: v for k, v in contract.azure_target(request).items() if k != 'vmSize'},
                        'The guest belongs to another Azure target.')
        if action != 'deploy':
            preview.require(contract.images(previous) == contract.images(request), 'Stale guest image selection.')
    else:
        preview.require(action == 'deploy', 'Guest tooling is not initialized; inspect provisioning before retrying.')
    selected = local_request(request)
    started = time.monotonic()
    if action == 'deploy':
        # Host-only loopback mirror. No registry credential reaches the VM or its containers.
        preview.require(registry_image and '@sha256:' in registry_image, 'Pin the transfer registry image.')
        existing = lifecycle.docker('ps', '--all', '--filter', 'name=^/' + REGISTRY_NAME + '$', '--format', '{{.ID}}')
        if existing:
            old = json.loads(lifecycle.docker('inspect', existing))[0]
            preview.require(old['Config'].get('Labels', {}).get('io.agentstration.preview.transfer') == request['environmentId'],
                            'Refusing to replace a foreign transfer container.')
            lifecycle.docker('rm', '--force', old['Id'])
        lifecycle.docker('run', '--detach', '--name', REGISTRY_NAME,
                         '--label', 'io.agentstration.preview.transfer=' + request['environmentId'],
                         '--publish', '127.0.0.1:5001:5000', registry_image)
        try:
            for name in ('server', 'worker'):
                reference = contract.images(selected)[name]['reference']
                subprocess.run(['skopeo', 'copy', '--all', '--preserve-digests', '--retry-times', '5',
                                '--dest-tls-verify=false', 'oci-archive:' + str(root / 'incoming' / (name + '.oci.tar')),
                                'docker://' + reference.split('@')[0] + ':delivery'], check=True, capture_output=True, timeout=600)
            preview.private_write(saved, json.dumps(request) + '\n')
            result = lifecycle.execute(action, selected, root / 'state')
        finally:
            # Never remove a container selected only by a name after a failed create.
            resources = json.loads(lifecycle.docker('inspect', REGISTRY_NAME))
            preview.require(resources[0]['Config']['Labels'].get('io.agentstration.preview.transfer') == request['environmentId'],
                            'Refusing to remove a foreign transfer container.')
            lifecycle.docker('rm', '--force', resources[0]['Id'])
    else:
        result = lifecycle.execute(action, selected, root / 'state')
    # Keep the Run Command response below its 4 KiB limit and exclude host secrets/configuration.
    return {'state': result['state'], 'observedImages': result['observedImages'], 'expiresAt': result['expiresAt'],
            'guestOperationSeconds': round(time.monotonic() - started, 1),
            'memoryAvailableKiB': int(next(line.split()[1] for line in Path('/proc/meminfo').read_text().splitlines()
                                          if line.startswith('MemAvailable:')))}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('operation', choices=('deploy', 'status', 'destroy'))
    parser.add_argument('--request', type=Path, required=True)
    parser.add_argument('--registry-image')
    args = parser.parse_args()
    # A protected download URL is never inherited by the Docker/Compose invocation.
    os.environ.pop('BUNDLE_URL', None)
    result = run(args.operation, contract.loads(args.request.read_text()), args.registry_image)
    print('AGENTSTRATION_RESULT=' + json.dumps(result, separators=(',', ':')))


if __name__ == '__main__':
    try:
        main()
    except Exception:
        # Azure retains stdout/stderr; do not leak provider URLs or subprocess output.
        print('Guest operation failed; inspect the protected VM state and bounded health diagnostics.')
        raise SystemExit(1)
