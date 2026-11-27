"""Automatic publication authorizes live PR metadata and treats candidate archives only as data."""
import copy
import hashlib
import json
import os
import re
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import Mock, patch

import image_publication as publication
import images
import preview
import reconcile
from test_reconcile import API, REPOSITORY
from test_ci_images import ActionsAPI, fixture


class PublicationTests(unittest.TestCase):
    def setUp(self):
        self.api = ActionsAPI()
        self.github = reconcile.GitHub(REPOSITORY, self.api)
        self.sha = 'a' * 40
        self.event = {'number': 1, 'action': 'synchronize', 'pull_request': copy.deepcopy(self.api.pull)}
        self.selector = Mock()
        self.entry = images.catalogue('server')
        self.selector.entry.return_value = self.entry
        self.selector.component.side_effect = images.catalogue

    def test_push_and_late_opt_in_dispatch_only_fixed_main_server_preview(self):
        for action in ('synchronize', 'labeled', 'reopened', 'ready_for_review'):
            event = {**self.event, 'action': action, 'label': {'name': reconcile.LABEL}}
            calls = []
            def call(path, method='GET', body=None):
                if path.endswith('/dispatches'):
                    calls.append((path, method, body))
                else:
                    return self.api(path, method, body)
            github = reconcile.GitHub(REPOSITORY, call)
            self.assertTrue(publication.dispatch(github, 1, event))
            self.assertEqual([('repos/owner/repo/actions/workflows/oci-images.yml/dispatches', 'POST', {
                'ref': 'main', 'inputs': {'extension': 'server', 'publish_preview': True,
                                        'preview_pr': '1', 'source_sha': self.sha,
                                        'ci_run_id': '101', 'ci_run_attempt': '1'}})], calls)

    def test_no_opt_in_stale_events_and_status_labels_do_not_dispatch(self):
        for event in ({**self.event, 'action': 'labeled', 'label': {'name': 'preview:status:error'}},
                      {**self.event, 'pull_request': {'head': {'sha': 'b' * 40}}}):
            self.assertFalse(publication.dispatch(self.github, 1, event))
        self.api.pull['labels'] = []
        self.assertFalse(publication.dispatch(self.github, 1))

    def test_forks_drafts_closed_bots_and_read_only_grants_cannot_authorize(self):
        mutations = (lambda: self.api.pull.update(state='closed'), lambda: self.api.pull.update(draft=True),
                     lambda: self.api.pull['head']['repo'].update(full_name='fork/repo'),
                     lambda: self.api.events[0]['actor'].update(type='Bot'),
                     lambda: setattr(self.api, 'permission', 'read'))
        for mutate in mutations:
            self.setUp()
            mutate()
            if self.api.pull['state'] == 'closed' or self.api.pull['draft']:
                self.assertFalse(publication.dispatch(self.github, 1))
                continue
            with self.assertRaises(ValueError):
                publication.dispatch(self.github, 1)

    def test_late_extension_label_promotes_its_existing_ci_archive_without_rebuilding(self):
        self.api.actions = fixture(components=['foundry'])
        self.api.pull['labels'].append({'name': 'preview:extension:foundry'})
        event = {**self.event, 'action': 'labeled', 'label': {'name': 'preview:extension:foundry'}}
        self.assertTrue(publication.dispatch(self.github, 1, event))
        self.assertEqual(['foundry'], [d['inputs']['extension'] for d in self.api.dispatches])

    def test_opt_out_finishes_pending_publication_status_without_dispatching(self):
        self.api.actions['ci']['status'] = 'in_progress'
        self.assertFalse(publication.dispatch(self.github, 1, self.event))
        self.api.pull['labels'] = []
        event = {**self.event, 'action': 'unlabeled', 'label': {'name': 'preview:auto'}}
        self.assertFalse(publication.dispatch(self.github, 1, event))
        self.assertEqual('success', self.api.statuses[-1]['state'])
        self.assertEqual([], self.api.dispatches)

    def test_repeated_wait_events_do_not_append_identical_commit_statuses(self):
        self.api.actions['ci']['status'] = 'in_progress'
        self.assertFalse(publication.dispatch(self.github, 1, self.event))
        count = len(self.api.statuses)
        self.assertFalse(publication.dispatch(self.github, 1, self.event))
        self.assertEqual(count, len(self.api.statuses))

    def test_gate_never_builds_and_only_promotes_a_definitely_missing_private_image(self):
        missing = subprocess.CompletedProcess([], 1, b'', b'manifest unknown')
        with patch.object(publication.subprocess, 'run', return_value=missing):
            self.assertEqual({'build': 'false', 'publish': 'true', 'grant': '77', 'artifact-id': '501', 'ci-run': '101'},
                             publication.needs_publication(self.github, 1, self.sha, 'server', 101, 1, self.selector))
        self.selector.private_package.assert_called_once_with('server')
        self.selector.select.assert_not_called()
        for message in (b'unauthorized', b'connection refused', b'too many requests'):
            with patch.object(publication.subprocess, 'run', return_value=subprocess.CompletedProcess([], 1, b'', message)):
                with self.assertRaises(ValueError):
                    publication.needs_publication(self.github, 1, self.sha, 'server', 101, 1, self.selector)

    def test_three_requested_selective_build_cases_dispatch_only_affected_required_components(self):
        for changed, selected in ((['foundry'], ['foundry', 'git']), (['foundry', 'git'], ['foundry', 'git', 'openapi']), (['server'], ['foundry'])):
            with self.subTest(changed=changed):
                self.setUp()
                self.api.actions = fixture(components=changed)
                self.api.pull['labels'] += [{'name': 'preview:extension:' + name} for name in selected]
                self.assertTrue(publication.dispatch(self.github, 1, self.event))
                self.assertEqual(changed, [d['inputs']['extension'] for d in self.api.dispatches])
                statuses = {s['context']: s['state'] for s in self.api.statuses}
                for name in ['server', 'maf-worker', *selected]:
                    self.assertEqual('pending' if name in changed else 'success', statuses[publication.CONTEXT + name])

    def test_worker_only_change_and_unselected_extension_do_not_force_server_build_or_publication(self):
        self.api.actions = fixture(components=['maf-worker', 'foundry'])
        self.assertTrue(publication.dispatch(self.github, 1, self.event))
        self.assertEqual(['maf-worker'], [d['inputs']['extension'] for d in self.api.dispatches])

    def test_all_reused_components_wake_deployment_without_any_oci_dispatch(self):
        self.api.actions = fixture(components=[])
        self.assertTrue(publication.dispatch(self.github, 1, self.event))
        self.assertEqual([{'ref': 'main', 'inputs': {'dry_run': False}}], self.api.dispatches)
        self.assertTrue(all(s['state'] == 'success' for s in self.api.statuses))

    def test_pending_or_failed_pr_workflows_do_not_dispatch_publication(self):
        other = self.api.actions['runs'][1]
        for status, conclusion, expected in (('in_progress', None, 'pending'), ('completed', 'failure', 'failure')):
            other.update(status=status, conclusion=conclusion)
            self.assertFalse(publication.dispatch(self.github, 1, self.event))
            self.assertEqual([], self.api.dispatches)
            self.assertEqual(expected, self.api.statuses[-1]['state'])

    def test_existing_image_is_verified_and_skips_build(self):
        with patch.object(publication.subprocess, 'run', return_value=subprocess.CompletedProcess([], 0, b'index', b'')):
            self.assertEqual('false', publication.needs_publication(self.github, 1, self.sha, 'server', 101, 1, self.selector)['publish'])
        self.selector.select.assert_called_once_with('server', self.sha)

    def test_private_package_denial_and_head_change_during_gate_cannot_start_build(self):
        self.selector.private_package.side_effect = ValueError('not private')
        with patch.object(publication.subprocess, 'run') as inspect:
            with self.assertRaises(ValueError):
                publication.needs_publication(self.github, 1, self.sha, 'server', 101, 1, self.selector)
            inspect.assert_not_called()
        self.selector.private_package.side_effect = None
        def inspect(*args, **kwargs):
            self.api.pull['head']['sha'] = 'b' * 40
            return subprocess.CompletedProcess([], 1, b'', b'manifest unknown')
        with patch.object(publication.subprocess, 'run', side_effect=inspect):
            with self.assertRaises(ValueError):
                publication.needs_publication(self.github, 1, self.sha, 'server', 101, 1, self.selector)

    def test_command_line_rejects_non_main_and_non_sha_sources_before_mutation(self):
        for variables in ({'GITHUB_REF': 'refs/heads/candidate', 'SOURCE_SHA': self.sha},
                          {'GITHUB_REF': 'refs/heads/main', 'SOURCE_SHA': 'feature/branch'}):
            with patch.dict(os.environ, {**variables, 'GITHUB_EVENT_NAME': 'workflow_dispatch',
                                         'GITHUB_REPOSITORY': REPOSITORY, 'PREVIEW_PR': '1'}), \
                    patch('sys.argv', ['image_publication.py', 'gate']), \
                    patch.object(reconcile, 'GitHub') as github:
                with self.assertRaises(ValueError):
                    publication.main()
                github.return_value.call.assert_not_called()

    def test_stale_head_or_new_grant_blocks_gate_and_publication(self):
        for sha, grant in (('b' * 40, 77), (self.sha, 78)):
            with self.assertRaises(ValueError):
                publication.approval(self.github, 1, sha, grant)

    def receipt(self, root):
        archive = root / 'image.oci.tar'
        archive.write_bytes(b'offline archive')
        raw = b'index bytes'
        delivery = {'schemaVersion': 1, 'component': self.entry, 'repository': REPOSITORY, 'sourceSha': self.sha,
                    'digest': 'sha256:' + hashlib.sha256(raw).hexdigest(), 'archive': archive.name,
                    'archiveSha256': hashlib.sha256(archive.read_bytes()).hexdigest()}
        path = root / 'delivery.json'
        path.write_text(json.dumps(delivery))
        config = {'os': 'linux', 'architecture': 'amd64', 'config': {'Labels': {
            'org.opencontainers.image.revision': self.sha, 'org.opencontainers.image.source': 'https://github.com/' + REPOSITORY,
            'org.opencontainers.image.version': self.entry['version']}}}
        return path, delivery, raw, config

    def test_publish_verifies_data_and_copies_only_fixed_private_server_without_checkout_or_build(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            path, delivery, raw, config = self.receipt(root)
            with patch.object(publication.manual, 'command', side_effect=[raw, json.dumps(config).encode()]) as inspect, \
                    patch.object(images, 'private_package') as private, patch.object(images, 'copy_archive') as copy_archive:
                value = publication.publish(self.github, 1, self.sha, 77, path, root / 'auth.json', self.selector,
                                            component='server', run_id=101, attempt=1)
            self.assertEqual('ghcr.io/owner/repo-server@' + delivery['digest'], value['reference'])
            self.assertEqual(value, json.loads((root / 'publication-ghcr.json').read_text()))
            self.assertEqual(2, private.call_count)
            private.assert_called_with(REPOSITORY, 'repo-server')
            copy_archive.assert_called_once_with(path, 'ghcr.io/owner/repo-server:sha-' + self.sha, root / 'auth.json')
            self.assertTrue(all(call.args[0][0] == 'skopeo' for call in inspect.call_args_list))

    def test_same_archive_publisher_supports_worker_and_requested_aep_with_fixed_namespaces(self):
        for name in ('maf-worker', 'foundry'):
            self.setUp()
            self.api.actions = fixture(components=[name])
            if name == 'foundry':
                self.api.pull['labels'].append({'name': 'preview:extension:foundry'})
            self.entry = images.catalogue(name)
            self.selector.entry.return_value = self.entry
            with tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                path, delivery, raw, config = self.receipt(root)
                with patch.object(publication.manual, 'command', side_effect=[raw, json.dumps(config).encode()]), \
                        patch.object(images, 'private_package') as private, patch.object(images, 'copy_archive') as copier:
                    value = publication.publish(self.github, 1, self.sha, 77, path, root / 'auth.json', self.selector,
                                                component=name, run_id=101, attempt=1)
                self.assertIn('/repo-' + self.entry['imageName'] + '@', value['reference'])
                self.assertEqual(501, value['artifactId'])
                copier.assert_called_once()
                private.assert_called_with(REPOSITORY, 'repo-' + self.entry['imageName'])

    def test_unrequested_extension_cannot_be_published_even_with_a_successful_ci_archive(self):
        self.api.actions = fixture(components=['foundry'])
        with self.assertRaises(ValueError), patch.object(images, 'copy_archive') as copier:
            publication.publish(self.github, 1, self.sha, 77, Path('not-read.json'), Path('not-read-auth.json'), self.selector,
                                component='foundry', run_id=101, attempt=1)
        copier.assert_not_called()

    def test_receipt_tampering_archive_escape_checksum_and_config_mismatch_cannot_write(self):
        changes = (
            lambda d, c: d.update(schemaVersion=True),
            lambda d, c: d.update(repository='other/repo'), lambda d, c: d.update(sourceSha='b' * 40),
            lambda d, c: d.update(component=images.catalogue('foundry')),
            lambda d, c: d.update(archive='../image.oci.tar'), lambda d, c: d.update(archiveSha256='0' * 64),
            lambda d, c: d.update(digest='sha256:' + '0' * 64),
            lambda d, c: c.update(architecture='arm64'),
            lambda d, c: c['config']['Labels'].update({'org.opencontainers.image.revision': 'b' * 40}),
            lambda d, c: c['config']['Labels'].update({'org.opencontainers.image.source': 'https://github.com/fork/repo'}),
            lambda d, c: c['config']['Labels'].update({'org.opencontainers.image.version': '99.0.0'}))
        for change in changes:
            with tempfile.TemporaryDirectory() as directory:
                path, delivery, raw, config = self.receipt(Path(directory))
                change(delivery, config)
                path.write_text(json.dumps(delivery))
                with patch.object(publication.manual, 'command', side_effect=[raw, json.dumps(config).encode()]), \
                        patch.object(images, 'private_package') as private, patch.object(images, 'copy_archive') as copy_archive:
                    with self.assertRaises(ValueError):
                        publication.publish(self.github, 1, self.sha, 77, path, Path(directory) / 'auth.json', self.selector,
                                            component='server', run_id=101, attempt=1)
                private.assert_not_called()
                copy_archive.assert_not_called()

    def test_removed_opt_in_during_archive_verification_prevents_copy(self):
        with tempfile.TemporaryDirectory() as directory:
            path, delivery, raw, config = self.receipt(Path(directory))
            def inspect(command):
                if '--config' in command:
                    self.api.pull['labels'] = []
                    return json.dumps(config).encode()
                return raw
            with patch.object(publication.manual, 'command', side_effect=inspect), \
                    patch.object(images, 'private_package'), patch.object(images, 'copy_archive') as copy_archive:
                with self.assertRaises(ValueError):
                    publication.publish(self.github, 1, self.sha, 77, path, Path(directory) / 'auth.json', self.selector,
                                        component='server', run_id=101, attempt=1)
            copy_archive.assert_not_called()

    def test_failure_reports_use_owned_card_without_claiming_environment_removal(self):
        self.api.pull['labels'].append({'name': 'preview:status:deployed'})
        with patch.dict(os.environ, {'GITHUB_RUN_ID': '123'}):
            publication.report_failure(self.github, 1, self.sha, 77)
            publication.report_failure(self.github, 1, self.sha, 77)
        self.assertEqual(1, len(self.api.comments))
        self.assertIn('/actions/runs/123', self.api.comments[0]['body'])
        self.assertIn('did not start a deployment', self.api.comments[0]['body'])
        self.assertEqual({'preview:auto', 'preview:status:deployed', 'preview:status:error'},
                         {label['name'] for label in self.api.pull['labels']})
        self.api.pull['head']['sha'] = 'b' * 40
        with self.assertRaises(ValueError):
            publication.report_failure(self.github, 1, self.sha, 77)


class PublicationWorkflowTests(unittest.TestCase):
    def test_completion_listener_covers_all_independent_pr_workflows(self):
        root = preview.ROOT / '.github/workflows'
        admission = (root / 'preview-image-publication.yml').read_text()
        names = set(re.search(r'workflows: \[([^\]]+)\]', admission)[1].split(', '))
        for path in root.glob('*.yml'):
            source = path.read_text()
            if re.search(r'(?m)^  pull_request:|^on:.*\bpull_request\b', source):
                name = re.search(r'(?m)^name: (.+)$', source)[1]
                self.assertIn(name, names, 'PR workflow must wake deferred publication: ' + path.name)
        ci = (root / 'ci.yml').read_text()
        self.assertIn('source-sha: ${{ github.event.pull_request.head.sha || github.sha }}', ci)
        self.assertNotIn('PreviewServer', ci)

    def test_pr_event_admission_never_checks_out_or_executes_candidate_code(self):
        workflow = (preview.ROOT / '.github/workflows/preview-image-publication.yml').read_text()
        self.assertIn('pull_request_target:', workflow)
        self.assertIn('synchronize, labeled', workflow)
        self.assertIn('ref: ${{ github.sha }}', workflow)
        self.assertIn('actions: write', workflow)
        for forbidden in ('ref: ${{ github.event.pull_request.head.sha }}', 'ref: ${{ github.event.workflow_run.head_sha }}',
                          'head.ref', 'environment:', 'secrets:', 'packages:', 'id-token:', 'download-artifact', 'azure/login'):
            self.assertNotIn(forbidden, workflow)

    def test_candidate_build_has_no_publishing_permissions_and_main_publisher_never_checks_out_candidate(self):
        root = preview.ROOT / '.github/workflows'
        builder = (root / 'validate-oci-image.yml').read_text()
        workflow = (root / 'oci-images.yml').read_text()
        self.assertIn('ref: ${{ inputs.source-sha || github.sha }}', builder)
        for forbidden in ('packages: write', 'actions: write', 'secrets:', 'environment:', 'id-token:'):
            self.assertNotIn(forbidden, builder)
        publisher = workflow.split('\n  publish:\n')[1].split('\n  report-publication-result:\n')[0]
        self.assertNotIn('ref:', publisher)
        self.assertNotIn('uses: ./.github/workflows/validate', publisher)
        self.assertIn('image_publication.py publish', publisher)
        self.assertIn("if: inputs.preview_pr == ''", publisher)
        self.assertIn("if: github.event_name == 'push' && github.ref_type == 'tag'", publisher)
        self.assertIn("needs.admission.outputs.build == 'true'", workflow)
        self.assertIn("format('pr-{0}-{1}', inputs.preview_pr, inputs.source_sha)", workflow)
        self.assertIn('cancel-in-progress: false', workflow)
        self.assertIn('artifact-ids: ${{ needs.admission.outputs.artifact-id }}', publisher)
        self.assertIn('run-id: ${{ needs.admission.outputs.ci-run }}', publisher)
        admission = workflow.split('\n  admission:\n')[1].split('\n  validate-and-build:\n')[0]
        self.assertIn('actions: read', admission)


if __name__ == '__main__':
    unittest.main()
