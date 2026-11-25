"""Offline security checks for the shared Access preflight, including the real HTTP boundary."""
import copy
import io
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import Mock, patch
import urllib.error

import cloudflare

ACCOUNT = 'a' * 32
ZONE = 'b' * 32
APP = '00000000-0000-4000-8000-000000000001'
IDP = '00000000-0000-4000-8000-000000000002'
GROUP = '00000000-0000-4000-8000-000000000003'
TOKEN = '00000000-0000-4000-8000-000000000004'
ROOT = '/accounts/' + ACCOUNT
APP_PATH = ROOT + '/access/apps/' + APP


def environment():
    return {'CLOUDFLARE_' + key: value for key, value in {
        'ACCOUNT_ID': ACCOUNT, 'ZONE_ID': ZONE, 'ACCESS_APP_ID': APP,
        'TEAM_DOMAIN': 'agentstration.cloudflareaccess.com', 'PREVIEW_DOMAIN': 'agentstration.cloud',
        'API_TOKEN': 'private-api-fixture', 'ACCESS_CLIENT_ID': 'c' * 32 + '.access',
        'ACCESS_CLIENT_SECRET': 'private-service-fixture'}.items()}


class Cloud:
    def __init__(self):
        self.reads = []
        self.organization = {'auth_domain': 'agentstration.cloudflareaccess.com'}
        self.app = {'id': APP, 'type': 'self_hosted', 'domain': 'dev-*.agentstration.cloud',
                    'aud': 'd' * 64, 'allowed_idps': [IDP]}
        self.provider = {'type': 'azureAD', 'config': {'support_groups': True,
                         'client_id': APP, 'directory_id': IDP, 'client_secret': 'private-entra-fixture'}}
        self.policies = [
            {'decision': 'allow', 'include': [{'azureAD': {'id': GROUP, 'identity_provider_id': IDP}}]},
            {'decision': 'non_identity', 'include': [{'service_token': {'token_id': TOKEN}}]}]
        self.apps = [self.app]

    def get(self, path):
        self.reads.append(('get', path))
        return {'success': True, 'result': copy.deepcopy({
            ROOT + '/access/organizations': self.organization, APP_PATH: self.app,
            ROOT + '/access/identity_providers/' + IDP: self.provider}[path])}

    def list(self, path):
        self.reads.append(('list', path))
        return copy.deepcopy({APP_PATH + '/policies': self.policies, ROOT + '/access/apps': self.apps,
                              ROOT + '/cfd_tunnel': [], '/zones/' + ZONE + '/dns_records': []}[path])


class AccessAuditTests(unittest.TestCase):
    def setUp(self):
        self.env = environment()
        self.cloud = Cloud()

    def audit(self):
        return cloudflare.audit(cloudflare.configuration(self.env), self.cloud)

    def test_valid_shared_policies_are_read_only_and_do_not_claim_end_to_end_protection(self):
        result = self.audit()
        self.assertEqual('configuration-validated', result['state'])
        self.assertEqual('configured-not-exercised', result['automationCredentials'])
        self.assertEqual('not-created', result['deployment'])
        self.assertIsNone(result['url'])
        self.assertIn(('list', ROOT + '/cfd_tunnel'), self.cloud.reads)
        self.assertIn(('list', '/zones/' + ZONE + '/dns_records'), self.cloud.reads)
        for secret in ('private-api-fixture', 'private-service-fixture', 'private-entra-fixture'):
            self.assertNotIn(secret, json.dumps(result))

    def test_every_missing_protected_value_fails_before_network_access(self):
        for key in self.env:
            for value in ('', 'value\nwith-newline'):
                with self.subTest(key=key, value=value):
                    changed = {**self.env, key: value}
                    with self.assertRaisesRegex(ValueError, key):
                        cloudflare.configuration(changed)
        self.assertEqual([], self.cloud.reads)

    def test_wrong_identifier_types_and_url_inputs_are_rejected(self):
        cases = {'ACCOUNT_ID': APP, 'ZONE_ID': '../other', 'ACCESS_APP_ID': 'd' * 64,
                 'TEAM_DOMAIN': 'https://agentstration.cloudflareaccess.com',
                 'PREVIEW_DOMAIN': '*.agentstration.cloud', 'ACCESS_CLIENT_ID': APP}
        for key, value in cases.items():
            with self.subTest(key=key), self.assertRaises(ValueError):
                cloudflare.configuration({**self.env, 'CLOUDFLARE_' + key: value})

    def test_wrong_cloudflare_team_is_rejected(self):
        self.cloud.organization['auth_domain'] = 'another-team.cloudflareaccess.com'
        with self.assertRaisesRegex(ValueError, 'TEAM_DOMAIN'):
            self.audit()

    def test_wrong_app_scope_audience_and_alternative_auth_paths_are_rejected(self):
        for field, value in (('domain', '*.agentstration.cloud'), ('domain', 'dev-*.agentstration.cloud/private'),
                             ('type', 'saas'), ('id', IDP), ('aud', ''), ('options_preflight_bypass', True),
                             ('allow_authenticate_via_warp', True), ('read_service_tokens_from_header', 'Authorization'),
                             ('self_hosted_domains', ['dev-*.agentstration.cloud', 'public.agentstration.cloud']),
                             ('destinations', [{'type': 'public', 'uri': 'public.agentstration.cloud'}])):
            with self.subTest(field=field, value=value):
                self.cloud = Cloud()
                self.cloud.app[field] = value
                with self.assertRaises(ValueError):
                    self.audit()

    def test_explicit_public_destination_for_the_same_pattern_is_supported(self):
        self.cloud.app['self_hosted_domains'] = ['dev-*.agentstration.cloud']
        self.cloud.app['destinations'] = [{'type': 'public', 'uri': 'dev-*.agentstration.cloud'}]
        self.assertEqual('configuration-validated', self.audit()['state'])

    def test_all_identity_providers_and_non_entra_providers_are_rejected(self):
        for providers in ([], [IDP, APP], None):
            with self.subTest(providers=providers), self.assertRaises(ValueError):
                self.cloud.app['allowed_idps'] = providers
                self.audit()
        self.cloud.app['allowed_idps'] = [IDP]
        self.cloud.provider['type'] = 'onetimepin'
        with self.assertRaises(ValueError):
            self.audit()

    def test_group_support_and_valid_entra_identity_are_required(self):
        for key, value in (('support_groups', False), ('directory_id', 'unknown'), ('client_id', None)):
            with self.subTest(key=key), self.assertRaises(ValueError):
                self.cloud = Cloud()
                self.cloud.provider['config'][key] = value
                self.audit()

    def test_everyone_email_and_additional_or_include_rules_cannot_broaden_human_access(self):
        group = self.cloud.policies[0]['include'][0]
        for rules in ([{'everyone': {}}], [{'email_domain': {'domain': 'example.com'}}],
                      [group, {'everyone': {}}], [group, {'email': {'email': 'user@example.com'}}]):
            with self.subTest(rules=rules), self.assertRaises(ValueError):
                self.cloud.policies[0]['include'] = rules
                self.audit()

    def test_group_must_use_the_selected_identity_provider_and_object_id(self):
        for key, value in (('identity_provider_id', APP), ('id', 'Agentstration-Preview-Users')):
            with self.subTest(key=key), self.assertRaises(ValueError):
                self.cloud = Cloud()
                self.cloud.policies[0]['include'][0]['azureAD'][key] = value
                self.audit()

    def test_requiring_the_selected_entra_login_method_is_supported(self):
        self.cloud.policies[0]['require'] = [{'login_method': {'id': IDP}}]
        self.assertEqual('configuration-validated', self.audit()['state'])
        self.cloud.policies[0]['require'][0]['login_method']['id'] = APP
        with self.assertRaises(ValueError):
            self.audit()

    def test_service_auth_requires_one_specific_token_and_no_interactive_requirements(self):
        changes = ({'decision': 'allow'}, {'decision': 'bypass'}, {'include': [{'any_valid_service_token': {}}]},
                   {'include': [{'service_token': {'token_id': 'not-a-token-id'}}]},
                   {'require': [{'login_method': {'id': IDP}}]}, {'exclude': [{'everyone': {}}]})
        for change in changes:
            with self.subTest(change=change), self.assertRaises(ValueError):
                self.cloud = Cloud()
                self.cloud.policies[1].update(change)
                self.audit()

    def test_additional_policies_including_bypass_or_block_everyone_require_review(self):
        for decision in ('bypass', 'deny', 'allow', 'non_identity'):
            with self.subTest(decision=decision), self.assertRaises(ValueError):
                self.cloud = Cloud()
                self.cloud.policies.append({'decision': decision, 'include': [{'everyone': {}}]})
                self.audit()

    def test_an_application_overriding_a_preview_or_path_is_rejected(self):
        for domain in ('dev-pr-123.agentstration.cloud', 'dev-pr-123.agentstration.cloud/api',
                       'dev-*.agentstration.cloud/public', '*.agentstration.cloud',
                       'DEV-PR-123.AGENTSTRATION.CLOUD', 'https://dev-pr-123.agentstration.cloud/api',
                       'dev-pr-123.agentstration.cloud.:443'):
            with self.subTest(domain=domain), self.assertRaisesRegex(ValueError, 'overlaps'):
                self.cloud.apps = [self.cloud.app, {'id': IDP, 'domain': domain}]
                self.audit()

    def test_overlapping_additional_hostname_and_destination_are_also_rejected(self):
        for extra in ({'self_hosted_domains': ['dev-pr-123.agentstration.cloud']},
                      {'destinations': [{'type': 'public', 'uri': 'dev-pr-123.agentstration.cloud/api'}]}):
            with self.subTest(extra=extra), self.assertRaises(ValueError):
                self.cloud.apps = [self.cloud.app, {'id': IDP, 'domain': 'other.example.com', **extra}]
                self.audit()

    def test_an_unrelated_application_can_coexist(self):
        self.cloud.apps.append({'id': IDP, 'domain': 'docs.agentstration.cloud'})
        self.assertEqual('configuration-validated', self.audit()['state'])

    def test_cloudflare_inventory_authorization_failure_cannot_report_success(self):
        original = self.cloud.list
        for denied in (ROOT + '/cfd_tunnel', '/zones/' + ZONE + '/dns_records'):
            def listing(path):
                if path == denied:
                    raise RuntimeError('Cloudflare API read failed (HTTP 403)')
                return original(path)
            with self.subTest(denied=denied), patch.object(self.cloud, 'list', side_effect=listing), self.assertRaises(RuntimeError):
                self.audit()

    def test_untrusted_actions_invocation_never_contacts_cloudflare(self):
        for branch, event in (('refs/heads/feature', 'workflow_dispatch'), ('refs/heads/main', 'pull_request')):
            result = subprocess.run([sys.executable, str(Path(cloudflare.__file__)), '--output', '/tmp/unused-access-audit.json'],
                                    env={**os.environ, **self.env, 'GITHUB_ACTIONS': 'true', 'GITHUB_REF': branch,
                                         'GITHUB_EVENT_NAME': event}, text=True, capture_output=True, timeout=10)
            self.assertNotEqual(0, result.returncode)
            self.assertIn('trusted main', result.stderr)
            self.assertNotIn('private-api-fixture', result.stderr)
            self.assertNotIn('private-service-fixture', result.stderr)

    def test_cli_writes_only_safe_evidence_and_explicit_limitations(self):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / 'audit.json'
            summary = Path(directory) / 'summary.md'
            env = {**self.env, 'GITHUB_ACTIONS': 'true', 'GITHUB_REF': 'refs/heads/main',
                   'GITHUB_EVENT_NAME': 'workflow_dispatch', 'GITHUB_STEP_SUMMARY': str(summary)}
            with patch.dict(os.environ, env, clear=True), patch.object(cloudflare, 'ReadOnlyAPI', return_value=self.cloud), \
                    patch.object(sys, 'argv', ['cloudflare.py', '--output', str(output)]):
                cloudflare.main()
            self.assertEqual('configuration-validated', json.loads(output.read_text())['state'])
            self.assertEqual(0o600, output.stat().st_mode & 0o777)
            self.assertIn('unexercised', summary.read_text())
            for secret in ('private-api-fixture', 'private-service-fixture', 'private-entra-fixture'):
                self.assertNotIn(secret, output.read_text() + summary.read_text())


class ApiTests(unittest.TestCase):
    def response(self, payload):
        return io.BytesIO(payload if isinstance(payload, bytes) else json.dumps(payload).encode())

    def test_http_requests_are_get_only_to_fixed_api_with_no_service_credentials(self):
        api = cloudflare.ReadOnlyAPI('private-api-fixture')
        api.opener.open = Mock(return_value=self.response({'success': True, 'result': []}))
        api.get('/accounts/' + ACCOUNT + '/cfd_tunnel')
        args, kwargs = api.opener.open.call_args
        request = args[0]
        self.assertEqual('GET', request.method)
        self.assertEqual('https://api.cloudflare.com/client/v4/accounts/' + ACCOUNT + '/cfd_tunnel', request.full_url)
        self.assertEqual({'Authorization': 'Bearer private-api-fixture', 'Accept': 'application/json'}, request.headers)
        self.assertIsNone(request.data)
        self.assertEqual(30, kwargs['timeout'])

    def test_redirects_cannot_forward_authorization_to_another_origin(self):
        with self.assertRaisesRegex(ValueError, 'redirects are refused'):
            cloudflare.NoRedirect().redirect_request(None, None, 302, 'redirect', {}, 'https://other.invalid/')

    def test_errors_do_not_disclose_response_bodies_or_credential_values(self):
        for failure in (urllib.error.HTTPError('https://private-api-fixture', 403, 'private-service-fixture', {},
                                               self.response(b'private-entra-fixture')),
                        urllib.error.URLError('private-api-fixture')):
            api = cloudflare.ReadOnlyAPI('private-api-fixture')
            api.opener.open = Mock(side_effect=failure)
            with self.assertRaises(RuntimeError) as error:
                api.get('/accounts/' + ACCOUNT)
            self.assertNotIn('private-', str(error.exception))

    def test_malformed_failed_and_oversized_api_responses_are_rejected_without_content(self):
        for payload in (b'private-api-fixture', b'x' * (cloudflare.MAX_RESPONSE + 1),
                        {'success': False, 'errors': [{'message': 'private-service-fixture'}]}, [], {'success': True}):
            with self.subTest(payload_type=type(payload).__name__):
                api = cloudflare.ReadOnlyAPI('fixture')
                api.opener.open = Mock(return_value=self.response(payload))
                with self.assertRaises(ValueError) as error:
                    api.get('/accounts/' + ACCOUNT)
                self.assertNotIn('private-', str(error.exception))

    def test_inventory_reads_all_pages_before_reporting_no_overlap(self):
        api = cloudflare.ReadOnlyAPI('fixture')
        api.get = Mock(side_effect=[{'result': [{'id': i} for i in range(50)], 'result_info': {'total_pages': 2}},
                                    {'result': [{'id': 50}], 'result_info': {'total_pages': 2}}])
        self.assertEqual(51, len(api.list('/access/apps')))
        self.assertEqual('/access/apps?page=2&per_page=50', api.get.call_args.args[0])

    def test_partial_or_non_converging_inventory_is_never_silently_accepted(self):
        for response in ({'result': [], 'result_info': {'total_pages': 9999}},
                         {'result': [{'id': i} for i in range(50)]}):
            api = cloudflare.ReadOnlyAPI('fixture')
            api.get = Mock(return_value=response)
            with self.assertRaises(ValueError):
                api.list('/access/apps')
            self.assertLessEqual(api.get.call_count, cloudflare.MAX_PAGES)


if __name__ == '__main__':
    unittest.main()
