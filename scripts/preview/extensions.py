"""Trusted AEP deployment recipes; labels select data, never executable configuration."""
import base64
import hashlib
import hmac
import json
from pathlib import Path
import re
import secrets
import subprocess
import time
import urllib.request
import uuid

import preview

PREFIX = 'preview:extension:'
ID = re.compile(r'[a-z][a-z0-9-]{0,31}')
METADATA = ('id', 'imageName', 'platform', 'extensionId', 'aepVersion', 'contributionKind', 'contributionId')


def validate_ids(values):
    preview.require(isinstance(values, list) and len(values) <= 16
                    and all(isinstance(value, str) and ID.fullmatch(value) for value in values)
                    and values == sorted(set(values)), 'Extensions must be a sorted unique list of up to 16 catalogue IDs.')
    return values


def entry(identifier):
    validate_ids([identifier])
    path = preview.ROOT / 'deploy/images/extensions' / (identifier + '.json')
    preview.require(path.is_file(), 'Extension has no implemented image catalogue: ' + identifier + '.')
    value = preview.read_json(path)
    preview.require(value.get('schemaVersion') == 1 and value.get('kind', 'aep-extension') == 'aep-extension'
                    and value.get('id') == identifier, 'Invalid AEP image catalogue.')
    # Recipes need only public catalogue metadata on the guest, never source projects.
    return value


def from_labels(labels):
    preview.require(isinstance(labels, list) and len(labels) <= 100, 'Invalid issue label inventory.')
    selected = []
    for label in labels:
        name = label.get('name', '') if isinstance(label, dict) else label
        preview.require(isinstance(name, str), 'Invalid issue label.')
        if name.startswith(PREFIX):
            selected.append(name[len(PREFIX):])
    validate_ids(sorted(selected))
    for identifier in selected:
        entry(identifier)
    return sorted(selected)


def trusted(receipts):
    validate_ids(list(receipts))
    identities = set()
    for identifier, receipt in receipts.items():
        component = preview.delivery_entry(receipt)
        expected = entry(identifier)
        preview.require(all(component.get(key) == expected.get(key) for key in METADATA),
                        'Extension delivery differs from the trusted deployment catalogue.')
        preview.require(component['extensionId'] not in identities, 'Duplicate AEP extension identity.')
        identities.add(component['extensionId'])


def service(identifier):
    return 'extension-' + identifier


def prepare(directory, state, configuration, receipts):
    trusted(receipts)
    # Retain the identity/key inventory even after deselection; missing keys are never regenerated.
    known = state.setdefault('extensionKeys', {}) if receipts or 'extensionKeys' in state else {}
    for identifier, identity in known.items():
        validate_ids([identifier])
        key = directory / ('extension-' + identifier + '.key')
        preview.require(key.is_file() and not key.is_symlink(), 'Restore the original extension enrollment key.')
        key.chmod(0o600)
    agent = configuration['Agentstration']
    managed = agent.setdefault('Extensions', {}) if known or receipts else {}
    for identity in known.values():
        managed.pop(identity, None)
    for identifier, receipt in receipts.items():
        identity = preview.delivery_entry(receipt)['extensionId']
        preview.require(identifier not in known or known[identifier] == identity, 'An extension catalogue ID cannot change its persisted AEP identity.')
        if identifier not in known:
            preview.private_write(directory / ('extension-' + identifier + '.key'), secrets.token_urlsafe(48))
            known[identifier] = identity
        managed[identity] = {'EnrollmentMode': 'SharedKeyFile', 'SharedKeyFile': {'Path': '/run/secrets/aep-' + identifier}}
    if known or receipts:
        hosts = [service(identifier) for identifier in receipts]
        agent['Aep'] = {'Enrollment': {'AutoEnrollmentLevel': '/instance'},
                        'Transport': {'AllowedHttpHosts': hosts, 'AllowedPrivateNetworkHosts': hosts}}


def overlay(directory, state):
    services, volumes, keys = {}, {}, {}
    labels = {'io.agentstration.preview.environment': state['environmentId'],
              'io.agentstration.preview.owner': state.get('deploymentOwnerId', 'unmanaged')}
    for name, image in state['images'].items():
        if not name.startswith('extension-'):
            continue
        identifier = name[len('extension-'):]
        key = 'aep-' + identifier
        keys[key] = {'file': str(directory / (name + '.key'))}
        volumes[name] = {'labels': labels}
        services[name] = {'image': image['reference'], 'restart': 'unless-stopped',
            'labels': {**labels, 'io.agentstration.preview.source': image['sourceSha']},
            'read_only': True, 'cap_drop': ['ALL'], 'security_opt': ['no-new-privileges:true'], 'tmpfs': ['/tmp'],
            'ports': ['127.0.0.1::8080'],
            'environment': {'ASPNETCORE_ENVIRONMENT': 'Preview', 'Aep__EnrollmentMode': 'SharedKeyFile',
                'Aep__SharedKeyFile__Path': '/run/secrets/aep-key',
                'Aep__SharedKeyFile__AuthorityUrl': 'http://server:8080',
                'Aep__SharedKeyFile__PublicEndpoint': 'http://' + name + ':8080',
                'Aep__SharedKeyFile__StateFile': '/var/lib/aep/instance',
                'Aep__SharedKeyFile__AllowInsecureHttp': 'true'},
            'secrets': [{'source': key, 'target': '/run/secrets/aep-key'}],
            'volumes': [name + ':/var/lib/aep'], 'depends_on': {'server': {'condition': 'service_started'}},
            'healthcheck': {'test': ['CMD', 'bash', '-ec',
                "exec 3<>/dev/tcp/127.0.0.1/8080; printf 'GET /health HTTP/1.1\\r\\nHost: localhost\\r\\nConnection: close\\r\\n\\r\\n' >&3; IFS= read -r status <&3; [[ \"$$status\" == *' 200 '* ]]"],
                'interval': '2s', 'timeout': '5s', 'retries': 60, 'start_period': '20s'}}
    if keys:
        services['server'] = {'secrets': [{'source': key, 'target': '/run/secrets/' + key} for key in keys]}
    return {'services': services, 'volumes': volumes, 'secrets': keys}


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


def request(url, body=None, headers=None):
    payload = json.dumps(body, separators=(',', ':')).encode() if body is not None else None
    value = urllib.request.Request(url, payload, {'Content-Type': 'application/json', **(headers or {})})
    with urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect()).open(value, timeout=10) as response:
        raw = response.read(65537)
        preview.require(response.status == 200 and len(raw) <= 65536, 'Invalid AEP verification response.')
        return json.loads(raw)


def verify(directory, state):
    verified = []
    for name, image in state['images'].items():
        if not name.startswith('extension-'):
            continue
        identifier = name[len('extension-'):]
        port = preview.compose(directory, 'port', name, '8080', capture=True).strip()
        preview.require(re.fullmatch(r'127\.0\.0\.1:[0-9]{1,5}', port), 'Extension verification requires a loopback-only endpoint.')
        key = (Path(directory) / (name + '.key')).read_text()
        try:
            manifest = request('http://' + port + '/.well-known/aep', headers={'Authorization': 'Bearer ' + key})
            identity = manifest['extension']
            preview.require(identity['id'] == image['extensionId'] and identity['version'] == image['version']
                and manifest['protocolVersion'] == image['aepVersion']
                and image['contributionId'] in {item['id'] for item in manifest.get('contributions', {}).get(image['contributionKind'], [])},
                'Extension manifest differs from the pinned delivery.')
            instance = str(uuid.UUID(preview.compose(directory, 'exec', '-T', name, 'cat', '/var/lib/aep/instance', capture=True).strip()))
            announcement = {'instanceId': instance, 'extension': {field: identity.get(field) for field in ('id', 'name', 'version', 'description')},
                            'endpoint': 'http://' + name + ':8080', 'pairingUri': None, 'method': 'sharedKeyFile'}
            timestamp = int(time.time())
            signed = json.dumps({'timestamp': timestamp, 'announcement': announcement}, separators=(',', ':')).encode()
            signature = base64.b64encode(hmac.digest(key.encode(), signed, hashlib.sha256)).decode()
            result = request(preview.server_address(directory) + '/api/aep/enrollments/announce', announcement,
                             {'X-AEP-Enrollment-Timestamp': str(timestamp), 'X-AEP-Enrollment-Signature': signature})
            preview.require(result['requestId'] == instance and result['state'].lower() == 'available',
                            'Authority has not confirmed available extension enrollment.')
        except (OSError, ValueError, KeyError, TypeError, subprocess.SubprocessError):
            # No response body, key, URL, or subprocess output in CI/ARM logs.
            raise ValueError('Authenticated AEP enrollment verification failed for ' + identifier + '.') from None
        verified.append(identifier)
    return verified
