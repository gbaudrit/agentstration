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
        if not re.search(r'(?m)^    uses: \./\.github/workflows/preview-environment\.yml[ \t]*$', body):
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

    def test_all_preview_callers_preserve_the_secret_boundary(self):
        found = {name: assert_secret_boundary(source) for name, source in self.workflows().items()}
        self.assertEqual('deployment', found['preview-on-demand.yml']['execute'])
        self.assertEqual('deployment', found['preview-azure-environment.yml']['execute'])
        self.assertEqual('validation', found['preview-azure-validation.yml']['portable-call'])

    def test_missing_commented_or_nested_inheritance_cannot_pass_for_deployment(self):
        for name in ('preview-on-demand.yml', 'preview-azure-environment.yml'):
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


if __name__ == '__main__':
    unittest.main()
