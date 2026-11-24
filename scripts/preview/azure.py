"""Azure VM adapter. ARM/OIDC and temporary OCI transport stay outside Agentstration."""
import base64
from contextlib import contextmanager
from datetime import datetime, timedelta, timezone
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import tarfile
import tempfile
import time
import uuid

import contract
import lifecycle
import preview

OWNER = 'agentstration.preview.owner'
ENVIRONMENT = 'agentstration.preview.environment'
REPOSITORY = 'agentstration.preview.repository'
API = '2024-07-01'
# Public infrastructure image, resolved once and pinned in the checked-in transport manifest.
TRANSPORT = preview.ROOT / 'deploy/preview/azure/transport.json'


def error_codes(stderr):
    """Retain bounded Azure codes, never nested messages, targets or parameter values."""
    codes = []

    def collect(value, depth=0):
        if not isinstance(value, dict) or depth > 4 or len(codes) >= 6:
            return
        code = value.get('code')
        if isinstance(code, str) and re.fullmatch(r'[A-Za-z][A-Za-z0-9_.]{0,79}', code) and code not in codes:
            codes.append(code)
        for child in ('error', 'innererror'):
            collect(value.get(child), depth + 1)
        details = value.get('details', [])
        if isinstance(details, list):
            for detail in details[:10]:
                collect(detail, depth + 1)

    match = re.search(r'ERROR:\s*\(([A-Za-z][A-Za-z0-9_.]{0,79})\)', stderr)
    if match:
        codes.append(match[1])
    try:
        value, _ = json.JSONDecoder().raw_decode(stderr.partition('ERROR:')[2].lstrip())
        collect(value)
    except ValueError:
        pass
    return codes


def command(arguments, *, timeout=180):
    completed = subprocess.run(arguments, text=True, capture_output=True, timeout=timeout)
    if completed.returncode:
        context = []
        for argument in arguments[:4]:
            if argument.startswith('-'):
                break
            context.append(argument)
        codes = error_codes(completed.stderr)
        reason = ' [' + ', '.join(codes) + ']' if codes else ''
        raise RuntimeError('Azure preview command failed (' + ' '.join(context) + ')' + reason
                           + '; provider output is withheld to protect credentials.')
    return completed.stdout.strip()


def prefix(request):
    target = request['target']
    identity = '/'.join((target['subscriptionId'].lower(), target['resourceGroup'].lower(), request['environmentId']))
    return 'pv-' + hashlib.sha256(identity.encode()).hexdigest()[:20]


def resource_ids(request):
    target = request['target']
    root = '/subscriptions/' + target['subscriptionId'] + '/resourceGroups/' + target['resourceGroup'] + '/providers/'
    name = prefix(request)
    return {kind: root + provider + '/' + name + suffix for kind, provider, suffix in (
        ('virtualMachine', 'Microsoft.Compute/virtualMachines', '-vm'), ('dataDisk', 'Microsoft.Compute/disks', '-data'),
        ('networkInterface', 'Microsoft.Network/networkInterfaces', '-nic'), ('publicIp', 'Microsoft.Network/publicIPAddresses', '-pip'),
        ('virtualNetwork', 'Microsoft.Network/virtualNetworks', '-vnet'), ('networkSecurityGroup', 'Microsoft.Network/networkSecurityGroups', '-nsg'))}


def verify_vm(request, instance):
    target = request['target']
    preview.require((instance.get('identity') or {}).get('type') in (None, 'None'),
                    'Preview VMs must not expose an Azure managed identity to workloads.')
    preview.require(instance['location'] == target['region'] and instance['hardwareProfile']['vmSize'] == target['vmSize']
                    and instance['storageProfile']['imageReference']['version'] == target['imageVersion'],
                    'Observed Azure VM location, size or OS version differs from the request.')


def tags(request, metadata):
    pr = re.match(r'pr-([0-9]+)(?:-|$)', request['environmentId'])
    return {OWNER: metadata['ownerId'], ENVIRONMENT: request['environmentId'],
            REPOSITORY: request['deliveries']['server']['repository'].lower(),
            'agentstration.preview.sha': request['sourceSha'], 'agentstration.preview.pr': pr[1] if pr else 'none',
            'agentstration.preview.created': metadata['createdAt'], 'agentstration.preview.expires': metadata['expiresAt']}


def inspect_owned(request, metadata, resources):
    """Only deterministic resource IDs in the dedicated RG are candidates for mutation."""
    ids = {value.lower() for value in resource_ids(request).values()}
    vm = resource_ids(request)['virtualMachine'].lower()
    relevant = []
    expected = tags(request, metadata) if metadata else {}
    parent_owned = any(item['id'].lower() == vm and metadata
                       and all((item.get('tags') or {}).get(key) == expected[key] for key in (OWNER, ENVIRONMENT, REPOSITORY))
                       for item in resources)
    for item in resources:
        identifier = item['id'].lower()
        if item['name'].lower().startswith(prefix(request)):
            # Azure creates the OS disk on behalf of the VM; verify its attachment separately.
            if item['name'].lower() == prefix(request) + '-os' and item['type'].lower() == 'microsoft.compute/disks':
                continue
            # Platform-installed Run Command extensions are children of the owned VM.
            if identifier.startswith((vm + '/extensions/', vm + '/runcommands/')):
                preview.require(parent_owned, 'An Azure child resource has no verified owned VM.')
                relevant.append(item)
                continue
            preview.require(identifier in ids,
                            'Unexpected resources occupy this preview namespace; refusing mutation.')
            preview.require(metadata and all((item.get('tags') or {}).get(key) == expected[key]
                                             for key in (OWNER, ENVIRONMENT, REPOSITORY)),
                            'Foreign or unmanaged Azure resources exist; refusing adoption or deletion.')
            relevant.append(item)
    return relevant


def validate_record(request, record, action):
    contract.fields(record, ('schemaVersion', 'ownerId', 'createdAt', 'expiresAt', 'request', 'destroyed', 'initialized'))
    preview.require(record['schemaVersion'] == 1 and type(record['destroyed']) is bool and type(record['initialized']) is bool, 'Invalid Azure ownership metadata.')
    uuid.UUID(record['ownerId'])
    datetime.fromisoformat(record['createdAt'])
    datetime.fromisoformat(record['expiresAt'])
    previous = contract.validate(record['request'])
    previous_target, target = contract.azure_target(previous), contract.azure_target(request)
    preview.require(previous['environmentId'] == request['environmentId'] and previous['provider'] == 'azure-vm', 'Azure ownership identity mismatch.')
    preview.require(previous['deliveries']['server']['repository'].lower() == request['deliveries']['server']['repository'].lower(),
                    'Azure ownership metadata belongs to another repository.')
    fixed = ('subscriptionId', 'resourceGroup') if record['destroyed'] else ('subscriptionId', 'resourceGroup', 'region', 'imageVersion', 'osProfile')
    preview.require(all(previous_target[key] == target[key] for key in fixed),
                    'Existing Azure previews cannot change location, OS image or ownership scope; reset before changing location/OS.')
    if not record['destroyed']:
        preview.require(previous['database'] == request['database'], 'Reset the Azure preview explicitly before changing databases.')
    if action != 'deploy':
        preview.require(contract.images(previous) == contract.images(request) and previous_target == target,
                        'Status/destroy requires the recorded Azure image and target selection.')


class Azure:
    """Actual Azure CLI boundary, with a private state blob and an exclusive provider lease."""
    def __init__(self, request, directory):
        self.request, self.directory = request, Path(directory)
        self.account = os.environ.get('AZURE_PREVIEW_STORAGE_ACCOUNT', '')
        self.container = os.environ.get('AZURE_PREVIEW_STORAGE_CONTAINER', 'previews')
        preview.require(re.fullmatch(r'[a-z0-9]{3,24}', self.account) and re.fullmatch(r'[a-z0-9][a-z0-9-]{1,61}[a-z0-9]', self.container),
                        'Configure a dedicated private Azure staging account/container.')
        self.blob = prefix(request) + '/lifecycle.json'
        self.lease = None
        actual = self.az('account', 'show')
        preview.require(actual['id'].lower() == request['target']['subscriptionId'].lower(), 'Azure login targets another subscription.')
        properties = self.az('storage', 'account', 'show', '--name', self.account,
                             '--resource-group', request['target']['resourceGroup'])
        preview.require(properties.get('allowBlobPublicAccess') is False and properties.get('allowSharedKeyAccess') is False,
                        'Staging must disable public blobs and shared-key authentication.')
        properties = self.storage('container', 'show')
        preview.require(properties['properties'].get('publicAccess') in (None, 'None', 'off'), 'The staging container must be private.')

    def az(self, *args, timeout=180):
        raw = command(['az', *args, '--subscription', self.request['target']['subscriptionId'], '--only-show-errors', '--output', 'json'], timeout=timeout)
        return json.loads(raw) if raw else None

    def storage(self, *args, **kwargs):
        container_option = '--name' if args[0] == 'container' else '--container-name'
        return self.az('storage', *args, '--account-name', self.account, container_option, self.container, '--auth-mode', 'login', **kwargs)

    def read(self):
        if not self.storage('blob', 'exists', '--name', self.blob)['exists']:
            return None
        path = self.directory / 'ownership.json'
        self.storage('blob', 'download', '--name', self.blob, '--file', str(path), '--overwrite', 'true', '--no-progress')
        return preview.read_json(path)

    def write(self, record, *, initial=False):
        path = self.directory / 'ownership.json'
        preview.private_write(path, json.dumps(record) + '\n')
        args = [] if initial else ['--lease-id', self.lease]
        self.storage('blob', 'upload', '--name', self.blob, '--file', str(path), '--overwrite', 'false' if initial else 'true', '--no-progress', *args)

    @contextmanager
    def lock(self):
        # Infinite lease avoids overlapping ARM mutations after runner termination or renewal loss.
        # Normal exit releases it; interrupted-run recovery explicitly breaks the lease after inspection.
        self.lease = self.storage('blob', 'lease', 'acquire', '--blob-name', self.blob, '--lease-duration', '-1')
        try:
            yield
        finally:
            self.storage('blob', 'lease', 'release', '--blob-name', self.blob, '--lease-id', self.lease)
            self.lease = None

    def resources(self, record):
        resources = self.az('resource', 'list', '--resource-group', self.request['target']['resourceGroup'])
        relevant = inspect_owned(self.request, record, resources)
        os_disk = next((r for r in resources if r['name'].lower() == prefix(self.request) + '-os'), None)
        if os_disk:
            properties = self.az('disk', 'show', '--ids', os_disk['id'])
            vm_id = resource_ids(self.request)['virtualMachine']
            preview.require(any(r['id'].lower() == vm_id.lower() for r in relevant)
                            and (properties.get('managedBy') or '').lower() == vm_id.lower(),
                            'An unverified OS disk occupies this namespace; inspect the interrupted ARM deployment.')
        return relevant

    def provision(self, record):
        target = self.request['target']
        # A public throwaway key satisfies Azure's Linux provisioning API; SSH has no ingress.
        key = self.directory / 'unused-ssh-key'
        existing = any(r['id'].lower() == resource_ids(self.request)['virtualMachine'].lower() for r in self.resources(record))
        if existing:
            public_key = self.az('vm', 'show', '--ids', resource_ids(self.request)['virtualMachine'])['osProfile']['linuxConfiguration']['ssh']['publicKeys'][0]['keyData']
        else:
            command(['ssh-keygen', '-q', '-t', 'ed25519', '-N', '', '-f', str(key)])
            public_key = key.with_suffix('.pub').read_text().strip()
            key.unlink()
        parameters = {'location': target['region'], 'resourcePrefix': prefix(self.request), 'ownershipTags': tags(self.request, record),
                      'vmSize': target['vmSize'], 'imageVersion': target['imageVersion'], 'sshPublicKey': public_key}
        path = self.directory / 'parameters.json'
        preview.private_write(path, json.dumps({'parameters': {k: {'value': v} for k, v in parameters.items()}}))
        self.az('deployment', 'group', 'create', '--name', prefix(self.request), '--resource-group', target['resourceGroup'],
                '--template-file', str(preview.ROOT / 'deploy/preview/azure/main.bicep'), '--parameters', '@' + str(path), timeout=1800)
        instance = self.az('vm', 'show', '--ids', resource_ids(self.request)['virtualMachine'])
        verify_vm(self.request, instance)
        self.az('vm', 'start', '--ids', resource_ids(self.request)['virtualMachine'], timeout=600)

    def power(self):
        view = self.az('vm', 'get-instance-view', '--ids', resource_ids(self.request)['virtualMachine'])
        return next((s['code'] for s in view.get('instanceView', {}).get('statuses', []) if s['code'].startswith('PowerState/')), 'unknown')

    def metrics(self):
        try:
            response = self.az('monitor', 'metrics', 'list', '--resource', resource_ids(self.request)['virtualMachine'],
                               '--metric', 'Percentage CPU', 'CPU Credits Remaining', 'CPU Credits Consumed',
                               '--interval', 'PT1M', '--aggregation', 'Average')
            values = {}
            for metric in response.get('value', []):
                samples = [point for series in metric.get('timeseries', []) for point in series.get('data', [])
                           if point.get('average') is not None]
                if samples:
                    values[metric['name']['value']] = {'average': samples[-1]['average'], 'timestamp': samples[-1]['timeStamp']}
            return {'azureCompute': values, 'azureMetricsState': 'available' if values else 'pending'}
        except RuntimeError:
            # Azure Monitor can lag a newly provisioned VM; never fabricate a capacity result.
            return {'azureMetricsState': 'unavailable'}

    def transfer(self, expect_existing=False):
        bundle, checksum = bundle_images(self.request, self.directory)
        name = 'transfer/' + prefix(self.request) + '/' + uuid.uuid4().hex + '.tar.gz'
        try:
            self.storage('blob', 'upload', '--name', name, '--file', str(bundle), '--overwrite', 'false', '--no-progress')
            expires = (datetime.now(timezone.utc) + timedelta(minutes=30)).strftime('%Y-%m-%dT%H:%MZ')
            url = self.storage('blob', 'generate-sas', '--name', name, '--permissions', 'r', '--expiry', expires,
                               '--https-only', '--as-user', '--full-uri')
            return self.run_guest('deploy', url=url, checksum=checksum, expect_existing=expect_existing)
        finally:
            # Staged application bytes are private, temporary, and never become Actions artifacts.
            self.storage('blob', 'delete', '--name', name, '--delete-snapshots', 'include')

    def run_guest(self, action, *, url=None, checksum=None, expect_existing=False):
        properties = {'source': {'script': guest_script(action, self.request, checksum, expect_existing)}, 'timeoutInSeconds': 1800, 'asyncExecution': False,
                      'parameters': [], 'protectedParameters': [{'name': 'BUNDLE_URL', 'value': url}] if url else []}
        path = self.directory / 'run-command.json'
        preview.private_write(path, json.dumps({'location': self.request['target']['region'],
                                              'tags': tags(self.request, self.read()), 'properties': properties}))
        endpoint = 'https://management.azure.com' + resource_ids(self.request)['virtualMachine'] + '/runCommands/preview-lifecycle?api-version=' + API
        try:
            self.az('rest', '--method', 'put', '--url', endpoint, '--body', '@' + str(path), timeout=1900)
            deadline = time.monotonic() + 1800
            while True:
                response = self.az('rest', '--method', 'get', '--url', endpoint + '&$expand=instanceView')
                view = response.get('properties', {}).get('instanceView', {})
                if view.get('executionState') in ('Succeeded', 'Failed', 'Canceled', 'TimedOut'):
                    break
                preview.require(time.monotonic() < deadline, 'Timed out waiting for the Azure guest lifecycle.')
                time.sleep(10)
            preview.require(view.get('executionState') == 'Succeeded' and view.get('exitCode') == 0,
                            'Guest lifecycle failed; inspect the protected VM and Azure Run Command status (no raw logs are exported).')
            lines = view.get('output', '').splitlines()
            marker = [line.removeprefix('AGENTSTRATION_RESULT=') for line in lines if line.startswith('AGENTSTRATION_RESULT=')]
            preview.require(len(marker) == 1, 'Guest returned no bounded lifecycle result.')
            return json.loads(marker[0])
        finally:
            path.unlink(missing_ok=True)
            # Discard the protected download parameter from the ARM resource after execution.
            self.az('rest', '--method', 'delete', '--url', endpoint)

    def reset(self, record):
        for kind in ('virtualMachine', 'networkInterface', 'publicIp', 'virtualNetwork', 'networkSecurityGroup', 'dataDisk'):
            # Re-inspect every time: never delete a resource whose ownership changed.
            resources = self.resources(record)
            identifier = resource_ids(self.request)[kind]
            if any(r['id'].lower() == identifier.lower() for r in resources):
                api = API if kind == 'virtualMachine' else '2024-03-02' if kind == 'dataDisk' else '2024-05-01'
                self.az('resource', 'delete', '--ids', identifier, '--api-version', api, timeout=900)


def bundle_images(request, directory):
    """CI exports the exact registry index + attestations. No rebuild or credentials in the bundle."""
    path = Path(directory) / 'bundle'
    path.mkdir(mode=0o700)
    preview.private_write(path / 'request.json', json.dumps(request) + '\n')
    for name in ('server', 'worker'):
        auth = os.environ.get('DOCKER_CONFIG')
        credentials = ['--src-authfile', str(Path(auth) / 'config.json')] if auth else []
        command(['skopeo', 'copy', '--all', '--preserve-digests', '--retry-times', '5', *credentials,
                 'docker://' + contract.images(request)[name]['reference'], 'oci-archive:' + str(path / (name + '.oci.tar'))], timeout=900)
    bundle = Path(directory) / 'transfer.tar.gz'
    with tarfile.open(bundle, 'w:gz') as archive:
        for file in sorted(path.iterdir()):
            archive.add(file, arcname='incoming/' + file.name)
        for relative in ('scripts/packaging/images.py', 'deploy/preview/compose.yml', 'deploy/preview/postgresql.yml',
                         'deploy/preview/postgresql.json', 'deploy/preview/azure/transport.json'):
            archive.add(preview.ROOT / relative, arcname='tooling/' + relative)
        for file in sorted((preview.ROOT / 'scripts/preview').glob('*.py')):
            if not file.name.startswith('test_'):
                archive.add(file, arcname='tooling/scripts/preview/' + file.name)
    with bundle.open('rb') as stream:
        checksum = hashlib.file_digest(stream, 'sha256').hexdigest()
    return bundle, checksum


def guest_script(action, request, checksum=None, expect_existing=False):
    preview.require(action in ('deploy', 'status', 'destroy'), 'Unsupported guest operation.')
    encoded = base64.b64encode(json.dumps(request).encode()).decode()
    script = '#!/bin/bash\nset -euo pipefail\numask 077\n'
    if action == 'deploy':
        preview.require(isinstance(checksum, str) and re.fullmatch('[0-9a-f]{64}', checksum), 'Invalid bundle checksum.')
        script += '''cloud-init status --wait >/dev/null 2>&1
test -d /srv/agentstration-preview
mountpoint -q /srv/agentstration-preview
python3 - <<'DOWNLOAD'
import hashlib, os, pathlib, tarfile, urllib.request
root = pathlib.Path('/srv/agentstration-preview')
path = root / 'transfer.tar.gz'
try:
    with urllib.request.urlopen(os.environ.pop('BUNDLE_URL'), timeout=120) as response, path.open('wb') as output:
        while block := response.read(1024 * 1024):
            output.write(block)
    with path.open('rb') as stream:
        if hashlib.file_digest(stream, 'sha256').hexdigest() != 'CHECKSUM':
            raise ValueError('Bundle checksum mismatch')
    with tarfile.open(path) as archive:
        archive.extractall(root, filter='data')
except Exception:
    print('Private image transfer failed; no provider URL is logged.')
    raise SystemExit(1)
finally:
    path.unlink(missing_ok=True)
DOWNLOAD
unset BUNDLE_URL
'''.replace('CHECKSUM', checksum)
    if expect_existing:
        script += 'test -f /srv/agentstration-preview/azure-request.json\n'
        script += 'test -f /srv/agentstration-preview/state/' + request['environmentId'] + '/lifecycle.json\n'
    script += "printf '%s' '" + encoded + "' | base64 --decode > /srv/agentstration-preview/request.json\n"
    script += 'python3 /srv/agentstration-preview/tooling/scripts/preview/azure_host.py ' + action + ' --request /srv/agentstration-preview/request.json'
    if action == 'deploy':
        # Registry digest is checked in, not supplied by a workload/ref or mutable tag.
        image = preview.read_json(TRANSPORT)['registryImage']
        preview.require(re.fullmatch(r'docker.io/library/registry:3@sha256:[0-9a-f]{64}', image), 'Pin the public transfer registry image.')
        script += ' --registry-image ' + image
    script += '\n'
    if action == 'deploy':
        script += 'rm -f /srv/agentstration-preview/incoming/*.oci.tar\n'
    return script


def execute(action, request, client_factory=Azure, now=None):
    contract.operation(action, request)
    preview.require(request['provider'] == 'azure-vm' and request['registry'] == 'ghcr.io', 'Azure consumes private GHCR deliveries only.')
    if action == 'validate':
        return lifecycle.result(request, 'validated')
    started = time.monotonic()
    with tempfile.TemporaryDirectory(prefix='agentstration-azure-') as temporary:
        client = client_factory(request, temporary)
        record = client.read()
        resources = client.resources(record)
        if record is None:
            if action != 'deploy':
                return azure_result(request, 'destroyed' if action == 'destroy' else 'absent', [])
            created = now or datetime.now(timezone.utc)
            record = {'schemaVersion': 1, 'ownerId': str(uuid.uuid4()), 'createdAt': created.isoformat(),
                      'expiresAt': (created + timedelta(hours=request['ttlHours'])).isoformat(), 'request': request, 'destroyed': False, 'initialized': False}
            client.write(record, initial=True)
        with client.lock():
            record = client.read()
            validate_record(request, record, action)
            resources = client.resources(record)
            if action == 'deploy':
                if record['destroyed']:
                    preview.require(not resources, 'A reset preview still has resources; finish reset before deploying.')
                    created = now or datetime.now(timezone.utc)
                    record.update(ownerId=str(uuid.uuid4()), createdAt=created.isoformat(), destroyed=False, initialized=False)
                record['request'] = request
                record['expiresAt'] = (datetime.fromisoformat(record['createdAt']) + timedelta(hours=request['ttlHours'])).isoformat()
                client.write(record)
                client.provision(record)
                guest = client.transfer(expect_existing=record['initialized'])
                preview.require(guest['state'] == 'running', 'Azure guest did not confirm healthy exact-digest containers.')
                record['initialized'] = True
                client.write(record)
                value = azure_result(request, 'running', client.resources(record), record, guest)
                value['metrics'].update(client.metrics())
                value['metrics']['provisionAndDeploySeconds'] = round(time.monotonic() - started, 1)
                return value
            if action == 'destroy' and request['dataMode'] == 'reset':
                client.reset(record)
                record['destroyed'] = True
                record['initialized'] = False
                client.write(record)
                return azure_result(request, 'destroyed', client.resources(record), record)
            vm = resource_ids(request)['virtualMachine']
            present = any(r['id'].lower() == vm.lower() for r in resources)
            if not present:
                return azure_result(request, 'stopped' if resources else 'absent', resources, record)
            power = client.power()
            guest = {}
            if power == 'PowerState/running':
                guest = client.run_guest(action)
            if action == 'destroy':
                client.az('vm', 'deallocate', '--ids', vm, timeout=900)
                return azure_result(request, 'destroyed', resources, record, guest)
            return azure_result(request, guest.get('state', 'stopped' if power == 'PowerState/deallocated' else 'provisioning'), resources, record, guest)


def azure_result(request, state, resources, record=None, guest=None):
    value = lifecycle.result(request, state)
    guest = guest or {}
    value.update(expiresAt=(record or {}).get('expiresAt'), observedImages=guest.get('observedImages', {}),
                 providerResourceIds={'subscription': request['target']['subscriptionId'], 'resourceGroup': request['target']['resourceGroup'],
                                      'resources': [r['id'] for r in resources]},
                 metrics={k: guest[k] for k in ('guestOperationSeconds', 'memoryAvailableKiB') if k in guest})
    # No public URL before #1285 verifies protection. Application still binds guest loopback only.
    return value
