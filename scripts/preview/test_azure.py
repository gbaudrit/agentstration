"""Offline Azure boundaries: ownership, retries, credential isolation and guest delegation."""
import copy
from contextlib import contextmanager
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

import azure
import azure_host
import contract
import lifecycle
from test_contract import request


def azure_request(**kwargs):
    return request(provider='azure-vm', target={'subscriptionId': '00000000-0000-4000-8000-000000000001',
                   'resourceGroup': 'previews', 'region': 'westeurope', 'vmSize': 'Standard_B2ms', 'imageVersion': '24.04.202511120'}, **kwargs)


class Cloud:
    def __init__(self):
        self.record = None
        self.items = []
        self.effects = []
        self.running = False
        self.fail_transfer = False
        self.leased = False

    def factory(self, value, directory):
        self.request = value
        return self

    def read(self):
        return copy.deepcopy(self.record)

    def resources(self, record):
        return azure.inspect_owned(self.request, record, copy.deepcopy(self.items))

    def write(self, record, initial=False):
        if not initial and not self.leased:
            raise AssertionError('Writes must hold the provider lease')
        self.record = copy.deepcopy(record)

    @contextmanager
    def lock(self):
        if self.leased:
            raise ValueError('Another operation holds the lease')
        self.leased = True
        try:
            yield
        finally:
            self.leased = False

    def provision(self, record):
        self.effects.append('provision')
        self.items = [{'id': identifier, 'name': identifier.rsplit('/', 1)[1], 'tags': azure.tags(self.request, record)}
                      for identifier in azure.resource_ids(self.request).values()]
        self.running = True

    def metrics(self):
        return {}

    def transfer(self, expect_existing=False):
        self.effects.append(('transfer', expect_existing))
        if self.fail_transfer:
            raise RuntimeError('Interrupted transfer')
        return {'state': 'running', 'observedImages': {n: v['reference'] for n, v in contract.images(self.request).items()},
                'guestOperationSeconds': 12, 'memoryAvailableKiB': 12345}

    def power(self):
        return 'PowerState/running' if self.running else 'PowerState/deallocated'

    def run_guest(self, action):
        self.effects.append(action)
        return {'state': 'running' if action == 'status' else 'destroyed', 'observedImages': {}}

    def az(self, *args, **kwargs):
        assert args[:2] == ('vm', 'deallocate')
        self.effects.append('deallocate')
        self.running = False

    def reset(self, record):
        self.effects.append('reset')
        self.items = [r for r in self.items if r['id'] not in azure.resource_ids(self.request).values()]
        self.running = False


class AzureTests(unittest.TestCase):
    def setUp(self):
        self.cloud = Cloud()
        self.value = azure_request()

    def execute(self, action, value=None, now=None):
        return azure.execute(action, value or self.value, self.cloud.factory, now=now)

    def test_validation_never_initializes_azure_or_private_state(self):
        with patch.object(azure, 'Azure', side_effect=AssertionError('No cloud access')):
            result = lifecycle.execute('validate', self.value, None)
        self.assertEqual('validated', result['state'])
        self.assertEqual({}, result['providerResourceIds'])

    def test_azure_never_silently_ignores_docker_host_state_or_bootstrap_flags(self):
        for state_root, bootstrap in ((Path('/tmp/private-host-state'), None), (None, {'InitialBootstrapEnabled': True})):
            with self.subTest(state_root=state_root), self.assertRaises(ValueError):
                lifecycle.execute('deploy', self.value, state_root, bootstrap)

    def test_absent_status_and_destroy_have_no_mutating_effects(self):
        self.assertEqual('absent', self.execute('status')['state'])
        self.assertEqual('destroyed', self.execute('destroy')['state'])
        self.assertIsNone(self.cloud.record)
        self.assertEqual([], self.cloud.effects)

    def test_deploy_preserve_redeploy_keeps_ownership_expiry_and_private_ingress(self):
        first = self.execute('deploy', now=datetime(2026, 11, 24, tzinfo=timezone.utc))
        record = copy.deepcopy(self.cloud.record)
        self.assertEqual('running', first['state'])
        self.assertIsNone(first['url'])
        stopped = self.execute('destroy')
        self.assertEqual('destroyed', stopped['state'])
        self.assertEqual(6, len(stopped['providerResourceIds']['resources']))
        self.assertEqual('stopped', self.execute('status')['state'])
        again = self.execute('deploy', now=datetime(2026, 11, 25, tzinfo=timezone.utc))
        self.assertEqual(record['ownerId'], self.cloud.record['ownerId'])
        self.assertEqual(first['expiresAt'], again['expiresAt'])
        self.assertEqual(12345, again['metrics']['memoryAvailableKiB'])
        self.assertEqual(12, again['metrics']['guestOperationSeconds'])
        self.assertGreaterEqual(again['metrics']['provisionAndDeploySeconds'], 0)
        self.assertFalse(self.cloud.leased)
        self.assertIn(('transfer', True), self.cloud.effects)

    def test_reset_removes_only_owned_resources_and_can_change_database_afterwards(self):
        self.execute('deploy')
        other = {'id': '/other/resource', 'name': 'some-other-preview', 'tags': {}}
        self.cloud.items.append(other)
        reset = copy.deepcopy(self.value)
        reset['dataMode'] = 'reset'
        for _ in range(2):
            self.assertEqual([], self.execute('destroy', reset)['providerResourceIds']['resources'])
        self.assertEqual([other], self.cloud.items)
        self.assertTrue(self.cloud.record['destroyed'])
        self.assertEqual('running', self.execute('deploy', azure_request(database='postgresql'))['state'])

    def test_reset_allows_a_corrected_os_image_and_keeps_scope_isolation(self):
        self.execute('deploy')
        reset = copy.deepcopy(self.value)
        reset['dataMode'] = 'reset'
        self.execute('destroy', reset)
        changed = copy.deepcopy(self.value)
        changed['target']['imageVersion'] = '24.04.202611120'
        self.assertEqual('running', self.execute('deploy', changed)['state'])
        self.assertTrue(self.cloud.record['initialized'])

    def test_foreign_or_unmanaged_resources_are_never_adopted_or_deleted(self):
        for action in ('deploy', 'status', 'destroy'):
            for owner in (None, 'another-owner'):
                self.cloud.items = [{'id': azure.resource_ids(self.value)['virtualMachine'],
                                     'name': azure.prefix(self.value) + '-vm', 'tags': {azure.OWNER: owner}}]
                with self.subTest(action=action, owner=owner), self.assertRaises(ValueError):
                    self.execute(action)
        self.assertIsNone(self.cloud.record)
        self.assertEqual([], self.cloud.effects)

    def test_platform_agent_children_require_an_owned_parent_vm(self):
        self.execute('deploy')
        child = {'id': azure.resource_ids(self.value)['virtualMachine'] + '/extensions/RunCommandLinux',
                 'name': azure.prefix(self.value) + '-vm/RunCommandLinux', 'tags': {}}
        self.cloud.items.append(child)
        self.assertEqual('running', self.execute('status')['state'])
        self.cloud.items = [child]
        with self.assertRaises(ValueError):
            self.execute('destroy')

    def test_partial_transfer_is_recoverable_without_new_owner_or_expiry(self):
        self.cloud.fail_transfer = True
        with self.assertRaises(RuntimeError):
            self.execute('deploy')
        owner = self.cloud.record['ownerId']
        expiry = self.cloud.record['expiresAt']
        self.assertFalse(self.cloud.leased)
        self.cloud.fail_transfer = False
        self.assertEqual('running', self.execute('deploy')['state'])
        self.assertEqual(owner, self.cloud.record['ownerId'])
        self.assertEqual(expiry, self.cloud.record['expiresAt'])

    def test_stale_digests_database_changes_and_target_drift_are_rejected(self):
        self.execute('deploy')
        previous_effects = len(self.cloud.effects)
        changed = copy.deepcopy(self.value)
        changed['deliveries']['worker']['digest'] = 'sha256:' + 'e' * 64
        with self.assertRaises(ValueError):
            self.execute('destroy', changed)
        with self.assertRaises(ValueError):
            self.execute('deploy', azure_request(database='postgresql'))
        for key, value in (('region', 'eastus'), ('imageVersion', '24.04.202601010'), ('resourceGroup', 'other')):
            changed = copy.deepcopy(self.value)
            changed['target'][key] = value
            with self.subTest(key=key), self.assertRaises(ValueError):
                self.execute('deploy', changed)
        self.assertEqual(previous_effects, len(self.cloud.effects))

    def test_azure_contract_rejects_mutable_os_and_unsupported_sizes(self):
        for key, invalid in (('imageVersion', 'latest'), ('imageVersion', '24.04'), ('resourceGroup', '../group'), ('vmSize', 'Standard_D64s_v5')):
            value = copy.deepcopy(self.value)
            value['target'][key] = invalid
            with self.subTest(key=key), self.assertRaises(ValueError):
                contract.validate(value)

    def test_lease_contention_stops_before_infrastructure_mutation(self):
        self.execute('deploy')
        count = len(self.cloud.effects)
        self.cloud.leased = True
        with self.assertRaises(ValueError):
            self.execute('destroy')
        self.assertEqual(count, len(self.cloud.effects))

    def test_observed_vm_must_have_no_managed_identity_and_match_selected_infrastructure(self):
        instance = {'location': 'westeurope', 'hardwareProfile': {'vmSize': 'Standard_B2ms'},
                    'storageProfile': {'imageReference': {'version': self.value['target']['imageVersion']}}}
        azure.verify_vm(self.value, instance)
        for path, value in ((('identity',), {'type': 'SystemAssigned'}), (('identity',), {'type': 'UserAssigned'}),
                            (('hardwareProfile', 'vmSize'), 'Standard_B4ms'), (('location',), 'eastus'),
                            (('storageProfile', 'imageReference', 'version'), 'latest')):
            changed = copy.deepcopy(instance)
            selected = changed
            for key in path[:-1]:
                selected = selected[key]
            selected[path[-1]] = value
            with self.subTest(path=path, value=value), self.assertRaises(ValueError):
                azure.verify_vm(self.value, changed)

    def test_guest_request_uses_same_digests_without_cloud_credentials(self):
        with patch.object(lifecycle, 'docker', return_value='actual-daemon'):
            guest = azure_host.local_request(self.value)
        self.assertEqual('docker', guest['provider'])
        self.assertEqual('actual-daemon', guest['target']['daemonId'])
        for name in ('server', 'worker'):
            self.assertEqual(contract.images(self.value)[name]['reference'].split('@')[1], contract.images(guest)[name]['reference'].split('@')[1])
            self.assertTrue(contract.images(guest)[name]['reference'].startswith('localhost:5001/'))
        self.assertNotIn('subscriptionId', json.dumps(guest))

    def test_run_command_uses_only_protected_url_and_deletes_it_afterwards(self):
        self.execute('deploy')
        with tempfile.TemporaryDirectory() as temporary:
            client = object.__new__(azure.Azure)
            client.request, client.directory = self.value, Path(temporary)
            calls = []
            secret = 'https://private.example.test/image?sig=disposable-test-value'
            def invoke(*args, **kwargs):
                calls.append(args)
                if args[0:3] == ('rest', '--method', 'put'):
                    body = json.loads((client.directory / 'run-command.json').read_text())
                    self.assertEqual(0o600, (client.directory / 'run-command.json').stat().st_mode & 0o777)
                    self.assertNotIn(secret, body['properties']['source']['script'])
                    self.assertEqual([], body['properties']['parameters'])
                    self.assertEqual(secret, body['properties']['protectedParameters'][0]['value'])
                if args[0:3] == ('rest', '--method', 'get'):
                    return {'properties': {'instanceView': {'executionState': 'Succeeded', 'exitCode': 0,
                            'output': 'AGENTSTRATION_RESULT={"state":"running","observedImages":{}}'}}}
            client.az = invoke
            client.read = self.cloud.read
            result = client.run_guest('deploy', url=secret, checksum='a' * 64)
            self.assertEqual('running', result['state'])
            self.assertFalse((client.directory / 'run-command.json').exists())
            self.assertEqual(('rest', '--method', 'delete'), calls[-1][:3])

    def test_provider_command_failure_never_echoes_credentials_or_raw_logs(self):
        secret = 'public-disposable-secret-fixture'
        with patch.object(subprocess, 'run', return_value=subprocess.CompletedProcess([], 1, secret, secret)):
            with self.assertRaises(RuntimeError) as error:
                azure.command(['az', 'rest', '--body', secret])
        self.assertNotIn(secret, str(error.exception))

    def test_guest_wrapper_is_valid_shell_and_never_embeds_a_download_credential(self):
        for action in ('deploy', 'status', 'destroy'):
            script = azure.guest_script(action, self.value, 'a' * 64 if action == 'deploy' else None)
            subprocess.run(['bash', '-n'], input=script, text=True, check=True)
            self.assertNotIn('GHCR', script)
            self.assertNotIn('sig=', script)
        with self.assertRaises(ValueError):
            azure.guest_script('deploy', self.value, 'injected;command')


if __name__ == '__main__':
    unittest.main()
