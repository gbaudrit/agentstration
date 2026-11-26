#!/usr/bin/env python3
"""Qualify real AEP selection, Azure-style digest transport and retained enrollment without Azure/models."""
import argparse
import json
from pathlib import Path
import shutil
import tempfile
import urllib.error
import urllib.request
import uuid

import azure_host
import contract
import extensions
import lifecycle
import preview
import preview_smoke


def run(server_path, worker_path, extension_path, output):
    server, worker, extension = [preview.read_json(path) for path in (server_path, worker_path, extension_path)]
    identifier = preview.delivery_entry(extension)['id']
    name = extensions.service(identifier)
    environment = 'aep-smoke-' + uuid.uuid4().hex[:12]
    target = preview.read_json(preview.ROOT / 'deploy/preview/targets/azure-vm.example.json')
    core = contract.plan(server, worker, environment, provider='azure-vm', target=target)
    selected = contract.plan(server, worker, environment, provider='azure-vm', target=target,
                             extension_deliveries={identifier: extension})
    registry = preview.read_json(preview.ROOT / 'deploy/preview/azure/transport.json')['registryImage']
    with tempfile.TemporaryDirectory(prefix='preview-extension-') as temporary:
        root = Path(temporary)
        incoming = root / 'incoming'
        incoming.mkdir()
        for service, path, receipt in zip(('server', 'worker', name), (server_path, worker_path, extension_path), (server, worker, extension)):
            shutil.copyfile(path.parent / receipt['archive'], incoming / (service + '.oci.tar'))
        active = selected
        try:
            first = azure_host.run('deploy', selected, registry, root)
            preview.require(first['state'] == 'running' and first['extensionsVerified'] == [identifier], 'Initial extension deployment failed.')
            directory = root / 'state' / environment
            state = preview.read_json(directory / 'deployment.json')
            original_key = (directory / (name + '.key')).read_bytes()
            instance = preview.compose(directory, 'exec', '-T', name, 'cat', '/var/lib/aep/instance', capture=True).strip()
            uuid.UUID(instance)
            port = preview.compose(directory, 'port', name, '8080', capture=True).strip()
            for headers in ({}, {'Authorization': 'Bearer public-disposable-invalid-key'}):
                try:
                    extensions.request('http://' + port + '/.well-known/aep', headers=headers)
                except urllib.error.HTTPError as error:
                    preview.require(error.code == 401, 'Unexpected anonymous/invalid-key response.')
                else:
                    raise ValueError('Unauthenticated extension discovery was accepted.')
            with urllib.request.urlopen(preview.server_address(directory) + '/bootstrap', timeout=10) as response:
                preview.require(response.status == 200, 'Manual bootstrap must remain available.')
            preview.require(preview.read_json(directory / 'server-configuration.json')['Agentstration']['Bootstrap']['InitialBootstrapEnabled'] is False,
                            'The smoke must exercise enrollment without a privileged fixture bootstrap.')
            # Inspect and remove only the owned extension container; the next deploy recreates it.
            project = 'preview-' + environment
            resources = lifecycle.inventory(project)
            lifecycle.owned(resources, state['deploymentOwnerId'])
            container = next(item for item in resources['containers'] if item['Config']['Labels']['com.docker.compose.service'] == name)
            lifecycle.docker('rm', '--force', container['Id'])
            azure_host.run('deploy', selected, registry, root)
            preview.require(preview.compose(directory, 'exec', '-T', name, 'cat', '/var/lib/aep/instance', capture=True).strip() == instance,
                            'Recreation replaced the persisted extension instance.')
            active = core
            azure_host.run('deploy', core, registry, root)
            resources = lifecycle.inventory(project)
            preview.require(not any(item['Config']['Labels']['com.docker.compose.service'] == name for item in resources['containers']),
                            'Deselection retained a running extension container.')
            preview.require(any(item['Name'].endswith('_' + name) for item in resources['volumes']), 'Deselection deleted extension state.')
            active = selected
            final = azure_host.run('deploy', selected, registry, root)
            preview.require(final['extensionsVerified'] == [identifier]
                            and (directory / (name + '.key')).read_bytes() == original_key
                            and preview.compose(directory, 'exec', '-T', name, 'cat', '/var/lib/aep/instance', capture=True).strip() == instance,
                            'Reactivation did not preserve extension identity and enrollment key.')
            preview.require(final['expiresAt'] == first['expiresAt'], 'Redeployment renewed the expiry anchor.')
            local = azure_host.local_request(selected)
            lifecycle.execute('destroy', {**local, 'dataMode': 'reset'}, root / 'state')
            preview.require(not any(lifecycle.inventory(project).values()), 'Reset retained owned containers, volumes or networks.')
            preview.private_write(output, json.dumps({'passed': True, 'extension': identifier,
                'image': contract.images(selected)[name], 'exactDigestGuestTransport': True,
                'enrolledBeforeManualBootstrap': True, 'anonymousAndWrongKeyRejected': True,
                'recreationAndReactivationPreserveIdentity': True, 'deselectionRetainsState': True,
                'expiryPreserved': True, 'fullResetRemovedResources': True}, indent=2) + '\n')
        except Exception:
            directory = root / 'state' / environment
            if (directory / 'deployment.json').exists():
                keys = [path.read_text() for path in directory.glob('*.key')]
                diagnostic = preview_smoke.failure_diagnostics(directory, keys)
                preview.private_write(output, json.dumps({'passed': False, 'diagnostics': diagnostic}, indent=2) + '\n')
            raise
        finally:
            lifecycle.execute('destroy', {**azure_host.local_request(active), 'dataMode': 'reset'}, root / 'state')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--server-delivery', type=Path, required=True)
    parser.add_argument('--worker-delivery', type=Path, required=True)
    parser.add_argument('--extension-delivery', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    run(args.server_delivery, args.worker_delivery, args.extension_delivery, args.output)
