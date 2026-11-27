"""Status labels expose availability and operation diagnostics as independent, non-authorizing signals."""
from contextlib import redirect_stdout
import io
import json
import os
import unittest
from unittest.mock import patch

import issue_commands as commands
from test_issue_commands import GitHub, REPOSITORY


class StatusLabelsTests(unittest.TestCase):
    def setUp(self):
        self.github = GitHub()
        self.ready = {'environmentId': 'issue-17', 'state': 'running',
                      'url': 'https://dev-issue-17.agentstration.cloud'}

    def names(self):
        return {item['name'] for item in self.github.labels}

    def sync(self, result=None, **kwargs):
        commands.sync_status(REPOSITORY, 'issue-17', result, self.github, **kwargs)

    def report(self, operation, result=None, failure=None):
        options = {**commands.parse('/preview ' + operation), 'tracking_issue': '17', 'environment_id': 'issue-17'}
        variables = {'GITHUB_REPOSITORY': REPOSITORY, 'GITHUB_REF': 'refs/heads/main', 'GITHUB_RUN_ID': '1',
                     'TRACKING_ISSUE': '17', 'OPERATION': operation, 'SELECTION_JSON': json.dumps(options),
                     'EXECUTION_RESULT': 'success' if result else 'failure',
                     'RESULT_JSON': json.dumps(result) if result else '',
                     'SELECTION_FAILURE': json.dumps(failure or {})}
        with patch.dict(os.environ, variables, clear=True), patch('sys.argv', ['issue_commands.py', 'report']), \
                patch.object(commands, 'api', self.github), redirect_stdout(io.StringIO()) as output:
            code = commands.main()
        self.assertEqual(0, code, output.getvalue())

    def test_pending_update_and_failed_update_can_coexist_with_available_old_preview(self):
        self.sync(self.ready, operation='deploy', outcome='success')
        self.sync(outcome='pending', operation='deploy')
        self.assertEqual({'preview:status:deployed', 'preview:status:pending'}, self.names())
        self.sync(outcome='error', operation='deploy')
        self.assertEqual({'preview:status:deployed', 'preview:status:error'}, self.names())

    def test_successful_status_does_not_clear_an_unresolved_operation_failure_or_wait(self):
        self.sync(self.ready, operation='deploy', outcome='success')
        self.sync(operation='deploy', outcome='error')
        self.sync(operation='deploy', outcome='pending')
        self.sync(self.ready, operation='status', outcome='success')
        self.assertEqual({'preview:status:deployed', 'preview:status:pending', 'preview:status:error'}, self.names())

    def test_successful_deploy_clears_pending_and_error_without_changing_control_labels(self):
        self.github.labels = [{'name': name} for name in (
            'preview:auto', 'preview:keep', 'preview:extension:foundry', 'preview:status:pending', 'preview:status:error')]
        self.sync(self.ready, operation='deploy', outcome='success')
        self.assertEqual({'preview:auto', 'preview:keep', 'preview:extension:foundry', 'preview:status:deployed'}, self.names())

    def test_verified_bad_health_withdraws_deployed_and_marks_error(self):
        self.sync(self.ready, operation='deploy', outcome='success')
        for state in ('unhealthy', 'drifted', 'cleanup-pending'):
            with self.subTest(state=state):
                self.sync({'state': state})
                self.assertEqual({'preview:status:error'}, self.names())
        self.sync(self.ready)
        self.assertEqual({'preview:status:deployed', 'preview:status:error'}, self.names())

    def test_destroy_confirmation_clears_all_status_labels(self):
        self.sync(self.ready, operation='deploy', outcome='success')
        self.sync(operation='destroy', outcome='pending')
        self.sync(operation='destroy', outcome='error')
        self.sync({'state': 'destroyed', 'providerResourceIds': {'resources': []}},
                  operation='destroy', outcome='success')
        self.assertEqual(set(), self.names())

    def test_never_created_status_is_not_an_error_and_does_not_erase_a_failed_deploy(self):
        self.sync({'state': 'absent'})
        self.assertEqual(set(), self.names())
        self.sync(operation='deploy', outcome='error')
        self.sync({'state': 'absent'}, outcome='success')
        self.assertEqual({'preview:status:error'}, self.names())

    def test_migration_removes_legacy_label_only_and_transition_is_idempotent(self):
        self.github.labels = [{'name': 'preview:deployed'}, {'name': 'preview:keep'}]
        self.sync(self.ready)
        self.assertEqual({'preview:keep', 'preview:status:deployed'}, self.names())
        mutations = len([call for call in self.github.calls if call[1] != 'GET'])
        self.sync(self.ready)
        self.assertEqual(mutations, len([call for call in self.github.calls if call[1] != 'GET']))

    def test_cross_environment_and_unknown_outcomes_are_rejected_before_any_mutation(self):
        for result, kwargs in (({**self.ready, 'environmentId': 'issue-18'}, {}),
                               (None, {'outcome': 'unknown'})):
            with self.subTest(result=result), self.assertRaises(ValueError):
                self.sync(result, **kwargs)
        self.assertEqual([], self.github.calls)

    def test_trusted_command_dispatch_marks_waiting_but_untrusted_comment_cannot(self):
        self.assertTrue(self.github.admit())
        self.assertIn('preview:status:pending', self.names())
        other = GitHub()
        other.permission = {'permission': 'read'}
        self.assertFalse(other.admit())
        self.assertEqual([], other.labels)

    def test_report_failure_then_status_retains_error_until_successful_deploy(self):
        self.report('deploy', self.ready)
        self.report('deploy')
        self.assertEqual({'preview:status:deployed', 'preview:status:error'}, self.names())
        self.report('status', self.ready)
        self.assertIn('preview:status:error', self.names())
        self.report('deploy', self.ready)
        self.assertEqual({'preview:status:deployed'}, self.names())

    def test_missing_images_report_waiting_not_failure_and_preserves_available_preview(self):
        self.report('deploy', self.ready)
        self.report('deploy', failure={'code': 'image-unavailable', 'component': 'server'})
        self.assertEqual({'preview:status:deployed', 'preview:status:pending'}, self.names())

    def test_label_outage_does_not_suppress_an_authorized_dispatch(self):
        original = self.github.__call__
        def unavailable(path, method='GET', body=None):
            if '/labels' in path:
                raise RuntimeError('PRIVATE')
            return original(path, method, body)
        with redirect_stdout(io.StringIO()) as output:
            selector = commands.manual.PublishedImages(REPOSITORY, run=lambda arguments: json.dumps(original(arguments[2])).encode())
            self.assertTrue(commands.admit(self.github.event(), REPOSITORY, unavailable, selector))
        self.assertTrue(self.github.selection())
        self.assertNotIn('PRIVATE', output.getvalue())
