#!/usr/bin/env python3
"""Root-only Azure guest transport; reuse the existing owned Docker lifecycle."""
import argparse
import base64
import copy
import json
import os
from pathlib import Path
import subprocess
import time

import contract
import extensions
import lifecycle
import preview
import access_host

GUEST_ROOT = Path('/srv/agentstration-preview')
REGISTRY = 'localhost:5001'
REGISTRY_NAME = 'agentstration-preview-transfer'


def local_request(request):
    value = copy.deepcopy(request)
    value['provider'] = 'docker'
    value['registry'] = REGISTRY
    value['target'] = {'port': 5080, 'daemonId': lifecycle.docker('info', '--format', '{{.ID}}')}
    return contract.validate(value)


def run(action, request, registry_image=None, root=GUEST_ROOT, access_config=None, access_owner=None, extension_configuration=None):
    contract.operation(action, request)
    preview.require(request['provider'] == 'azure-vm', 'The guest accepts Azure requests only.')
    settings = extensions.configuration(extension_configuration or {}, request.get('extensions', {})) if action == 'deploy' else {}
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
    route_file = root / 'access.json'
    route = preview.read_json(route_file) if route_file.exists() else None
    if route:
        preview.require(route['ownerId'] == access_owner, 'Guest access ownership differs from the provider record.')
    if access_config:
        preview.require(action == 'deploy' and access_config['ownerId'] == access_owner, 'Invalid guest connector operation.')
        route = {key: access_config[key] for key in ('ownerId', 'hostname')}
    if route:
        os.environ['PREVIEW_ALLOWED_HOSTS'] = 'localhost;127.0.0.1;server;' + route['hostname']
        os.environ['PREVIEW_FORWARDED_HEADERS'] = 'true'
        if action in ('deploy', 'destroy'):
            access_host.stop(root, route['ownerId'])
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
            for name, image in contract.images(selected).items():
                if 'sourceSha' not in image:
                    continue
                reference = contract.images(selected)[name]['reference']
                subprocess.run(['skopeo', 'copy', '--all', '--preserve-digests', '--retry-times', '5',
                                '--dest-tls-verify=false', 'oci-archive:' + str(root / 'incoming' / (name + '.oci.tar')),
                                'docker://' + reference.split('@')[0] + ':delivery'], check=True, capture_output=True, timeout=600)
            preview.private_write(saved, json.dumps(request) + '\n')
            result = lifecycle.execute(action, selected, root / 'state', extension_configuration=settings)
            if access_config:
                preview.private_write(route_file, json.dumps(route) + '\n')
                access_host.start(root, access_config)
        finally:
            # Never remove a container selected only by a name after a failed create.
            resources = json.loads(lifecycle.docker('inspect', REGISTRY_NAME))
            preview.require(resources[0]['Config']['Labels'].get('io.agentstration.preview.transfer') == request['environmentId'],
                            'Refusing to remove a foreign transfer container.')
            lifecycle.docker('rm', '--force', resources[0]['Id'])
    else:
        result = lifecycle.execute(action, selected, root / 'state')
    verified = []
    if action != 'destroy' and result['state'] == 'running' and (route or request.get('extensions')):
        # Protection checks cannot be disabled through the optional test profile.
        verification = preview.verify(root / 'state' / request['environmentId'])
        verified = verification.get('extensionsVerified', [])
    # Keep the Run Command response below its 4 KiB limit and exclude host secrets/configuration.
    return {'state': result['state'],
            **({'health': result['health']} if action == 'deploy' else {}),
            'observedImages': {key: value for key, value in result['observedImages'].items() if not key.startswith('extension-')},
            'extensionsVerified': verified, 'expiresAt': result['expiresAt'],
            'accessReady': bool(route and action != 'destroy' and access_host.ready(route['ownerId'])),
            'guestOperationSeconds': round(time.monotonic() - started, 1),
            'memoryAvailableKiB': int(next(line.split()[1] for line in Path('/proc/meminfo').read_text().splitlines()
                                          if line.startswith('MemAvailable:')))}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('operation', choices=('deploy', 'status', 'destroy'))
    parser.add_argument('--request', type=Path, required=True)
    parser.add_argument('--registry-image')
    parser.add_argument('--access-owner')
    args = parser.parse_args()
    # A protected download URL is never inherited by the Docker/Compose invocation.
    os.environ.pop('BUNDLE_URL', None)
    # Only this per-tunnel token crosses ARM protected parameters. Remove it before
    # any Docker/Compose subprocess; management and probe credentials never arrive.
    protected = os.environ.pop('ACCESS_CONNECTOR', None)
    connector = json.loads(protected) if protected else None
    encoded_settings = os.environ.pop('EXTENSION_CONFIGURATION', None)
    settings = base64.b64decode(encoded_settings, validate=True).decode() if encoded_settings else '{}'
    result = run(args.operation, contract.loads(args.request.read_text()), args.registry_image,
                 access_config=connector, access_owner=args.access_owner, extension_configuration=settings)
    print('AGENTSTRATION_RESULT=' + json.dumps(result, separators=(',', ':')))


if __name__ == '__main__':
    try:
        main()
    except Exception:
        # Azure retains stdout/stderr; do not leak provider URLs or subprocess output.
        print('Guest operation failed; inspect the protected VM state and bounded health diagnostics.')
        raise SystemExit(1)
