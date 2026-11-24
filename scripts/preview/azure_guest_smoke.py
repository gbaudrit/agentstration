#!/usr/bin/env python3
"""Exercise the Azure guest's exact OCI transport and existing Compose lifecycle without Azure."""
import argparse
import json
from pathlib import Path
import shutil
import tempfile
import uuid

import azure_host
import contract
import lifecycle
import preview


def smoke(server_path, worker_path, database, output):
    receipts = [preview.read_json(path) for path in (server_path, worker_path)]
    value = contract.plan(*receipts, 'guest-' + uuid.uuid4().hex[:12], provider='azure-vm', database=database,
                          target=preview.read_json(preview.ROOT / 'deploy/preview/targets/azure-vm.example.json'))
    image = preview.read_json(preview.ROOT / 'deploy/preview/azure/transport.json')['registryImage']
    with tempfile.TemporaryDirectory(prefix='agentstration-azure-guest-') as temporary:
        root = Path(temporary)
        incoming = root / 'incoming'
        incoming.mkdir()
        for name, path, receipt in zip(('server', 'worker'), (server_path, worker_path), receipts):
            shutil.copyfile(path.parent / receipt['archive'], incoming / (name + '.oci.tar'))
        try:
            first = azure_host.run('deploy', value, image, root)
            preview.require(first['state'] == 'running', 'Guest never became healthy.')
            preview.require(azure_host.run('status', value, root=root)['state'] == 'running', 'Guest status drifted.')
            azure_host.run('destroy', value, root=root)
            # A legacy v1 guest must accept the now-explicit equivalent OS profile.
            value['target']['osProfile'] = contract.AZURE_OS_PROFILE
            second = azure_host.run('deploy', value, image, root)
            preview.require(first['expiresAt'] == second['expiresAt'], 'Guest retry renewed expiry.')
            for name in ('server', 'worker'):
                preview.require(second['observedImages'][name].endswith('@' + value['deliveries'][name]['digest']), 'Guest digest changed during transport.')
            evidence = {'database': database, 'state': 'passed', 'exactDigestTransport': True, 'preserveRecreate': True,
                        'expiryPreserved': True, 'legacyOsProfileCompatible': True, 'observedImages': second['observedImages'],
                        'memoryAvailableKiB': second['memoryAvailableKiB'], 'guestOperationSeconds': second['guestOperationSeconds']}
            preview.private_write(output, json.dumps(evidence, indent=2) + '\n')
        finally:
            selected = azure_host.local_request(value)
            selected['dataMode'] = 'reset'
            lifecycle.execute('destroy', selected, root / 'state')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--server-delivery', type=Path, required=True)
    parser.add_argument('--worker-delivery', type=Path, required=True)
    parser.add_argument('--database', choices=('sqlite', 'postgresql'), required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    smoke(args.server_delivery, args.worker_delivery, args.database, args.output)
