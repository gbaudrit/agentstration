"""Optional extensions remain data selected by trusted tooling; no cloud or registry needed."""
import base64
import copy
import hashlib
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

import contract
import extensions
import issue_commands
import lifecycle
import manual
import preview
from images import catalogue
from test_contract import request, receipts
from test_issue_commands import GitHub, REPOSITORY
import test_lifecycle
from test_manual import Metadata, VARIABLES, options


def receipt(identifier='foundry'):
    return {'schemaVersion': 1, 'extension': catalogue(identifier), 'repository': 'owner/repo',
            'sourceSha': 'e' * 40, 'digest': 'sha256:' + 'f' * 64}


def selected(**kwargs):
    return request(extension_deliveries={'foundry': receipt()}, **kwargs)


class SelectionTests(unittest.TestCase):
    def test_tracking_labels_do_not_deploy_and_invalid_or_core_ids_are_refused(self):
        self.assertEqual([], extensions.from_labels([{'name': 'extension:foundry'}]))
        self.assertEqual(['foundry'], extensions.from_labels([{'name': 'preview:extension:foundry'}]))
        for name in ('../foundry', 'Foundry', '', 'server', 'maf-worker'):
            with self.subTest(name=name), self.assertRaises(ValueError):
                extensions.from_labels([{'name': extensions.PREFIX + name}])
        with self.assertRaises(ValueError):
            extensions.from_labels(['preview:extension:foundry'] * 2)

    def test_issue_freezes_labels_rechecks_before_execution_and_cleanup_ignores_changes(self):
        github = GitHub()
        github.issue['labels'] = [{'name': extensions.PREFIX + 'foundry'}]
        self.assertTrue(github.admit())
        options = github.selection()
        self.assertEqual(['foundry'], options['extensions'])
        self.assertEqual('17', issue_commands.tracking(options, REPOSITORY, github))
        github.issue['labels'] = []
        with self.assertRaisesRegex(ValueError, 'labels changed'):
            issue_commands.tracking(options, REPOSITORY, github)
        for operation in ('status', 'destroy'):
            github = GitHub('/preview ' + operation)
            github.issue['labels'] = [{'name': extensions.PREFIX + 'not-packaged'}]
            self.assertTrue(github.admit())
            self.assertNotIn('extensions', github.selection())
            self.assertEqual('17', issue_commands.tracking(github.selection(), REPOSITORY, github))

    def test_unmerged_label_is_admitted_for_published_source_verification(self):
        github = GitHub()
        github.issue['labels'] = [{'name': extensions.PREFIX + 'unpublished'}]
        self.assertTrue(github.admit())
        self.assertEqual(['unpublished'], github.selection()['extensions'])
        self.assertEqual('17', issue_commands.tracking(github.selection(), REPOSITORY, github))

    def test_independently_published_extension_uses_its_own_source_and_canonical_identity(self):
        metadata = Metadata()
        entry = catalogue('foundry')
        image = 'ghcr.io/owner/repo-extension-foundry'
        manifest = b'public-disposable-extension-manifest'
        metadata.manifests[image] = manifest
        metadata.configs[image] = {'os': 'linux', 'architecture': 'amd64', 'config': {'Labels': {
            'org.opencontainers.image.source': 'https://github.com/owner/repo',
            'org.opencontainers.image.revision': 'e' * 40, 'org.opencontainers.image.version': entry['version']}}}
        metadata.files['deploy/images/extensions/foundry.json'] = json.dumps(entry)
        metadata.files[entry['versionFile']] = entry['version']
        original = metadata.run
        def run(arguments):
            if arguments[:2] == ['gh', 'api'] and '/repo-extension-foundry/versions?' in arguments[2]:
                return json.dumps([{'name': 'sha256:' + hashlib.sha256(manifest).hexdigest(),
                                    'metadata': {'container': {'tags': ['sha-' + 'e' * 40]}}}]).encode()
            return original(arguments)
        value = manual.resolve(options(extensions=['foundry']), VARIABLES, 'owner/repo', manual.PublishedImages('owner/repo', run))
        self.assertEqual('a' * 40, value['sourceSha'])
        self.assertEqual('e' * 40, value['extensions']['foundry']['sourceSha'])
        self.assertEqual('aep-extension', value['extensions']['foundry']['component']['kind'])
        self.assertNotIn('versionFile', value['extensions']['foundry']['component'])
        metadata.files['deploy/images/extensions/foundry.json'] = json.dumps({**entry, 'extensionId': 'other'})
        with self.assertRaises(ValueError):
            manual.resolve(options(extensions=['foundry']), VARIABLES, 'owner/repo', manual.PublishedImages('owner/repo', run))


class PendingMetadata(Metadata):
    """A private image whose bounded catalogue is present only at its published source."""
    identifier = 'pending-aep'
    image = 'ghcr.io/owner/repo-extension-pending-aep'

    def __init__(self):
        super().__init__()
        self.pending = {**catalogue('foundry'), 'id': self.identifier, 'imageName': 'extension-' + self.identifier,
                        'extensionId': 'Fixture.Pending.Extension', 'contributionKind': 'schemaFormats',
                        'contributionId': 'fixture.schema', 'versionFile': 'src/extensions/Fixture.Pending.Extension/version.txt',
                        'project': 'unread/project.csproj', 'dockerfile': 'unread/Dockerfile', 'testProject': 'unread/test.csproj'}
        self.manifests[self.image] = b'public-disposable-pending-image'
        self.configs[self.image] = {'os': 'linux', 'architecture': 'amd64', 'config': {'Labels': {
            'org.opencontainers.image.source': 'https://github.com/owner/repo',
            'org.opencontainers.image.revision': 'e' * 40, 'org.opencontainers.image.version': self.pending['version']}}}
        self.files[self.path] = json.dumps(self.pending)
        self.files[self.pending['versionFile']] = self.pending['version']
        self.published = True

    @property
    def path(self):
        return 'deploy/images/extensions/' + self.identifier + '.json'

    def run(self, arguments):
        if arguments[:2] == ['gh', 'api'] and '/repo-extension-pending-aep/versions?' in arguments[2]:
            self.calls.append(arguments)
            return json.dumps([{'name': 'sha256:' + hashlib.sha256(self.manifests[self.image]).hexdigest(),
                                'metadata': {'container': {'tags': ['sha-' + 'e' * 40]}}}] if self.published else []).encode()
        return super().run(arguments)

    def resolve(self):
        return manual.resolve(options(extensions=[self.identifier]), VARIABLES, 'owner/repo', manual.PublishedImages('owner/repo', self.run))


class PendingCatalogueTests(unittest.TestCase):
    def test_private_published_source_is_sufficient_before_catalogue_merge(self):
        metadata = PendingMetadata()
        self.assertFalse(extensions.catalogue_path(metadata.identifier).exists())
        value = metadata.resolve()
        self.assertEqual(2, value['schemaVersion'])
        item = value['extensions'][metadata.identifier]
        self.assertEqual(('e' * 40, metadata.pending['version']), (item['sourceSha'], item['component']['version']))
        self.assertNotIn('versionFile', item['component'])
        self.assertNotIn('project', item['component'])
        contract.operation('deploy', value)
        self.assertFalse(any('unread/' in ' '.join(call) or call[0] not in ('gh', 'skopeo') for call in metadata.calls))
        reads = [call[2] for call in metadata.calls if call[:2] == ['gh', 'api'] and '/contents/' in call[2] and
                 ('pending-aep' in call[2] or 'Fixture.Pending' in call[2])]
        self.assertEqual(2, len(reads))
        self.assertTrue(all(path.endswith('?ref=' + 'e' * 40) for path in reads))
        with tempfile.TemporaryDirectory() as temporary:
            state = preview.prepare(Path(temporary) / 'preview', *receipts(), 'pending-test', extension_deliveries=value['extensions'])
            overlay = extensions.overlay(Path(temporary) / 'preview', state)
            service = overlay['services']['extension-pending-aep']
            self.assertEqual(['127.0.0.1::8080'], service['ports'])
            self.assertTrue(service['read_only'])
            self.assertNotIn('privileged', service)
            self.assertEqual(['extension-pending-aep:/var/lib/aep'], service['volumes'])

    def test_unpublished_public_and_inaccessible_images_fail_during_selection(self):
        for change in ('unpublished', 'public', 'inaccessible', 'versions-inaccessible'):
            metadata = PendingMetadata()
            if change == 'unpublished':
                metadata.published = False
            elif change == 'public':
                original = metadata.run
                def public(arguments):
                    if arguments[:2] == ['gh', 'api'] and arguments[2].endswith('/repo-extension-pending-aep'):
                        return b'{"visibility":"public"}'
                    return original(arguments)
                metadata.run = public
            else:
                original = metadata.run
                def inaccessible(arguments):
                    if arguments[:2] == ['gh', 'api'] and '/repo-extension-pending-aep' in arguments[2] and (change == 'inaccessible' or '/versions?' in arguments[2]):
                        raise manual.Unavailable('PRIVATE PROVIDER OUTPUT')
                    return original(arguments)
                metadata.run = inaccessible
            with self.subTest(change=change), self.assertRaises(ValueError) as failure:
                metadata.resolve()
            self.assertNotIn('PRIVATE', str(failure.exception))
            if change != 'public':
                self.assertEqual('pending-aep', failure.exception.public['component'])
                self.assertEqual('image-unavailable' if change == 'unpublished' else 'registry-access', failure.exception.public['code'])

    def test_pending_source_cannot_change_recipe_namespace_protocol_or_version_path(self):
        for field, invalid in (('schemaVersion', True), ('imageName', 'server'), ('id', 'foundry'), ('kind', 'awp-worker'),
                               ('versionFile', '../../private'), ('versionFile', '.github/workflows/ci.yml'),
                               ('versionFile', 'src/extensions/../version.txt'),
                               ('extensionId', 'Environment__Injection'), ('aepVersion', 'unsupported'),
                               ('contributionKind', 'hostCommands')):
            metadata = PendingMetadata()
            metadata.files[metadata.path] = json.dumps({**metadata.pending, field: invalid})
            with self.subTest(field=field, invalid=invalid), self.assertRaises(ValueError):
                metadata.resolve()
            if field == 'versionFile':
                self.assertFalse(any('/contents/' + invalid in ' '.join(call) for call in metadata.calls))
        metadata = PendingMetadata()
        metadata.configs[metadata.image]['config']['Labels']['org.opencontainers.image.source'] = 'https://github.com/foreign/repo'
        with self.assertRaises(ValueError):
            metadata.resolve()

    def test_pending_receipt_cannot_alias_another_component_image(self):
        value = PendingMetadata().resolve()
        value['extensions']['pending-aep']['component']['imageName'] = 'extension-foundry'
        with self.assertRaisesRegex(ValueError, 'namespace'):
            contract.operation('deploy', value)

    def test_pending_extension_cannot_reuse_a_deselected_extensions_identity(self):
        value = PendingMetadata().resolve()
        value['extensions']['pending-aep']['component']['extensionId'] = 'agentstration.extensions.foundry'
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary) / 'preview'
            preview.prepare(directory, *receipts(), 'example', extension_deliveries={'foundry': receipt()})
            original = (directory / 'extension-foundry.key').read_bytes()
            with self.assertRaisesRegex(ValueError, 'persisted AEP identity'):
                preview.prepare(directory, *receipts(), 'example', extension_deliveries=value['extensions'])
            self.assertEqual(original, (directory / 'extension-foundry.key').read_bytes())
            self.assertFalse((directory / 'extension-pending-aep.key').exists())

    def test_guest_bundle_contains_only_frozen_public_metadata_and_trusted_scripts(self):
        import azure
        import tarfile
        value = PendingMetadata().resolve()
        def copy(arguments, **_kwargs):
            Path(arguments[-1].removeprefix('oci-archive:')).write_bytes(b'public-disposable-oci-fixture')
        with tempfile.TemporaryDirectory() as temporary, patch.object(azure, 'command', side_effect=copy):
            bundle, _ = azure.bundle_images(value, Path(temporary))
            with tarfile.open(bundle) as archive:
                path = 'tooling/deploy/images/extensions/pending-aep.json'
                raw = archive.extractfile(path).read()
                self.assertEqual({'schemaVersion': 1, **value['extensions']['pending-aep']['component']}, json.loads(raw))
                self.assertNotIn(b'versionFile', raw)
                self.assertNotIn(b'unread/', raw)
                self.assertFalse(any('src/extensions' in name or 'Dockerfile' in name for name in archive.getnames()))
                self.assertEqual((preview.ROOT / 'scripts/preview/extensions.py').read_bytes(),
                                 archive.extractfile('tooling/scripts/preview/extensions.py').read())
                root = Path(temporary) / 'guest'
                destination = root / 'deploy/images/extensions/pending-aep.json'
                destination.parent.mkdir(parents=True)
                destination.write_bytes(raw)
                with patch.object(preview, 'ROOT', root):
                    contract.operation('deploy', value)




class ExtensionContractTests(unittest.TestCase):
    def test_version_1_remains_unchanged_and_version_2_freezes_optional_receipts(self):
        self.assertEqual(1, request()['schemaVersion'])
        value = selected()
        self.assertEqual(2, value['schemaVersion'])
        self.assertEqual(value, contract.loads(json.dumps(value)))
        self.assertEqual({'server', 'worker', 'extension-foundry'}, set(contract.images(value)))
        self.assertEqual('Agentstration.Extensions.Foundry', contract.images(value)['extension-foundry']['extensionId'])
        self.assertEqual({'server', 'worker', 'postgres', 'extension-foundry'}, set(contract.images(selected(database='postgresql'))))

    def test_noncanonical_duplicate_or_unknown_deployments_fail_but_saved_cleanup_needs_no_catalogue(self):
        for change in ('secret', 'key', 'identity', 'mutable', 'kind'):
            value = selected()
            if change == 'secret': value['extensions']['foundry']['token'] = 'public-disposable'
            if change == 'key': value['extensions']['other'] = value['extensions'].pop('foundry')
            if change == 'identity': value['extensions']['foundry']['component']['extensionId'] = 'bad:injected'
            if change == 'mutable': value['extensions']['foundry']['digest'] = 'latest'
            if change == 'kind': value['extensions']['foundry']['component']['kind'] = 'server'
            with self.subTest(change=change), self.assertRaises(ValueError): contract.validate(value)
        value = selected()
        with patch.object(extensions, 'entry', side_effect=ValueError('Catalogue no longer exists')):
            contract.operation('status', value)
            contract.operation('destroy', value)
            with self.assertRaises(ValueError): contract.operation('deploy', value)
        value['extensions']['foundry']['component']['extensionId'] = 'different.valid.identity'
        with self.assertRaises(ValueError): contract.operation('deploy', value)

    def test_authority_configuration_cannot_alias_two_extension_identities_by_case(self):
        first = contract.delivery(receipt())
        second = copy.deepcopy(first)
        second['component'].update(id='another', imageName='extension-another',
                                   extensionId=first['component']['extensionId'].lower())
        with self.assertRaisesRegex(ValueError, 'Duplicate AEP'):
            request(extension_deliveries={'another': second, 'foundry': first})
        entries = {'foundry': first['component'], 'another': second['component']}
        with patch.object(extensions, 'entry', side_effect=lambda identifier: entries[identifier]):
            with self.assertRaisesRegex(ValueError, 'Duplicate AEP'):
                extensions.trusted({'another': second, 'foundry': first})

    def test_private_state_keys_are_isolated_retained_and_never_silently_replaced(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary) / 'example'
            args = (*receipts(), 'example')
            first = preview.prepare(directory, *args, extension_deliveries={'foundry': receipt()})
            key = directory / 'extension-foundry.key'
            original = key.read_bytes()
            config = preview.read_json(directory / 'server-configuration.json')
            trust = config['Agentstration']['Extensions']['Agentstration.Extensions.Foundry']
            self.assertEqual('/run/secrets/aep-foundry', trust['SharedKeyFile']['Path'])
            self.assertNotEqual(original, (directory / 'worker.key').read_bytes())
            self.assertEqual(0o600, key.stat().st_mode & 0o777)
            overlay = extensions.overlay(directory, first)
            service = overlay['services']['extension-foundry']
            self.assertEqual(['127.0.0.1::8080'], service['ports'])
            self.assertEqual([{'source': 'aep-foundry', 'target': '/run/secrets/aep-key'}], service['secrets'])
            self.assertNotIn(original.decode(), json.dumps(overlay))
            second = preview.prepare(directory, *args)
            self.assertNotIn('extension-foundry', second['images'])
            self.assertEqual(original, key.read_bytes())
            self.assertEqual({}, preview.read_json(directory / 'server-configuration.json')['Agentstration']['Extensions'])
            preview.prepare(directory, *args, extension_deliveries={'foundry': receipt()})
            self.assertEqual(original, key.read_bytes())
            key.unlink()
            with self.assertRaisesRegex(ValueError, 'original extension'): preview.prepare(directory, *args, extension_deliveries={'foundry': receipt()})

    def test_multiple_catalogue_entries_have_separate_keys_state_and_trust(self):
        first = contract.delivery(receipt())
        second = copy.deepcopy(first)
        second['component'].update(id='another', imageName='extension-another', extensionId='Example.Another',
                                   contributionKind='tools', contributionId='another-tool')
        entries = {'foundry': first['component'], 'another': second['component']}
        with tempfile.TemporaryDirectory() as temporary, patch.object(extensions, 'entry', side_effect=lambda identifier: entries[identifier]):
            directory = Path(temporary) / 'example'
            state = preview.prepare(directory, *receipts(), 'example', extension_deliveries={'another': second, 'foundry': first})
            keys = [(directory / ('extension-' + identifier + '.key')).read_bytes() for identifier in entries]
            self.assertNotEqual(*keys)
            overlay = extensions.overlay(directory, state)
            for identifier in entries:
                service = overlay['services'][extensions.service(identifier)]
                self.assertEqual([{'source': 'aep-' + identifier, 'target': '/run/secrets/aep-key'}], service['secrets'])
                self.assertEqual([extensions.service(identifier) + ':/var/lib/aep'], service['volumes'])
            self.assertEqual(2, len(overlay['services']['server']['secrets']))
            preview.prepare(directory, *receipts(), 'example', extension_deliveries={'another': second})
            config = preview.read_json(directory / 'server-configuration.json')['Agentstration']
            self.assertEqual({'Example.Another'}, set(config['Extensions']))
            self.assertEqual(['extension-another'], config['Aep']['Transport']['AllowedHttpHosts'])

    def test_authenticated_manifest_and_signed_available_enrollment_are_required(self):
        value = selected()
        image = contract.images(value)['extension-foundry']
        identity = {'id': image['extensionId'], 'version': image['version'], 'name': 'Public fixture', 'description': None}
        manifest = {'protocolVersion': image['aepVersion'], 'extension': identity,
                    'contributions': {image['contributionKind']: [{'id': image['contributionId']}]}}
        instance = '00000000-0000-4000-8000-000000000001'
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            key = 'public-disposable-key'
            (directory / 'extension-foundry.key').write_text(key)
            def compose(_directory, *arguments, **_kwargs):
                return '127.0.0.1:5099' if arguments[0] == 'port' else instance
            with patch.object(preview, 'compose', side_effect=compose), patch.object(preview, 'server_address', return_value='http://127.0.0.1:5080'), patch.object(extensions, 'request') as call:
                call.side_effect = [manifest, {'requestId': instance, 'state': 'available'}]
                self.assertEqual(['foundry'], extensions.verify(directory, {'images': {'extension-foundry': image}}))
                headers = call.call_args_list[0].kwargs['headers']
                self.assertEqual('Bearer ' + key, headers['Authorization'])
                post = call.call_args_list[1].args
                payload = json.dumps({'timestamp': int(post[2]['X-AEP-Enrollment-Timestamp']), 'announcement': post[1]}, separators=(',', ':')).encode()
                self.assertEqual(base64.b64encode(extensions.hmac.digest(key.encode(), payload, 'sha256')).decode(), post[2]['X-AEP-Enrollment-Signature'])
                self.assertEqual('http://extension-foundry:8080', post[1]['endpoint'])
                for response in ({'requestId': 'other', 'state': 'available'},):
                    call.side_effect = [manifest, response]
                    with self.assertRaisesRegex(ValueError, 'Authenticated AEP'): extensions.verify(directory, {'images': {'extension-foundry': image}})


class ExtensionLifecycleTests(unittest.TestCase):
    setUp = test_lifecycle.LifecycleTests.setUp
    execute = test_lifecycle.LifecycleTests.execute

    def test_redeploy_deselects_only_inspected_owned_container_and_retains_key(self):
        value = selected()
        self.execute('deploy', value)
        directory = self.root / 'example'
        key = (directory / 'extension-foundry.key').read_bytes()
        self.execute('deploy', request())
        self.assertIn(('rm', 'example-extension-foundry'), self.host.mutations)
        self.assertEqual(key, (directory / 'extension-foundry.key').read_bytes())
        self.assertEqual('running', self.execute('status', request())['state'])
        self.execute('deploy', value)
        self.assertEqual(key, (directory / 'extension-foundry.key').read_bytes())
        self.assertEqual('running', self.execute('status', value)['state'])


    def test_deselection_retry_after_interruption_uses_retained_managed_identity(self):
        self.execute('deploy', selected())
        original = self.host.docker
        def interrupted(*arguments):
            if arguments[0] == 'stop' and arguments[-1] == 'example-extension-foundry':
                raise RuntimeError('public-disposable-interruption')
            return original(*arguments)
        with patch.object(lifecycle, 'docker', side_effect=interrupted):
            with self.assertRaises(RuntimeError):
                self.execute('deploy', request())
        self.assertEqual('running', self.execute('deploy', request())['state'])
        self.assertIn(('rm', 'example-extension-foundry'), self.host.mutations)


class ExtensionTransferTests(unittest.TestCase):
    def test_bundle_contains_every_selected_exact_archive_and_only_public_recipe_metadata(self):
        import azure
        import tarfile
        value = selected(provider='azure-vm', target={'subscriptionId': '00000000-0000-4000-8000-000000000001',
            'resourceGroup': 'previews', 'region': 'westeurope', 'vmSize': 'Standard_B2ms', 'imageVersion': '24.04.202611170'})
        calls = []
        def copy(arguments, **_kwargs):
            calls.append(arguments)
            Path(arguments[-1].removeprefix('oci-archive:')).write_bytes(b'public-disposable-oci-fixture')
        with tempfile.TemporaryDirectory() as temporary, patch.object(azure, 'command', side_effect=copy):
            bundle, checksum = azure.bundle_images(value, Path(temporary))
            self.assertEqual(hashlib.sha256(bundle.read_bytes()).hexdigest(), checksum)
            with tarfile.open(bundle) as archive:
                names = set(archive.getnames())
                for name in ('server', 'worker', 'extension-foundry'):
                    self.assertIn('incoming/' + name + '.oci.tar', names)
                self.assertIn('tooling/deploy/images/extensions/foundry.json', names)
                self.assertIn('tooling/scripts/preview/extensions.py', names)
                self.assertFalse(any(name.endswith('.key') or 'config.json' in name or 'src/extensions' in name for name in names))
            self.assertEqual(3, len(calls))
            for arguments, image in zip(calls, contract.images(value).values()):
                self.assertIn('docker://' + image['reference'], arguments)
                self.assertIn('--preserve-digests', arguments)
                self.assertIn('--all', arguments)

    def test_azure_requires_positive_enrollment_evidence_before_reporting_success(self):
        import azure
        import test_azure
        value = selected(provider='azure-vm', target={'subscriptionId': '00000000-0000-4000-8000-000000000001',
            'resourceGroup': 'previews', 'region': 'westeurope', 'vmSize': 'Standard_B2ms', 'imageVersion': '24.04.202611170'})
        cloud = test_azure.Cloud()
        with tempfile.TemporaryDirectory() as temporary:
            with self.assertRaisesRegex(ValueError, 'AEP enrollment'):
                azure.execute('deploy', value, client_factory=cloud.factory, route_factory=lambda *args: None)
            original = cloud.transfer
            def verified(**kwargs):
                return {**original(**kwargs), 'extensionsVerified': ['foundry']}
            cloud.transfer = verified
            self.assertEqual('running', azure.execute('deploy', value, client_factory=cloud.factory, route_factory=lambda *args: None)['state'])

    def test_authentication_headers_are_not_forwarded_on_redirects(self):
        self.assertIsNone(extensions.NoRedirect().redirect_request(None, None, 302, '', {}, 'https://other.example/'))


class EnrollmentDeadlineTests(unittest.TestCase):
    def test_pending_enrollment_is_retried_but_deadline_is_bounded(self):
        announcement = {'instanceId': 'public-disposable-instance'}
        with patch.object(extensions, 'request') as request, patch.object(extensions.time, 'sleep') as sleep:
            request.side_effect = [{'requestId': announcement['instanceId'], 'state': 'pending'},
                                   {'requestId': announcement['instanceId'], 'state': 'available'}]
            extensions.confirm_enrollment('http://127.0.0.1:5080', announcement, 'public-disposable-key')
            self.assertEqual(2, request.call_count)
            sleep.assert_called_once_with(1)
            request.side_effect = None
            request.return_value = {'requestId': announcement['instanceId'], 'state': 'pending'}
            with self.assertRaisesRegex(ValueError, 'deadline'):
                extensions.confirm_enrollment('http://127.0.0.1:5080', announcement, 'public-disposable-key', timeout=0)


class PrivateConfigurationTests(unittest.TestCase):
    def test_settings_are_private_selected_json_and_cannot_override_enrollment(self):
        values = {'foundry': {'Foundry': {'Endpoint': 'https://provider.example/'}}, 'another': {'Token': 'public-disposable-ignored'}}
        self.assertEqual({'foundry': values['foundry']}, extensions.configuration(json.dumps(values), ['foundry']))
        for invalid in ('not-json', [], {'foundry': []}, {'foundry': {'AEP': {}}},
                        {'foundry': {'aep:SharedKeyFile:Path': '/other'}}, {'foundry': {'Data': 'x' * 16384}}):
            with self.subTest(invalid=type(invalid).__name__), self.assertRaises(ValueError):
                extensions.configuration(invalid, ['foundry'])

    def test_private_file_rotation_recreates_only_the_selected_service_and_retains_identity(self):
        secret = 'public-disposable-backend-setting'
        values = {'foundry': {'Backend': {'ApiKey': secret}}}
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary) / 'example'
            args = (*receipts(), 'example')
            first = preview.prepare(directory, *args, extension_deliveries={'foundry': receipt()}, extension_configuration=values)
            identity = first['extensionConfigurations']['foundry']
            path = extensions.configuration_path(directory, 'foundry', identity)
            self.assertEqual(values['foundry'], json.loads(path.read_text()))
            self.assertEqual(0o600, path.stat().st_mode & 0o777)
            key = (directory / 'extension-foundry.key').read_bytes()
            overlay = extensions.overlay(directory, first)
            source = 'aep-config-foundry-' + identity
            self.assertIn({'source': source, 'target': '/app/appsettings.Preview.json'}, overlay['services']['extension-foundry']['secrets'])
            self.assertEqual([{'source': 'aep-foundry', 'target': '/run/secrets/aep-foundry'}], overlay['services']['server']['secrets'])
            self.assertNotIn(secret, json.dumps(overlay))
            self.assertNotIn(secret, json.dumps(first))
            same = preview.prepare(directory, *args, extension_deliveries={'foundry': receipt()}, extension_configuration=values)
            self.assertEqual(identity, same['extensionConfigurations']['foundry'])
            changed = preview.prepare(directory, *args, extension_deliveries={'foundry': receipt()}, extension_configuration={'foundry': {'Backend': {'ApiKey': secret + '-rotated'}}})
            self.assertNotEqual(identity, changed['extensionConfigurations']['foundry'])
            self.assertFalse(path.exists())
            self.assertEqual(key, (directory / 'extension-foundry.key').read_bytes())
            cleared = preview.prepare(directory, *args, extension_deliveries={'foundry': receipt()})
            self.assertEqual({}, cleared['extensionConfigurations'])
            self.assertNotIn('/app/appsettings.Preview.json', json.dumps(extensions.overlay(directory, cleared)))

    def test_azure_transports_settings_only_as_protected_parameters(self):
        import azure
        import test_azure
        value = test_azure.azure_request(extension_deliveries={'foundry': receipt()})
        secret = 'public-disposable-provider-credential'
        settings = {'foundry': {'Backend': {'ApiKey': secret}}}
        with tempfile.TemporaryDirectory() as temporary:
            client = object.__new__(azure.Azure)
            client.request, client.directory = value, Path(temporary)
            client.read = lambda: {'ownerId': '00000000-0000-4000-8000-000000000001', 'createdAt': '2026-11-26T00:00:00Z', 'expiresAt': '2026-11-26T02:00:00Z'}
            def invoke(*args, **kwargs):
                if args[:3] == ('rest', '--method', 'put'):
                    body = json.loads((client.directory / 'run-command.json').read_text())
                    properties = body['properties']
                    self.assertNotIn(secret, json.dumps(properties['source']))
                    self.assertEqual([], properties['parameters'])
                    self.assertEqual([{'name': 'EXTENSION_CONFIGURATION', 'value': base64.b64encode(json.dumps(settings).encode()).decode()}], properties['protectedParameters'])
                    self.assertNotIn(secret, json.dumps(value))
                if args[:3] == ('rest', '--method', 'get'):
                    return {'properties': {'instanceView': {'executionState': 'Succeeded', 'exitCode': 0,
                        'output': 'AGENTSTRATION_RESULT={"state":"running"}'}}}
            client.az = invoke
            client.run_guest('deploy', checksum='a' * 64, extension_configuration=settings)
            self.assertFalse((client.directory / 'run-command.json').exists())

    def test_invalid_configuration_fails_before_provider_mutation_and_input_is_removed(self):
        import azure
        from test_azure import azure_request
        import os
        with patch.dict(os.environ, {'PREVIEW_EXTENSION_CONFIGURATION': 'not-json'}), patch.object(azure, 'execute') as provider:
            with self.assertRaises(ValueError):
                lifecycle.execute('deploy', azure_request(), None)
            provider.assert_not_called()
            self.assertNotIn('PREVIEW_EXTENSION_CONFIGURATION', os.environ)


    def test_absent_optional_environment_secret_needs_no_new_configuration(self):
        import azure
        import os
        from test_azure import azure_request
        value = azure_request()
        with patch.dict(os.environ, {'PREVIEW_EXTENSION_CONFIGURATION': ''}), patch.object(azure, 'execute', return_value={'state': 'running'}) as provider:
            self.assertEqual({'state': 'running'}, lifecycle.execute('deploy', value, None))
            provider.assert_called_once_with('deploy', value)
            self.assertNotIn('PREVIEW_EXTENSION_CONFIGURATION', os.environ)

    def test_guest_removes_protected_settings_before_any_docker_invocation(self):
        import azure_host
        import os
        from contextlib import redirect_stdout
        import io
        from test_azure import azure_request
        settings = {'foundry': {'Backend': {'Token': 'public-disposable-fixture'}}}
        encoded = base64.b64encode(json.dumps(settings).encode()).decode()
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / 'request.json'
            path.write_text(json.dumps(azure_request(extension_deliveries={'foundry': receipt()})))
            def run(*arguments, **kwargs):
                self.assertNotIn('EXTENSION_CONFIGURATION', os.environ)
                self.assertNotIn('BUNDLE_URL', os.environ)
                self.assertEqual(settings, json.loads(kwargs['extension_configuration']))
                return {'state': 'running'}
            with patch.dict(os.environ, {'EXTENSION_CONFIGURATION': encoded, 'BUNDLE_URL': 'public-disposable-fixture'}), patch('sys.argv', ['azure_host.py', 'deploy', '--request', str(path)]), patch.object(azure_host, 'run', side_effect=run), redirect_stdout(io.StringIO()):
                azure_host.main()
