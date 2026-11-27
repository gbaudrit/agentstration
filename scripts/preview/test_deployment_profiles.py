"""GUID selection, exact-source pins and data-preserving lifecycle, entirely offline."""
import copy
import hashlib
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

import azure
import contract
import deployment_profiles as profiles
import lifecycle
import manual
import preview
import reconcile
from test_lifecycle import Host
from test_manual import Metadata, VARIABLES, options
from test_reconcile import API, NOW
from test_azure import Cloud

GUID = '2e65e8a4-0f73-49b6-ae24-636a65d7e793'
OTHER = '00000000-0000-4000-8000-000000000002'
SHA = 'a' * 40


def body(values):
    return '## Changes\n\n### Preview configuration\n```json\n' + json.dumps({'profiles': values}) + '\n```\n\n## Validation\nOffline.'


class Source(Metadata):
    def __init__(self):
        super().__init__()
        self.body = body([GUID])
        self.source_sha = SHA
        for root in (preview.ROOT / (profiles.ROOT + GUID), preview.ROOT / 'deploy/bootstrap/profiles/development'):
            for file in root.rglob('*'):
                if file.is_file():
                    self.files[file.relative_to(preview.ROOT).as_posix()] = file.read_text()

    def tree(self):
        entries, directories = [], set()
        for path, content in self.files.items():
            raw = content.encode()
            digest = hashlib.sha1(b'blob ' + str(len(raw)).encode() + b'\0' + raw).hexdigest()
            entries.append({'path': path, 'type': 'blob', 'mode': '100644', 'sha': digest, 'size': len(raw)})
            directories.update(parent.as_posix() for parent in Path(path).parents if parent.as_posix() != '.')
        return {'sha': 'f' * 40, 'truncated': False,
                'tree': entries + [{'path': path, 'type': 'tree', 'mode': '040000', 'sha': 'e' * 40} for path in directories]}

    def run(self, arguments):
        if arguments[:2] == ['gh', 'api']:
            path = arguments[2]
            if '/git/commits/' in path:
                return json.dumps({'sha': self.source_sha, 'tree': {'sha': 'f' * 40}}).encode()
            if '/git/trees/' in path:
                return json.dumps(self.tree()).encode()
            if '/pulls/' in path:
                return json.dumps({'state': 'open', 'body': self.body, 'head': {'sha': self.source_sha,
                    'repo': {'full_name': 'owner/repo'}}}).encode()
        return super().run(arguments)


class ProfileTests(unittest.TestCase):
    def setUp(self):
        self.source = Source()
        self.selector = manual.PublishedImages('owner/repo', self.source.run)

    def test_visible_ordered_selection_and_invalid_metadata(self):
        self.assertEqual([OTHER, GUID], profiles.from_body(body([OTHER, GUID])))
        self.assertEqual([], profiles.from_body('No profiles requested.'))
        self.assertEqual([], profiles.from_body('<!--\n' + body([GUID]) + '\n-->'))
        self.assertEqual([], profiles.from_body('````markdown\n' + body([GUID]) + '\n````'))
        for text in (body([GUID, GUID]), body(['../bad']), body([GUID.upper()]), body([GUID]) + body([]),
                     '### Preview configuration\n```json\n{"profiles":[]} trailing\n```', body([GUID] * 17)):
            with self.subTest(text=text), self.assertRaises(ValueError):
                profiles.from_body(text)

    def test_native_json_reserved_and_ordinary_configuration(self):
        self.assertEqual('permitted', profiles.json_settings('{/*comment*/"Ordinary:Child":"permitted",}')['Ordinary:Child'])
        for text in ('{"Agentstration":{"Bootstrap":null}}', '{"AGENTSTRATION:BOOTSTRAP:Path":"x"}',
                     '{"Agentstration:AdditionalSettings":[]}', '{"Agentstration":{"AdditionalSettings:0":"x"}}',
                     '{"A":"one","a":"two"}'):
            with self.subTest(text=text), self.assertRaises(ValueError):
                profiles.json_settings(text)

    def test_manual_pr_selection_freezes_source_and_content_without_a_pr_number_in_sources(self):
        request = manual.resolve(options(revision='pr:42'), VARIABLES, 'owner/repo', self.selector)
        self.assertEqual(3, request['schemaVersion'])
        self.assertEqual(42, request['selectionPullRequest'])
        self.assertEqual([GUID], [item['guid'] for item in request['deploymentProfiles']])
        self.assertTrue(profiles.recheck(request, self.selector))
        self.source.body = body([])
        with self.assertRaisesRegex(ValueError, 'changed'):
            profiles.recheck(request, self.selector)

    def test_missing_reserved_and_escape_sources_fail_before_provider_mutation(self):
        path = profiles.settings_path(GUID)
        for value in ('{"Agentstration:Bootstrap":null}', json.dumps({GUID:{'Bootstrap':{'ProfileReferences':[{'Path':'../escape'}]}}})):
            self.source.files[path] = value
            with self.subTest(value=value), self.assertRaises(ValueError):
                profiles.pin(self.selector, [GUID], SHA)
        del self.source.files[path]
        with self.assertRaises(ValueError):
            profiles.pin(self.selector, [GUID], SHA)

    def test_source_mismatch_symlink_and_digest_drift_are_rejected(self):
        pins = profiles.pin(self.selector, [GUID], SHA)
        self.source.files[profiles.settings_path(GUID)] += '\n'
        self.assertNotEqual(pins, profiles.pin(self.selector, [GUID], SHA))
        self.source.source_sha = 'b' * 40
        with self.assertRaises(ValueError):
            profiles.pin(self.selector, [GUID], SHA)
        self.source.source_sha = SHA
        tree = self.source.tree()
        next(item for item in tree['tree'] if item['path'] == profiles.settings_path(GUID))['mode'] = '120000'
        with patch.object(self.source, 'tree', return_value=tree), self.assertRaises(ValueError):
            profiles.pin(self.selector, [GUID], SHA)

    def test_order_changes_keep_data_and_failed_updates_never_claim_applied_selection(self):
        pins = profiles.pin(self.selector, [GUID], SHA)
        from test_contract import request as docker_request
        value = docker_request()
        value.update(schemaVersion=3, extensions={}, deploymentProfiles=pins, selectionPullRequest=None)
        host = Host()
        with tempfile.TemporaryDirectory() as directory, patch.object(lifecycle, 'docker', side_effect=host.docker), \
             patch.object(lifecycle, 'inventory', side_effect=host.inventory), patch.object(preview, 'compose', side_effect=host.compose), \
             patch.object(preview, 'verify', return_value={}):
            first = lifecycle.execute('deploy', value, directory)
            state = preview.read_json(Path(directory) / value['environmentId'] / 'deployment.json')
            self.assertEqual([GUID], first['deploymentProfiles'])
            private = preview.read_json(Path(directory) / value['environmentId'] / 'server-configuration.json')
            self.assertEqual([profiles.settings_path(GUID)], private['Agentstration']['AdditionalSettings'])
            value['deploymentProfiles'] = []
            host.fail_up = True
            with self.assertRaises(RuntimeError):
                lifecycle.execute('deploy', value, directory)
            status = lifecycle.execute('status', value, directory)
            self.assertEqual([GUID], status['deploymentProfiles'])
            self.assertEqual('drifted', status['state'])
            host.fail_up = False
            second = lifecycle.execute('deploy', value, directory)
            again = preview.read_json(Path(directory) / value['environmentId'] / 'deployment.json')
            self.assertEqual(state['instanceId'], again['instanceId'])
            self.assertEqual([], second['deploymentProfiles'])
            updated_private = preview.read_json(Path(directory) / value['environmentId'] / 'server-configuration.json')
            self.assertEqual(private['Agentstration']['Bootstrap']['Secrets'], updated_private['Agentstration']['Bootstrap']['Secrets'])
            self.assertFalse(any(args[:2] == ('volume','rm') for args in host.mutations))
            with patch.object(profiles, 'pin', side_effect=AssertionError('No source lookup')):
                lifecycle.execute('destroy', value, directory)

    def test_automatic_metadata_change_is_rechecked_and_never_implicitly_resets(self):
        api = API()
        api.pull['body'] = body([GUID])
        github = reconcile.GitHub('owner/repo', api)
        cloud = Cloud()
        def execute(action, request, **kwargs):
            if action == 'deploy':
                # Guest is a provider boundary; report only completed selected initialization.
                old_transfer = cloud.transfer
                def transfer(*args, **values):
                    guest = old_transfer(*args, **values)
                    guest.update(deploymentProfiles=[p['guid'] for p in request.get('deploymentProfiles', [])], bootstrapOutcome='ready')
                    return guest
                cloud.transfer = transfer
            return azure.execute(action, request, cloud.factory, now=NOW, route_factory=lambda *args: None, **kwargs)
        def run(records):
            return reconcile.reconcile(github, records, [], VARIABLES, apply=True, execute=execute, selector=self.selector, now=NOW)
        first = run([])
        self.assertEqual([], first['failures'])
        owner = cloud.record['ownerId']
        api.pull['body'] = body([])
        records = [{'prefix': azure.prefix(cloud.record['request']), 'record': copy.deepcopy(cloud.record)}]
        second = run(records)
        self.assertEqual([], second['failures'])
        self.assertEqual(owner, cloud.record['ownerId'])
        self.assertNotIn('reset', cloud.effects)
        self.assertEqual([], cloud.record['request']['deploymentProfiles'])

    def test_saved_automatic_association_cannot_drift_from_profile_order(self):
        request = manual.resolve(options(revision='pr:42', environment_id='pr-42'), VARIABLES, 'owner/repo', self.selector)
        association = {'pullRequest':42, 'optInEvent':77, 'optInAt':NOW.isoformat(), 'sourceSha':SHA, 'profileGuids':[GUID]}
        azure.validate_automation(request, association)
        association['profileGuids'] = []
        with self.assertRaisesRegex(ValueError, 'profile order'):
            azure.validate_automation(request, association)

    def test_stale_metadata_recheck_precedes_any_azure_mutation(self):
        request = manual.resolve(options(revision='pr:42'), VARIABLES, 'owner/repo', self.selector)
        cloud = Cloud()
        self.source.body = body([])
        with self.assertRaises(ValueError):
            azure.execute('deploy', request, cloud.factory, now=NOW, route_factory=lambda *args: None,
                          guard=lambda _: profiles.recheck(request, self.selector))
        self.assertIsNone(cloud.record)
        self.assertEqual([], cloud.effects)


if __name__ == '__main__':
    unittest.main()
