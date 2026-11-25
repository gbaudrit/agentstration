"""Offline route ownership, interruption recovery and fail-closed publication boundaries."""
import copy
import io
import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import urllib.error
import uuid

import access
import azure
import azure_host
import access_host
import cloudflare
from test_azure import azure_request, Cloud as AzureCloud
from test_cloudflare import Cloud as AuditCloud, environment, ROOT

TUNNEL = '00000000-0000-4000-8000-000000000010'
DNS = 'b' * 32


class Cloud(AuditCloud):
    def __init__(self):
        super().__init__()
        self.tunnels, self.dns, self.changes = [], [], []
        self.config = None
        self.interrupt = None

    def list(self, path):
        if path == ROOT + '/cfd_tunnel':
            return copy.deepcopy(self.tunnels)
        if path == '/zones/' + 'b' * 32 + '/dns_records':
            return copy.deepcopy(self.dns)
        return super().list(path)

    def get(self, path):
        if path.endswith('/' + TUNNEL + '/token'):
            return {'result': 'private-connector-fixture'}
        if path.endswith('/' + TUNNEL + '/configurations'):
            return {'result': {'config': copy.deepcopy(self.config)}}
        return super().get(path)

    def change(self, method, path, body=None):
        self.changes.append((method, path, copy.deepcopy(body)))
        if method == 'POST' and path.endswith('/cfd_tunnel'):
            item = {'id': TUNNEL, **body}
            self.tunnels.append(item)
        elif method == 'PUT':
            self.config = copy.deepcopy(body['config'])
            return {}
        elif method == 'POST' and path.endswith('/dns_records'):
            item = {'id': DNS, **body}
            self.dns.append(item)
        elif method == 'DELETE':
            if path.endswith('/dns_records/' + DNS):
                self.dns = []
            elif path.endswith('/cfd_tunnel/' + TUNNEL):
                self.tunnels = []
            return {}
        else:
            raise AssertionError('Unexpected provider mutation')
        if self.interrupt == path.rsplit('/', 1)[1]:
            self.interrupt = None
            raise RuntimeError('Interrupted after provider creation')
        return copy.deepcopy(item)


class RouteTests(unittest.TestCase):
    def setUp(self):
        self.request = {**azure_request(), 'environmentId': 'pr-17'}
        self.record = {'ownerId': '00000000-0000-4000-8000-000000000099'}
        self.cloud = Cloud()
        self.snapshots = []
        self.routes = self.construct()

    def construct(self):
        return access.Routes(self.request, self.record, lambda value: self.snapshots.append(copy.deepcopy(value)),
                             environment(), self.cloud)

    def publish(self):
        with patch.object(access, 'probe') as probe:
            url = self.routes.publish()
        probe.assert_called_once()
        return url

    def test_creation_intent_precedes_any_mutation_and_retry_keeps_single_owned_route(self):
        self.assertEqual([], self.cloud.changes)
        self.assertEqual(self.record['access'], self.snapshots[0]['access'])
        token = self.routes.prepare()
        self.assertEqual('private-connector-fixture', token['token'])
        self.assertFalse(self.record['access']['verified'])
        self.assertEqual('https://dev-pr-17.agentstration.cloud', self.publish())
        self.assertTrue(self.record['access']['verified'])
        self.routes.prepare()
        self.publish()
        self.assertEqual(1, len(self.cloud.tunnels))
        self.assertEqual(1, len(self.cloud.dns))
        self.assertEqual(1, len([change for change in self.cloud.changes if change[0] == 'POST' and change[1].endswith('/cfd_tunnel')]))
        self.assertNotIn('private-', json.dumps(self.record))

    def test_interrupted_tunnel_post_is_reconciled_using_saved_owner_intent(self):
        self.cloud.interrupt = 'cfd_tunnel'
        with self.assertRaises(RuntimeError):
            self.routes.prepare()
        self.assertIsNone(self.record['access']['tunnelId'])
        self.construct().prepare()
        self.assertEqual(1, len(self.cloud.tunnels))
        self.assertEqual(TUNNEL, self.record['access']['tunnelId'])

    def test_interrupted_dns_post_is_reconciled_before_retry_or_teardown(self):
        self.routes.prepare()
        self.cloud.interrupt = 'dns_records'
        with self.assertRaises(RuntimeError):
            self.publish()
        self.assertIsNone(self.record['access']['dnsId'])
        self.routes.destroy()
        self.routes.destroy()
        self.assertEqual([], self.cloud.dns)
        self.assertEqual([], self.cloud.tunnels)

    def test_foreign_dns_or_tunnel_never_authorizes_adoption_or_deletion(self):
        self.cloud.tunnels = [{'id': TUNNEL, 'name': self.record['access']['tunnelName'], 'config_src': 'local'}]
        with self.assertRaisesRegex(ValueError, 'Foreign tunnel'):
            self.routes.prepare()
        self.assertEqual([], self.cloud.changes)
        self.cloud.tunnels = []
        self.routes.prepare()
        self.cloud.dns = [{'id': DNS, 'type': 'CNAME', 'name': self.record['access']['hostname'],
                           'content': TUNNEL + '.cfargotunnel.com', 'proxied': True, 'comment': 'foreign'}]
        count = len(self.cloud.changes)
        for action in (self.routes.prepare, self.routes.withdraw, self.routes.destroy):
            with self.assertRaisesRegex(ValueError, 'Foreign'):
                action()
        self.assertEqual(count, len(self.cloud.changes))

    def test_changed_scope_or_owner_refuses_mutation(self):
        for key in ('ACCOUNT_ID', 'ZONE_ID', 'ACCESS_APP_ID', 'PREVIEW_DOMAIN', 'TEAM_DOMAIN'):
            changed = environment()
            changed['CLOUDFLARE_' + key] = {'ACCOUNT_ID': 'c' * 32, 'ZONE_ID': 'c' * 32,
                'ACCESS_APP_ID': TUNNEL, 'PREVIEW_DOMAIN': 'other.example', 'TEAM_DOMAIN': 'other.cloudflareaccess.com'}[key]
            with self.subTest(key=key), self.assertRaises(ValueError):
                access.Routes(self.request, self.record, lambda _: None, changed, self.cloud)
        self.record['ownerId'] = str(uuid.uuid4())
        with self.assertRaises(ValueError):
            self.construct()
        self.assertEqual([], self.cloud.changes)

    def test_broken_access_policy_prevents_creation_and_changed_jwt_guard_prevents_verification(self):
        self.cloud.policies[0]['decision'] = 'bypass'
        with self.assertRaises(ValueError):
            self.routes.prepare()
        self.assertEqual([], self.cloud.changes)
        self.cloud = Cloud()
        self.routes = self.construct()
        self.routes.prepare()
        self.publish()
        self.cloud.config['ingress'][0]['originRequest']['access']['required'] = False
        with patch.object(access, 'probe') as probe, self.assertRaises(ValueError):
            self.routes.verify()
        probe.assert_not_called()

    def test_failed_authentication_never_marks_route_verified(self):
        self.routes.prepare()
        with patch.object(access, 'probe', side_effect=ValueError('Authentication failed')), self.assertRaises(ValueError):
            self.routes.publish()
        self.assertFalse(self.record['access']['verified'])
        self.routes.withdraw()
        self.assertEqual([], self.cloud.dns)

    def test_teardown_does_not_require_a_healthy_shared_access_policy(self):
        self.routes.prepare()
        self.publish()
        self.cloud.policies[0]['decision'] = 'bypass'
        self.routes.destroy()
        self.assertEqual([], self.cloud.dns)
        self.assertEqual([], self.cloud.tunnels)

    def test_api_mutations_cannot_write_access_policies_or_foreign_endpoints(self):
        api = access.API('private-fixture')
        with patch.object(api, '_request') as request:
            for method, path in [('POST', ROOT + '/access/apps'), ('PATCH', ROOT + '/cfd_tunnel'),
                                 ('DELETE', '/zones/' + 'b' * 32 + '/settings')]:
                with self.assertRaises(ValueError):
                    api.change(method, path)
        request.assert_not_called()


class ProbeTests(unittest.TestCase):
    def setUp(self):
        self.values = cloudflare.configuration(environment())
        self.url = 'https://dev-pr-17.agentstration.cloud'

    def test_expected_denials_then_service_auth_success_publish_no_response_content(self):
        with patch.object(access, 'response', side_effect=[(302, 'https://agentstration.cloudflareaccess.com/cdn-cgi/access/login/x'),
                                                          (403, ''), (200, '')]) as request:
            access.probe(self.url, self.values, attempts=1)
        self.assertEqual({}, request.call_args_list[0].args[1])
        self.assertEqual('private-service-fixture', request.call_args_list[-1].args[1]['CF-Access-Client-Secret'])

    def test_origin_response_wrong_redirect_or_invalid_ci_token_cannot_pass(self):
        for responses in ([(200, '')], [(302, 'https://evil.example/')], [(403, ''), (403, ''), (401, '')]):
            with self.subTest(responses=responses), patch.object(access, 'response', side_effect=responses), self.assertRaises(ValueError):
                access.probe(self.url, self.values, attempts=1)

    def test_credentials_never_follow_a_redirect_or_expose_error_bodies(self):
        error = urllib.error.HTTPError(self.url, 302, 'private-body', {'Location': 'https://evil.example/'}, io.BytesIO(b'private-body'))
        opener = access.urllib.request.build_opener(access.NoProbeRedirect())
        with patch.object(access.urllib.request, 'build_opener', return_value=opener), patch.object(opener, 'open', side_effect=error):
            self.assertEqual((302, 'https://evil.example/'), access.response(self.url, {}))
        self.assertIsNone(access.NoProbeRedirect().redirect_request(None, None, 302, None, {}, 'https://evil.example/'))

    def test_probe_target_cannot_leak_credentials_to_another_host(self):
        for url in ('http://dev-pr-17.agentstration.cloud', self.url + '@evil.example', self.url + '/private', 'https://evil.example'):
            with self.subTest(url=url), patch.object(access, 'response') as response, self.assertRaises(ValueError):
                access.probe(url, self.values, attempts=1)
            response.assert_not_called()


class AzureAccessTests(unittest.TestCase):
    def setUp(self):
        self.cloud, self.api = AzureCloud(), Cloud()
        self.request = {**azure_request(), 'environmentId': 'pr-17'}
        self.cloud.closed_ingress = lambda: self.cloud.effects.append('closed-ingress')
        original = self.cloud.transfer
        self.cloud.transfer = lambda expect_existing=False, connector=None: {**original(expect_existing), 'accessReady': True}

    def routes(self, request, record, save, _environment):
        self.assertTrue(self.cloud.leased)
        return access.Routes(request, record, save, environment(), self.api)

    def execute(self, action, request=None):
        with patch.object(access, 'probe'):
            return azure.execute(action, request or self.request, self.cloud.factory, route_factory=self.routes)

    def test_publish_redeploy_preserve_and_reset_share_the_provider_lease(self):
        first = self.execute('deploy')
        self.assertEqual('https://dev-pr-17.agentstration.cloud', first['url'])
        self.assertEqual(TUNNEL, first['providerResourceIds']['cloudflare']['tunnelId'])
        self.assertIn('closed-ingress', self.cloud.effects)
        self.execute('deploy')
        self.assertEqual(1, len(self.api.tunnels))
        self.execute('destroy')
        self.assertEqual([], self.api.tunnels)
        self.assertEqual([], self.api.dns)
        self.assertIsNone(self.execute('status')['url'])
        self.execute('deploy')
        reset = {**self.request, 'dataMode': 'reset'}
        self.execute('destroy', reset)
        self.execute('destroy', reset)
        self.assertTrue(self.cloud.record['destroyed'])
        self.assertEqual([], self.api.tunnels)

    def test_partial_guest_failure_withdraws_dns_and_remains_retryable(self):
        self.execute('deploy')
        self.cloud.fail_transfer = True
        with self.assertRaises(RuntimeError):
            self.execute('deploy')
        self.assertEqual([], self.api.dns)
        self.assertFalse(self.cloud.record['access']['verified'])
        self.cloud.fail_transfer = False
        self.assertIsNotNone(self.execute('deploy')['url'])

    def test_no_url_if_guest_connector_or_ingress_check_fails(self):
        for failure in ('connector', 'ingress'):
            self.setUp()
            if failure == 'connector':
                self.cloud.transfer = lambda **kwargs: {'state': 'running', 'accessReady': False}
            else:
                self.cloud.closed_ingress = lambda: (_ for _ in ()).throw(ValueError('Ingress opened'))
            with self.subTest(failure=failure), self.assertRaises(ValueError):
                self.execute('deploy')
            self.assertEqual([], self.api.dns)
            self.assertFalse(self.cloud.record['access']['verified'])

    def test_connector_credential_uses_protected_parameters_and_is_removed_from_arm(self):
        self.execute('deploy')
        with tempfile.TemporaryDirectory() as temporary:
            client = object.__new__(azure.Azure)
            client.request, client.directory, client.read = self.request, Path(temporary), self.cloud.read
            calls = []
            def invoke(*args, **kwargs):
                calls.append(args)
                if args[:3] == ('rest', '--method', 'put'):
                    body = json.loads((client.directory / 'run-command.json').read_text())
                    public = json.dumps({key: value for key, value in body['properties'].items() if key != 'protectedParameters'})
                    self.assertNotIn('private-connector-fixture', public)
                    self.assertIn('--access-owner ' + self.cloud.record['ownerId'], public)
                    protected = {item['name']: item['value'] for item in body['properties']['protectedParameters']}
                    self.assertEqual('private-connector-fixture', json.loads(protected['ACCESS_CONNECTOR'])['token'])
                    self.assertNotIn('private-service-fixture', json.dumps(body))
                if args[:3] == ('rest', '--method', 'get'):
                    return {'properties': {'instanceView': {'executionState': 'Succeeded', 'exitCode': 0,
                            'output': 'AGENTSTRATION_RESULT={"state":"running"}'}}}
            client.az = invoke
            client.run_guest('deploy', url='https://fixture.example/bundle', checksum='a' * 64,
                             connector={'ownerId': self.cloud.record['ownerId'], 'hostname': 'dev-pr-17.agentstration.cloud', 'token': 'private-connector-fixture'})
            self.assertFalse((client.directory / 'run-command.json').exists())
            self.assertEqual(('rest', '--method', 'delete'), calls[-1][:3])

    def test_observed_ingress_must_match_the_owned_nic_and_deny_all_ports(self):
        ids = azure.resource_ids(self.request)
        vm = {'location': 'westeurope', 'hardwareProfile': {'vmSize': 'Standard_B2ms'},
              'storageProfile': {'imageReference': {'version': self.request['target']['imageVersion']}},
              'networkProfile': {'networkInterfaces': [{'id': ids['networkInterface']}]}}
        nic = {'networkSecurityGroup': {'id': ids['networkSecurityGroup']}}
        group = {'securityRules': [{'direction': 'Inbound', 'priority': 100, 'access': 'Deny', 'protocol': '*',
                 'sourcePortRange': '*', 'destinationPortRange': '*', 'sourceAddressPrefix': '*', 'destinationAddressPrefix': '*'}]}
        client = object.__new__(azure.Azure)
        client.request = self.request
        client.az = lambda *args, **kwargs: vm if args[:2] == ('vm', 'show') else nic if args[1] == 'nic' else group
        client.closed_ingress()
        group['securityRules'].append({'direction': 'Inbound', 'priority': 100, 'access': 'Allow'})
        with self.assertRaises(ValueError):
            client.closed_ingress()
        group['securityRules'].pop()
        nic['networkSecurityGroup']['id'] = '/foreign/nsg'
        with self.assertRaises(ValueError):
            client.closed_ingress()


class GuestAccessTests(unittest.TestCase):
    def test_guest_removes_protected_environment_before_any_compose_subprocess(self):
        config = {'ownerId': '00000000-0000-4000-8000-000000000099',
                  'hostname': 'dev-test.agentstration.cloud', 'token': 'private-connector-fixture'}
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / 'request.json'
            path.write_text(json.dumps(azure_request()))
            def run(*args, **kwargs):
                self.assertNotIn('ACCESS_CONNECTOR', os.environ)
                self.assertNotIn('BUNDLE_URL', os.environ)
                self.assertEqual(config, kwargs['access_config'])
                return {'state': 'running'}
            with patch.dict(os.environ, {'ACCESS_CONNECTOR': json.dumps(config), 'BUNDLE_URL': 'https://fixture.example/bundle'}), \
                    patch('sys.argv', ['azure_host.py', 'deploy', '--request', str(path), '--access-owner', config['ownerId']]), \
                    patch.object(azure_host, 'run', side_effect=run), patch('builtins.print'):
                azure_host.main()

    def test_foreign_named_connector_cannot_be_stopped_or_replaced(self):
        item = {'Id': 'fixture', 'Config': {'Labels': {access_host.LABEL: str(uuid.uuid4())}}}
        with patch.object(access_host.lifecycle, 'docker', side_effect=['fixture', json.dumps([item])]) as docker, self.assertRaises(ValueError):
            access_host.stop(Path('/unused'), str(uuid.uuid4()))
        self.assertFalse(any(call.args[0] == 'rm' for call in docker.call_args_list))

    def test_container_drift_cannot_be_reported_as_connector_readiness(self):
        images = access_host.preview.read_json(access_host.preview.ROOT / 'deploy/preview/access.json')
        current = {role: {'Config': {'Image': images[role + 'Image']}, 'State': {'Running': True},
                         'HostConfig': {'NetworkMode': 'host', 'ReadonlyRootfs': True,
                                        'Tmpfs': {'/data': '', '/config': ''} if role == 'proxy' else {}},
                         'Mounts': [{'Type': 'bind', 'Destination': '/etc/caddy/Caddyfile' if role == 'proxy' else '/run/secrets/tunnel-token', 'RW': False}]}
                   for role in access_host.ROLES}
        class Ready:
            status = 200
            def __enter__(self):
                return self
            def __exit__(self, *_):
                pass
        with patch.object(access_host, 'containers', return_value=current), patch.object(access_host.urllib.request, 'urlopen', return_value=Ready()):
            self.assertTrue(access_host.ready(str(uuid.uuid4())))
            for section, key, value in [('Config', 'Image', 'floating:latest'), ('HostConfig', 'ReadonlyRootfs', False),
                                        ('HostConfig', 'NetworkMode', 'bridge')]:
                original = current['connector'][section][key]
                current['connector'][section][key] = value
                with self.subTest(key=key):
                    self.assertFalse(access_host.ready(str(uuid.uuid4())))
                current['connector'][section][key] = original
            current['connector']['Mounts'][0]['RW'] = True
            self.assertFalse(access_host.ready(str(uuid.uuid4())))
            current['connector']['Mounts'][0]['RW'] = False
            current['connector']['Mounts'][0]['Type'] = 'volume'
            self.assertFalse(access_host.ready(str(uuid.uuid4())))
