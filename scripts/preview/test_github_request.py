"""GitHub defaults cannot change recorded provider identity or hide unsupported selections."""
import json
import unittest

import contract
import github_request
from test_azure import azure_request


class GitHubRequestTests(unittest.TestCase):
    variables = {'AZURE_PREVIEW_IMAGE_VERSION': '24.04.202611170', 'AZURE_PREVIEW_OS_PROFILE': 'ubuntu-24.04',
                 'AZURE_PREVIEW_VM_SIZE1': 'Standard_B2ms', 'AZURE_PREVIEW_VM_SIZE2': 'Standard_B4ms'}

    def test_missing_values_resolve_once_and_remain_canonical_when_variables_change(self):
        value = azure_request()
        del value['target']['vmSize'], value['target']['imageVersion']
        selected = github_request.resolve(json.dumps(value), self.variables, '2', 'deploy')
        self.assertEqual('Standard_B4ms', selected['target']['vmSize'])
        self.assertEqual('24.04.202611170', selected['target']['imageVersion'])
        self.assertEqual('ubuntu-24.04', selected['target']['osProfile'])
        changed = {**self.variables, 'AZURE_PREVIEW_IMAGE_VERSION': 'latest', 'AZURE_PREVIEW_OS_PROFILE': 'debian'}
        self.assertEqual(selected, github_request.resolve(json.dumps(selected), changed, action='deploy'))

    def test_explicit_values_override_defaults_and_empty_invalid_profiles_fail_before_execution(self):
        value = azure_request()
        self.assertEqual(value['target']['imageVersion'],
                         github_request.resolve(json.dumps(value), self.variables)['target']['imageVersion'])
        for profile in ('3', '4', '5', '6'):
            with self.subTest(profile=profile), self.assertRaises(ValueError):
                github_request.resolve(json.dumps(value), self.variables, profile, 'deploy')
        with self.assertRaises(ValueError):
            github_request.resolve(json.dumps(value), self.variables, '2', 'deploy')
        del value['target']['vmSize']
        with self.assertRaises(ValueError):
            github_request.resolve(json.dumps(value), {'AZURE_PREVIEW_VM_SIZE1': 'Standard_D64s_v5'}, action='deploy')

    def test_status_destroy_ignore_changed_defaults_and_keep_legacy_requests_usable(self):
        value = azure_request()
        for action in ('status', 'destroy'):
            with self.subTest(action=action):
                self.assertEqual(value, github_request.resolve(json.dumps(value), {'AZURE_PREVIEW_OS_PROFILE': 'unsupported'}, action=action))
                with self.assertRaises(ValueError):
                    github_request.resolve(json.dumps(value), self.variables, '1', action)
        explicit = {**value, 'target': {**value['target'], 'osProfile': contract.AZURE_OS_PROFILE}}
        self.assertEqual(contract.azure_target(value), contract.azure_target(explicit))

    def test_unknown_os_and_duplicate_or_secret_fields_are_rejected(self):
        value = azure_request()
        with self.assertRaises(ValueError):
            github_request.resolve(json.dumps(value), {'AZURE_PREVIEW_OS_PROFILE': 'debian-12'})
        value['target']['password'] = 'disposable-fixture'
        with self.assertRaises(ValueError):
            github_request.resolve(json.dumps(value), self.variables)
        with self.assertRaises(ValueError):
            github_request.resolve('{"provider":"azure-vm","provider":"docker"}', self.variables)

    def test_resolved_defaults_cannot_exceed_the_portable_request_size_limit(self):
        value = azure_request()
        del value['target']['imageVersion']
        variables = {**self.variables, 'AZURE_PREVIEW_IMAGE_VERSION': '24.04.' + '1' * 65536}
        with self.assertRaises(ValueError):
            github_request.resolve(json.dumps(value), variables)
