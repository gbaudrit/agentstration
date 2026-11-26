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
        self.assertIn('!github.event.issue.pull_request', source)
        self.assertIn('actions: write', source)
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
        self.assertIn('EXECUTION_RESULT: ${{ needs.execute.result }}', report)
        self.assertIn('RESULT_JSON: ${{ needs.execute.outputs.result }}', report)


if __name__ == '__main__':
    unittest.main()
