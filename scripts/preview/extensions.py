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
import urllib.error
import urllib.request
import uuid

import preview

PREFIX = 'preview:extension:'
ID = re.compile(r'[a-z][a-z0-9-]{0,31}')
METADATA = ('id', 'imageName', 'platform', 'extensionId', 'aepVersion', 'contributionKind', 'contributionId')


def configuration(value, selected):
    """Private .NET settings, separate from public receipts and managed AEP trust."""
    try:
        if isinstance(value, str):
            preview.require(len(value.encode()) <= 16384, "Private extension configuration is limited to 16 KiB.")
            import contract
            value = contract.parse(value)
        preview.require(isinstance(value, dict), 'Extension configuration must be a JSON object keyed by catalogue ID.')
        text = json.dumps(value, sort_keys=True, allow_nan=False)
        preview.require(len(text.encode()) <= 16384, 'Private extension configuration is limited to 16 KiB.')
        for identifier, settings in value.items():
            preview.require(isinstance(identifier, str) and ID.fullmatch(identifier) and isinstance(settings, dict),
                            'Extension configuration requires catalogue IDs and JSON settings objects.')
            preview.require(all(isinstance(key, str) and key.split(':')[0].casefold() != 'aep' for key in settings),
                            'AEP enrollment configuration is managed by the deployment tool.')
        return {identifier: value[identifier] for identifier in sorted(selected) if value.get(identifier)}
    except (json.JSONDecodeError, TypeError, OverflowError) as error:
        raise ValueError('Invalid private extension JSON configuration.') from None


def configuration_path(directory, identifier, version):
    validate_ids([identifier])
    return Path(directory).resolve() / ('extension-' + identifier + '-' + str(uuid.UUID(version)) + '.json')


def prepare_configuration(directory, state, settings):
    versions = state.setdefault('extensionConfigurations', {}) if settings or 'extensionConfigurations' in state else {}
    obsolete = []
    for identifier in list(versions):
        previous = configuration_path(directory, identifier, versions[identifier])
        preview.require(previous.is_file() and not previous.is_symlink(), 'Restore the recorded private extension configuration.')
        previous.chmod(0o600)
        text = json.dumps(settings[identifier], sort_keys=True, allow_nan=False) if identifier in settings else None
        if text != previous.read_text():
            obsolete.append(previous)
            del versions[identifier]
    for identifier, value in settings.items():
        if identifier not in versions:
            version = str(uuid.uuid4())
            preview.private_write(configuration_path(directory, identifier, version), json.dumps(value, sort_keys=True, allow_nan=False))
            versions[identifier] = version
    return obsolete


def validate_ids(values):
    preview.require(isinstance(values, list) and len(values) <= 16
                    and all(isinstance(value, str) and ID.fullmatch(value) for value in values)
                    and values == sorted(set(values)) and not {'server', 'maf-worker'} & set(values),
                    'Extensions must be a sorted unique list of up to 16 non-core catalogue IDs.')
    return values


def catalogue_path(identifier):
    validate_ids([identifier])
    return preview.ROOT / 'deploy/images/extensions' / (identifier + '.json')


def entry(identifier):
    path = catalogue_path(identifier)
    preview.require(path.is_file(), 'Extension has no implemented image catalogue: ' + identifier + '.')
    value = preview.read_json(path)
    preview.require(value.get('schemaVersion') == 1 and value.get('kind', 'aep-extension') == 'aep-extension'
                    and value.get('id') == identifier, 'Invalid AEP image catalogue.')
    # Recipes need only public catalogue metadata on the guest, never source projects.
    return value


def from_labels(labels):
    preview.require(isinstance(labels, list) and len(labels) <= 100, 'Invalid issue label inventory.')
    names = []
    for label in labels:
        name = label.get('name', '') if isinstance(label, dict) else label
        preview.require(isinstance(name, str), 'Invalid issue label.')
        names.append(name)
    import extension_deployments
    selected, _ = extension_deployments.labels(names)
    validate_ids(sorted(selected))
    # Admission freezes intent; the selector verifies the published source catalogue.
    return sorted(selected)


def options_from_labels(labels):
    from_labels(labels)
    import extension_deployments
    return extension_deployments.labels([item.get('name', '') if isinstance(item, dict) else item for item in labels])[1]


def trusted(receipts):
    validate_ids(list(receipts))
    identities = set()
    for identifier, receipt in receipts.items():
        component = preview.delivery_entry(receipt)
        preview.image_record(receipt, 'aep-extension', 'ghcr.io')
        preview.require(component['id'] == identifier and component['imageName'] == 'extension-' + identifier,
                        'Extension delivery differs from its catalogue ID/image namespace.')
        if catalogue_path(identifier).exists():
            expected = entry(identifier)
            preview.require(all(component.get(key) == expected.get(key) for key in METADATA),
                            'Extension delivery differs from the trusted deployment catalogue.')
        preview.require(component['extensionId'].casefold() not in identities, 'Duplicate AEP extension identity.')
        identities.add(component['extensionId'].casefold())


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
        preview.require(not any(other != identifier and previous.casefold() == identity.casefold() for other, previous in known.items()),
                        'An extension cannot alias another persisted AEP identity.')
        if identifier not in known:
            preview.require(not (directory / ('extension-' + identifier + '.key')).exists(),
                            'An unrecorded extension key exists; restore matching host state before retrying.')
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
            # Bind-mounted 0600 keys may belong to the host operator UID. Retain only DAC override.
            'read_only': True, 'cap_drop': ['ALL'], 'cap_add': ['DAC_OVERRIDE'], 'security_opt': ['no-new-privileges:true'], 'tmpfs': ['/tmp'],
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
        version = state.get('extensionConfigurations', {}).get(identifier)
        if version:
            # Changing the secret reference recreates the container without exposing a settings hash.
            config = 'aep-config-' + identifier + '-' + version
            keys[config] = {'file': str(configuration_path(directory, identifier, version))}
            services[name]['secrets'].append({'source': config, 'target': '/app/appsettings.Preview.json'})
    if keys:
        services['server'] = {'secrets': [{'source': 'aep-' + name, 'target': '/run/secrets/aep-' + name}
                                        for name in state.get('extensionKeys', {}) if service(name) in state['images']]}
    import extension_deployments
    return extension_deployments.merge({'services': services, 'volumes': volumes, 'secrets': keys},
                                       extension_deployments.overlay(directory, state))


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


def confirm_enrollment(authority, announcement, key, timeout=60):
    deadline = time.monotonic() + timeout
    while True:
        timestamp = int(time.time())
        signed = json.dumps({'timestamp': timestamp, 'announcement': announcement}, separators=(',', ':')).encode()
        signature = base64.b64encode(hmac.digest(key.encode(), signed, hashlib.sha256)).decode()
        try:
            result = request(authority + '/api/aep/enrollments/announce', announcement,
                             {'X-AEP-Enrollment-Timestamp': str(timestamp), 'X-AEP-Enrollment-Signature': signature})
            preview.require(result['requestId'] == announcement['instanceId'], 'Authority returned another enrollment identity.')
            if result['state'].lower() == 'available':
                return
        except urllib.error.HTTPError as error:
            if error.code not in (429, 503):
                raise
        except (urllib.error.URLError, TimeoutError):
            pass
        preview.require(time.monotonic() < deadline, 'Authority did not confirm available extension enrollment before the deadline.')
        time.sleep(1)


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
            confirm_enrollment(preview.server_address(directory), announcement, key)
        except (OSError, ValueError, KeyError, TypeError, AttributeError, subprocess.SubprocessError):
            # No response body, key, URL, or subprocess output in CI/ARM logs.
            raise ValueError('Authenticated AEP enrollment verification failed for ' + identifier + '.') from None
        verified.append(identifier)
    return verified
