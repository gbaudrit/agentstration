"""Bounded Actions provenance, shared by publication and deployment-selection tests."""
import copy
import unittest
import ci_images
from test_reconcile import API, REPOSITORY, ROOT


def fixture(sha='a' * 40, components=('server',)):
    ci = {'id': 101, 'workflow_id': 10, 'run_attempt': 1, 'path': '.github/workflows/ci.yml',
          'event': 'pull_request', 'head_sha': sha, 'repository': {'full_name': REPOSITORY},
          'head_repository': {'full_name': REPOSITORY}, 'pull_requests': [],
          'status': 'completed', 'conclusion': 'success'}
    return {'workflow': {'id': 10}, 'ci': ci, 'runs': [ci, {**ci, 'id': 202, 'workflow_id': 20, 'path': '.github/workflows/codeql.yml'}],
            'jobs': [{'id': 301 + i, 'name': 'oci-images (' + name + ') / validate-and-build', 'run_id': 101,
                      'run_attempt': 1, 'status': 'completed', 'conclusion': 'success'} for i, name in enumerate(components)],
            'artifacts': [{'id': 501 + i, 'name': 'oci-image-' + name + '-101-1', 'expired': False,
                           'workflow_run': {'id': 101}} for i, name in enumerate(components)]}


def read(value, path):
    bare = path.partition('?')[0]
    if bare.endswith('/actions/workflows/ci.yml'):
        return copy.deepcopy(value['workflow'])
    if bare.endswith('/actions/runs'):
        return {'workflow_runs': copy.deepcopy(value['runs'])}
    if bare.endswith('/actions/runs/101'):
        return copy.deepcopy(value['ci'])
    if bare.endswith('/actions/runs/101/jobs'):
        return {'jobs': copy.deepcopy(value['jobs'])}
    if bare.endswith('/actions/runs/101/artifacts'):
        return {'artifacts': copy.deepcopy(value['artifacts'])}
    raise AssertionError('Unexpected Actions fixture endpoint: ' + path)


class ActionsAPI(API):
    def __init__(self, components=('server',)):
        super().__init__()
        self.actions = fixture(components=components)
        self.statuses, self.dispatches = [], []

    def __call__(self, path, method='GET', body=None):
        if method == 'GET' and '/statuses?' in path:
            return [{**copy.deepcopy(s), 'creator': {'login': 'github-actions[bot]'}} for s in reversed(self.statuses)]
        if method == 'POST' and '/statuses/' in path:
            self.calls.append((path, method, copy.deepcopy(body)))
            self.statuses.append(copy.deepcopy(body))
            return None
        if method == 'POST' and path.endswith('/dispatches'):
            self.calls.append((path, method, copy.deepcopy(body)))
            self.dispatches.append(copy.deepcopy(body))
            return None
        if '/actions/' in path:
            self.calls.append((path, method, copy.deepcopy(body)))
            return read(self.actions, path)
        return super().__call__(path, method, body)


class CIImageTests(unittest.TestCase):
    def setUp(self):
        self.api = ActionsAPI()

    def context(self, **kwargs):
        return ci_images.context(REPOSITORY, self.api, 1, 'a' * 40, **kwargs)

    def test_reads_exact_ci_and_immutable_artifact_without_a_build_or_registry_access(self):
        value = self.context(component='server')
        self.assertEqual({'run': 101, 'attempt': 1, 'components': ['server'],
                          'artifact': 501, 'name': 'oci-image-server-101-1'}, value)
        self.assertTrue(all(method == 'GET' for _, method, _ in self.api.calls))

    def test_no_image_jobs_means_all_components_are_reused(self):
        self.api.actions = fixture(components=())
        self.assertEqual([], self.context()['components'])
        with self.assertRaises(ValueError):
            self.context(component='server')

    def test_checks_wait_for_all_latest_pr_workflows_and_block_failures(self):
        other = self.api.actions['runs'][1]
        other.update(status='in_progress', conclusion=None)
        with self.assertRaises(ci_images.Waiting) as caught:
            self.context()
        self.assertFalse(caught.exception.failed)
        other.update(status='completed', conclusion='failure')
        with self.assertRaises(ci_images.Waiting) as caught:
            self.context()
        self.assertTrue(caught.exception.failed)
        self.api.actions['runs'].append({**other, 'id': 203, 'conclusion': 'success'})
        self.assertEqual(['server'], self.context()['components'])

    def test_old_sha_forks_wrong_workflows_run_and_attempt_cannot_supply_archives(self):
        for changes in ({'head_sha': 'b' * 40}, {'head_repository': {'full_name': 'fork/repo'}},
                        {'repository': {'full_name': 'other/repo'}}, {'path': '.github/workflows/other.yml'},
                        {'event': 'push'}):
            with self.subTest(changes=changes):
                self.setUp()
                self.api.actions['ci'].update(changes)
                with self.assertRaises(ValueError):
                    self.context(component='server')
        self.setUp()
        for changes in ({'run_id': 102}, {'attempt': 2}):
            with self.assertRaises(ValueError):
                self.context(**changes)

    def test_failed_job_rerun_reuses_the_original_successful_image_artifact(self):
        self.api.actions['ci']['run_attempt'] = 2
        value = self.context(attempt=2, component='server')
        self.assertEqual(2, value['attempt'])
        self.assertEqual('oci-image-server-101-1', value['name'])
        self.api.actions['jobs'].append({**self.api.actions['jobs'][0], 'id': 401, 'run_attempt': 2, 'conclusion': 'failure'})
        with self.assertRaises(ValueError):
            self.context(component='server')

    def test_missing_expired_duplicate_or_foreign_artifact_never_falls_back_to_building(self):
        mutations = (lambda v: v.update(artifacts=[]), lambda v: v['artifacts'][0].update(expired=True),
                     lambda v: v['artifacts'].append(copy.deepcopy(v['artifacts'][0])),
                     lambda v: v['artifacts'][0].update(workflow_run={'id': 102}))
        for mutate in mutations:
            self.setUp()
            mutate(self.api.actions)
            with self.assertRaises(ValueError):
                self.context(component='server')


if __name__ == '__main__':
    unittest.main()
