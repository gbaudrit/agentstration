"""Offline revision/registry boundaries. No network, Azure or live model is required."""
import base64
import copy
from datetime import datetime, timezone
import hashlib
import json
import unittest
from unittest.mock import patch

import azure
import contract
import manual
import preview
import workflow_result
from images import catalogue
from test_azure import Cloud, azure_request


def options(**changes):
    return {'operation': 'deploy', 'environment_id': 'manual-example', 'revision': 'main', 'provider': 'azure-vm',
            'database': 'sqlite', 'vm_size_profile': '1', 'ttl_hours': 2, 'data_mode': 'preserve',
            'test_profile': 'readiness', 'worker_revision': 'published', **changes}


VARIABLES = {'AZURE_PREVIEW_SUBSCRIPTION_ID': '00000000-0000-4000-8000-000000000001',
             'AZURE_PREVIEW_RESOURCE_GROUP': 'previews', 'AZURE_PREVIEW_IMAGE_VERSION': '24.04.202611170',
             'AZURE_PREVIEW_VM_SIZE1': 'Standard_B2ms', 'AZURE_PREVIEW_VM_SIZE2': 'Standard_B4ms'}


class Metadata:
    """Fake the API and OCI executable boundary, not the selection implementation."""
    def __init__(self):
        self.calls = []
        self.private = True
        self.fork = False
        self.missing_sha_tag = False
        self.missing_all_tags = False
        self.tamper = False
        self.refs = {'main': 'a' * 40, 'worker/v0.1.0': 'c' * 40}
        self.entries = {name: catalogue(name) for name in ('server', 'maf-worker')}
        self.configs, self.manifests, self.files = {}, {}, {}
        self.versions = []
        for component, sha in (('server', 'a' * 40), ('maf-worker', 'c' * 40)):
            entry = self.entries[component]
            image = 'ghcr.io/owner/repo-' + entry['imageName']
            self.manifests[image] = json.dumps({'schemaVersion': 2, 'image': component}).encode()
            self.configs[image] = {'os': 'linux', 'architecture': 'amd64', 'config': {'Labels': {
                'org.opencontainers.image.source': 'https://github.com/owner/repo',
                'org.opencontainers.image.revision': sha, 'org.opencontainers.image.version': entry['version']}}}
            family = 'applications' if component == 'server' else 'workers'
            self.files[f'deploy/images/{family}/{component}.json'] = json.dumps(entry)
            self.files[entry['versionFile']] = (preview.ROOT / entry['versionFile']).read_text()
            if component == 'maf-worker':
                self.versions = [{'name': 'sha256:' + '0' * 64, 'metadata': {'container': {'tags': []}}},
                                 {'name': 'sha256:' + hashlib.sha256(self.manifests[image]).hexdigest(),
                                  'metadata': {'container': {'tags': ['sha-' + sha]}}}]

    def run(self, arguments):
        self.calls.append(arguments)
        if arguments[:2] == ['gh', 'api']:
            path = arguments[2]
            if path == 'repos/owner/repo':
                value = {'owner': {'type': 'User'}}
            elif '/commits/' in path:
                ref = path.split('/commits/')[1]
                from urllib.parse import unquote
                ref = unquote(ref)
                if ref not in self.refs and not preview.SHA.fullmatch(ref):
                    raise manual.Unavailable('Unknown revision')
                value = {'sha': self.refs.get(ref, ref)}
            elif '/pulls/' in path:
                value = {'head': {'sha': 'a' * 40, 'repo': {'full_name': 'fork/repo' if self.fork else 'owner/repo'}}}
            elif '/contents/' in path:
                name = path.split('/contents/')[1].split('?')[0]
                value = {'type': 'file', 'encoding': 'base64', 'content': base64.b64encode(self.files[name].encode()).decode()}
            elif '/versions?' in path:
                value = self.versions
            else:
                value = {'visibility': 'private' if self.private else 'public'}
            return json.dumps(value).encode()
        reference = arguments[-1].removeprefix('docker://')
        image = reference.split('@')[0].split(':')[0]
        if '--raw' in arguments:
            if '@' not in reference and (self.missing_all_tags or self.missing_sha_tag and ':sha-' in reference):
                raise manual.Unavailable('Not published')
            return b'tampered' if self.tamper and '@' in reference else self.manifests[image]
        return json.dumps(self.configs[image]).encode()


class ManualTests(unittest.TestCase):
    def setUp(self):
        self.metadata = Metadata()
        self.selector = manual.PublishedImages('owner/repo', self.metadata.run)

    def resolve(self, **changes):
        return manual.resolve(options(**changes), VARIABLES, 'owner/repo', self.selector)

    def test_resolves_exact_server_and_independent_worker_with_frozen_defaults(self):
        value = self.resolve()
        self.assertEqual('a' * 40, value['sourceSha'])
        self.assertEqual('c' * 40, value['deliveries']['worker']['sourceSha'])
        self.assertEqual('ubuntu-24.04', value['target']['osProfile'])
        self.assertEqual(2, value['ttlHours'])
        self.assertTrue(all('@sha256:' in image['reference'] for image in contract.images(value).values()))
        self.assertTrue(all(command[0] in ('gh', 'skopeo') for command in self.metadata.calls))
        self.assertFalse(any('build' in command or 'checkout' in command for command in self.metadata.calls))

    def test_postgresql_reuses_pinned_image_and_explicit_independent_worker(self):
        value = self.resolve(database='postgresql', vm_size_profile='2', worker_revision='worker/v0.1.0', ttl_hours=3.0)
        self.assertEqual('Standard_B4ms', value['target']['vmSize'])
        self.assertRegex(value['postgresImage'], preview.POSTGRES_IMAGE)
        self.assertEqual('c' * 40, value['deliveries']['worker']['sourceSha'])

    def test_branch_tag_sha_and_same_repository_pr(self):
        for ref in ('main', 'a' * 40, 'pr:12'):
            with self.subTest(ref=ref):
                self.assertEqual('a' * 40, self.selector.revision(ref))
        self.assertEqual('c' * 40, self.selector.revision('worker/v0.1.0'))
        self.assertTrue(any('worker%2Fv0.1.0' in command[-1] for command in self.metadata.calls))

    def test_unknown_ref_and_fork_are_refused(self):
        with self.assertRaises(manual.Unavailable):
            self.selector.revision('not-published')
        self.metadata.fork = True
        with self.assertRaisesRegex(ValueError, 'Fork PR'):
            self.selector.revision('pr:12')

    def test_untrusted_ref_never_becomes_a_command(self):
        for ref in ('$(secret)', '--help', 'pr:0', 'pr:1/../../2', 'x\ny', 'x' * 201):
            with self.subTest(ref=ref), self.assertRaises(ValueError):
                self.selector.revision(ref)
        self.assertEqual([], self.metadata.calls)

    def test_private_package_is_required(self):
        self.metadata.private = False
        with self.assertRaisesRegex(ValueError, 'private GHCR'):
            self.resolve()
        self.assertFalse(any(command[0] == 'skopeo' for command in self.metadata.calls))

    def test_semver_release_fallback_still_requires_exact_source(self):
        self.metadata.missing_sha_tag = True
        self.assertEqual('a' * 40, self.resolve()['sourceSha'])
        self.metadata.configs['ghcr.io/owner/repo-server']['config']['Labels']['org.opencontainers.image.revision'] = 'f' * 40
        with self.assertRaisesRegex(ValueError, 'source SHA'):
            self.resolve()

    def test_missing_image_never_falls_back_to_another_revision_or_builds(self):
        self.metadata.missing_all_tags = True
        with self.assertRaisesRegex(ValueError, 'Publish this component'):
            self.resolve()

    def test_frozen_index_is_verified_before_config_inspection(self):
        self.metadata.tamper = True
        with self.assertRaisesRegex(ValueError, 'frozen index'):
            self.resolve()
        self.assertFalse(any('--config' in command for command in self.metadata.calls))

    def test_version_repository_and_platform_mismatches_are_refused(self):
        for field, value in (('org.opencontainers.image.version', '9.0.0'), ('org.opencontainers.image.source', 'https://github.com/other/repo')):
            metadata = Metadata()
            metadata.configs['ghcr.io/owner/repo-server']['config']['Labels'][field] = value
            with self.subTest(field=field), self.assertRaisesRegex(ValueError, 'platform, source repository or version'):
                manual.resolve(options(), VARIABLES, 'owner/repo', manual.PublishedImages('owner/repo', metadata.run))
        self.metadata.configs['ghcr.io/owner/repo-server']['architecture'] = 'arm64'
        with self.assertRaises(ValueError):
            self.resolve()

    def test_catalogue_identity_and_external_xml_entities_are_refused(self):
        entry = copy.deepcopy(self.metadata.entries['server'])
        entry['versionFile'] = '../../untrusted'
        self.metadata.files['deploy/images/applications/server.json'] = json.dumps(entry)
        with self.assertRaisesRegex(ValueError, 'catalogue'):
            self.resolve()
        self.metadata = Metadata()
        self.selector = manual.PublishedImages('owner/repo', self.metadata.run)
        self.metadata.files['Directory.Build.props'] = '<!DOCTYPE Project [<!ENTITY a "data">]><Project/>'
        with self.assertRaisesRegex(ValueError, 'XML entities'):
            self.resolve()

    def test_worker_protocol_is_from_published_source_and_must_be_supported(self):
        entry = copy.deepcopy(self.metadata.entries['maf-worker'])
        entry['executionMaterialVersions'] = ['1.0']
        path = 'deploy/images/workers/maf-worker.json'
        self.metadata.files[path] = json.dumps(entry)
        self.assertEqual(['1.0'], self.resolve()['deliveries']['worker']['component']['executionMaterialVersions'])
        entry['awpVersion'] = '2.0'
        self.metadata.files[path] = json.dumps(entry)
        with self.assertRaisesRegex(ValueError, 'protocol metadata'):
            self.resolve()

    def test_latest_worker_skips_untagged_manifests_and_refuses_empty_inventory(self):
        self.assertEqual('c' * 40, self.resolve()['deliveries']['worker']['sourceSha'])
        self.metadata.versions = self.metadata.versions[:1]
        with self.assertRaisesRegex(ValueError, 'No tagged Worker'):
            self.resolve()

    def test_invalid_choices_fail_before_any_registry_read(self):
        for changes in ({'data_mode': 'reset'}, {'environment_id': '../shared'}, {'ttl_hours': 0}, {'ttl_hours': 169},
                        {'ttl_hours': 1.5}, {'vm_size_profile': '6'}, {'provider': 'ssh-docker'}, {'database': 'unknown'}):
            with self.subTest(changes=changes), self.assertRaises(ValueError):
                self.resolve(**changes)
        variables = {**VARIABLES, 'AZURE_PREVIEW_IMAGE_VERSION': 'latest'}
        with self.assertRaises(ValueError):
            manual.resolve(options(), variables, 'owner/repo', self.selector)
        self.assertEqual([], self.metadata.calls)


class RecordedSelectionTests(unittest.TestCase):
    def setUp(self):
        self.cloud = Cloud()
        self.value = azure_request()
        azure.execute('deploy', self.value, self.cloud.factory, now=datetime(2026, 11, 24, tzinfo=timezone.utc), route_factory=lambda *args: None)
        self.cloud.effects.clear()

    def recorded(self, **changes):
        args = {'environment_id': self.value['environmentId'], 'subscription': self.value['target']['subscriptionId'],
                'resource_group': self.value['target']['resourceGroup'], 'repository': 'owner/repo', 'client_factory': self.cloud.factory, **changes}
        return azure.recorded_request(**args)

    def test_status_destroy_reuse_recorded_images_even_after_defaults_and_tags_change(self):
        with patch.dict('os.environ', {'AZURE_PREVIEW_IMAGE_VERSION': 'latest', 'AZURE_PREVIEW_VM_SIZE1': 'invalid'}):
            saved = self.recorded()
        self.assertEqual(self.value, saved)
        self.assertEqual([], self.cloud.effects)
        saved['dataMode'] = 'reset'
        self.assertEqual([], azure.execute('destroy', saved, self.cloud.factory)['providerResourceIds']['resources'])
        self.assertEqual('absent', azure.execute('status', self.recorded(), self.cloud.factory)['state'])

    def test_missing_record_never_reconstructs_or_adopts_resources(self):
        self.cloud.record = None
        with self.assertRaisesRegex(ValueError, 'No recorded preview'):
            self.recorded()
        self.assertEqual([], self.cloud.effects)

    def test_repository_environment_and_scope_must_match(self):
        for changes in ({'repository': 'other/repo'}, {'environment_id': 'another'}, {'resource_group': 'another'},
                        {'subscription': '00000000-0000-4000-8000-000000000002'}):
            with self.subTest(changes=changes), self.assertRaisesRegex(ValueError, 'does not belong'):
                self.recorded(**changes)
        self.cloud.record['request']['deliveries']['worker']['repository'] = 'other/repo'
        with self.assertRaises(ValueError):
            self.recorded()
        self.assertEqual([], self.cloud.effects)

    def test_corrupt_record_is_refused(self):
        self.cloud.record['ownerId'] = 'unverified'
        with self.assertRaises(ValueError):
            self.recorded()

    def test_summary_reports_frozen_selection_expiry_resources_and_withheld_url(self):
        result = azure.execute('status', self.value, self.cloud.factory)
        text = workflow_result.render(result)
        for value in (self.value['sourceSha'], 'sqlite', result['expiresAt'], 'protected access (#1285)',
                      *azure.resource_ids(self.value).values(), *[image['reference'] for image in result['images'].values()]):
            self.assertIn(value, text)

    def test_reset_waits_for_delayed_azure_inventory_and_has_a_deadline(self):
        client = object.__new__(azure.Azure)
        client.request = self.value
        remaining = self.cloud.items[:1]
        with patch.object(client, 'az'), patch.object(client, 'resources', side_effect=[[]] * 6 + [remaining, []]), patch('azure.time.sleep') as sleep:
            client.reset(self.cloud.record)
            sleep.assert_called_once_with(5)
        with patch.object(client, 'az'), patch.object(client, 'resources', return_value=remaining), patch('azure.time.sleep'), \
                patch('azure.time.monotonic', side_effect=[0, 121]), self.assertRaisesRegex(ValueError, 'has not converged'):
            client.reset(self.cloud.record)


if __name__ == '__main__':
    unittest.main()
