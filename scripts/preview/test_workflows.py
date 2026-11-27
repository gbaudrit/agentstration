"""Preview call-site security contracts; runtime secret resolution is qualified on trusted main."""
import re
import unittest

import preview


def assert_secret_boundary(source):
    """Inspect explicit call jobs in the repository's block-style workflow format."""
    _, marker, jobs = source.partition('\njobs:\n')
    if not marker:
        raise ValueError('Workflow jobs are missing.')
    parts = re.split(r'(?m)^  ([A-Za-z_][A-Za-z0-9_-]*):[ \t]*\n', jobs)
    calls = {}
    for name, body in zip(parts[1::2], parts[2::2]):
        if not re.search(r'(?m)^    uses: \./\.github/workflows/preview-(environment|on-demand)\.yml[ \t]*$', body):
            continue
        validation = re.search(r'(?m)^      operation: validate[ \t]*$', body) is not None
        inheritance = re.search(r'(?m)^    secrets: inherit[ \t]*$', body) is not None
        if validation == inheritance:
            raise ValueError('Pure validation must not inherit secrets; deployment callers must opt in.')
        calls[name] = 'validation' if validation else 'deployment'
    return calls


class WorkflowSecretTests(unittest.TestCase):
    def workflows(self):
        return {path.name: path.read_text() for path in (preview.ROOT / '.github/workflows').glob('preview*.yml')}

    def test_reconciliation_uses_only_trusted_main_and_no_candidate_artifacts_or_scripts(self):
        source = self.workflows()['preview-reconcile.yml']
        self.assertIn("github.ref == 'refs/heads/main'", source)
        self.assertIn('ref: ${{ github.sha }}', source)
        self.assertIn('schedule:', source)
        self.assertIn('workflows: [OCI images]', source)
        self.assertIn("github.event.workflow_run.event == 'workflow_dispatch'", source)
        self.assertIn('cancel-in-progress: false', source)
        for forbidden in ('pull_request_target:', 'pull_request:', 'workflow_run.head_sha', 'download-artifact', 'actions: write', 'packages: write'):
            self.assertNotIn(forbidden, source)
        self.assertIn('default: true', source)
        self.assertIn('arguments+=(--apply)', source)

    def test_private_extension_configuration_is_only_available_in_protected_executors(self):
        source = self.workflows()['preview-environment.yml']
        validation = source.split('\n  execute:')[0]
        self.assertNotIn('secrets.PREVIEW_EXTENSION_CONFIGURATION', validation)
        self.assertEqual(2, source.count('PREVIEW_EXTENSION_CONFIGURATION: ${{ secrets.PREVIEW_EXTENSION_CONFIGURATION }}'))

    def test_extension_label_sync_is_main_only_and_has_no_cloud_or_dispatch_permission(self):
        source = self.workflows()['preview-extension-labels.yml']
        self.assertIn("github.ref == 'refs/heads/main'", source)
        self.assertIn('branches: [main]', source)
        self.assertIn('issues: write', source)
        self.assertIn('-PreviewExtensionsOnly', source)
        for privileged in ('id-token:', 'environment:', 'secrets: inherit', 'actions: write', 'packages: write'):
            self.assertNotIn(privileged, source)

    def test_all_preview_callers_preserve_the_secret_boundary(self):
        found = {name: assert_secret_boundary(source) for name, source in self.workflows().items()}
        self.assertEqual('deployment', found['preview-on-demand.yml']['execute'])
        self.assertEqual('deployment', found['preview-azure-environment.yml']['execute'])
        self.assertEqual('deployment', found['preview-issue-deployment.yml']['execute'])
        self.assertEqual('validation', found['preview-azure-validation.yml']['portable-call'])

    def test_missing_commented_or_nested_inheritance_cannot_pass_for_deployment(self):
        for name in ('preview-on-demand.yml', 'preview-azure-environment.yml', 'preview-issue-deployment.yml'):
            source = self.workflows()[name]
            for replacement in ('', '    # secrets: inherit', '      secrets: inherit'):
                with self.subTest(name=name, replacement=replacement), self.assertRaises(ValueError):
                    assert_secret_boundary(source.replace('    secrets: inherit', replacement))

    def test_pr_validation_must_not_inherit_deployment_secrets(self):
        source = self.workflows()['preview-azure-validation.yml']
        unsafe = source.replace('    uses: ./.github/workflows/preview-environment.yml',
                                '    uses: ./.github/workflows/preview-environment.yml\n    secrets: inherit')
        with self.assertRaises(ValueError):
            assert_secret_boundary(unsafe)

    def test_future_deployment_caller_is_checked_without_a_filename_allowlist(self):
        source = 'name: Future preview\njobs:\n  deploy:\n    uses: ./.github/workflows/preview-environment.yml\n    with:\n      operation: deploy\n'
        with self.assertRaises(ValueError):
            assert_secret_boundary(source)

    def test_issue_admission_has_no_cloud_identity_environment_or_selected_source_checkout(self):
        source = self.workflows()['preview-issue.yml']
        self.assertIn('issue_comment:', source)
        self.assertIn('types: [created]', source)
        self.assertIn("github.ref == 'refs/heads/main'", source)
        self.assertNotIn('!github.event.issue.pull_request', source)
        self.assertIn('actions: write', source)
        self.assertIn("startsWith(github.event.comment.body, '/preview')", source)
        for forbidden in ('environment:', 'secrets:', 'id-token:', 'azure/login', 'head.sha', 'head.ref', 'run: ${{'):
            self.assertNotIn(forbidden, source)

    def test_issue_relay_does_not_loosen_the_existing_provider_event_boundary(self):
        relay = self.workflows()['preview-issue-deployment.yml']
        self.assertIn('workflow_dispatch:', relay)
        self.assertNotIn('issue_comment:', relay)
        self.assertIn('uses: ./.github/workflows/preview-on-demand.yml', relay)
        provider = self.workflows()['preview-environment.yml']
        self.assertIn("os.environ['GITHUB_EVENT_NAME'] in ('workflow_dispatch', 'push')", provider)
        self.assertNotIn("'issue_comment'", provider)

    def test_pr_ci_read_permission_reaches_selector_through_every_reusable_caller(self):
        workflows = {path.name: path.read_text() for path in
                     (preview.ROOT / '.github/workflows').glob('*.yml')}
        selector = workflows['preview-on-demand.yml'].split('\n  select:\n', 1)[1].split('\n  recorded:\n', 1)[0]
        self.assertRegex(selector, r'(?m)^      actions: read$')
        callers = []
        for filename, source in workflows.items():
            jobs = re.split(r'(?m)^  ([A-Za-z_][A-Za-z0-9_-]*):[ \t]*\n', source.partition('\njobs:\n')[2])
            for name, body in zip(jobs[1::2], jobs[2::2]):
                if re.search(r'(?m)^    uses: \./\.github/workflows/preview-on-demand\.yml[ \t]*$', body):
                    callers.append((filename, name))
                    with self.subTest(workflow=filename, job=name):
                        self.assertRegex(body, r'(?m)^      actions: read$',
                                         'The caller must permit the nested selector to read PR CI provenance.')
        self.assertIn(('preview-issue-deployment.yml', 'execute'), callers)

    def test_pr_comment_write_permission_reaches_writers_and_the_reusable_caller(self):
        admission = self.workflows()['preview-issue.yml']
        relay = self.workflows()['preview-issue-deployment.yml']
        source = self.workflows()['preview-on-demand.yml']
        for writer in (admission, relay, source.split('\n  announce:\n', 1)[1].split('\n  execute:\n', 1)[0],
                       source.split('\n  report:\n', 1)[1]):
            self.assertIn('issues: write', writer)
            self.assertIn('pull-requests: write', writer)
        boundary = source.split('\n  boundary:\n', 1)[1].split('\n  select:\n', 1)[0]
        self.assertIn('pull-requests: read', boundary)
        self.assertNotIn('pull-requests: write', boundary)
        report = source.split('\n  report:\n', 1)[1]
        self.assertIn('fromJSON(inputs.selection).tracking_feedback', report)
        self.assertIn('SELECTION_JSON: ${{ needs.boundary.outputs.selection || inputs.selection }}', report)
        self.assertIn('STAGE_RESULTS: ${{ toJSON(needs) }}', report)
        self.assertIn('SELECTION_FAILURE: ${{ needs.select.outputs.failure }}', report)

    def test_issue_execution_is_gated_by_successful_publication_and_failures_are_reported(self):
        source = self.workflows()['preview-on-demand.yml']
        execute = source.split('\n  execute:\n', 1)[1].split('\n  report:\n', 1)[0]
        self.assertIn('needs: [boundary, select, recorded, announce]', execute)
        self.assertIn("needs.announce.result == 'success'", execute)
        self.assertIn("needs.boundary.outputs.issue-number == '' && needs.announce.result == 'skipped'", execute)
        announce = source.split('\n  announce:\n', 1)[1].split('\n  execute:\n', 1)[0]
        self.assertIn('issue_commands.py plan', announce)
        self.assertIn('SELECTION_JSON: ${{ needs.boundary.outputs.selection }}', announce)
        self.assertNotIn('environment:', announce)
        self.assertNotIn('id-token:', announce)
        report = source.split('\n  report:\n', 1)[1]
        self.assertIn('always()', report)
        self.assertIn("EXECUTION_RESULT: ${{ needs.recorded.outputs.result != '' && 'success' || needs.execute.result }}", report)
        self.assertNotIn('RESULT_JSON: ${{ needs.execute.outputs.result }}', report)
        self.assertIn("RESULT_FILE: ${{ needs.execute.result == 'success' && format('{0}/preview-feedback/preview-result.json', runner.temp) || '' }}", report)
        download = report.split('uses: actions/download-artifact@v8', 1)
        self.assertIn("if: needs.execute.result == 'success'", download[0])
        self.assertIn('name: preview-result-${{ fromJSON(needs.boundary.outputs.selection || inputs.selection).environment_id }}-${{ github.run_id }}-${{ github.run_attempt }}', download[1])
        self.assertNotIn('run-id:', download[1])
        self.assertNotIn('environment:', report)
        self.assertNotIn('id-token:', report)

    def test_reset_intent_reaches_only_the_deployment_executor_without_changing_the_manual_form(self):
        source = self.workflows()['preview-on-demand.yml']
        self.assertIn('reset_before_deploy: ${{ fromJSON(needs.boundary.outputs.selection).reset_before_deploy || false }}', source)
        provider = self.workflows()['preview-environment.yml']
        manual, reusable = provider.split('  workflow_call:', 1)
        self.assertNotIn('reset_before_deploy:', manual)
        self.assertIn('reset_before_deploy:', reusable)
        self.assertIn("operation == 'deploy' and request['provider'] == 'azure-vm'", provider)
        self.assertIn('arguments+=(--reset-before-deploy)', provider)


if __name__ == '__main__':
    unittest.main()
