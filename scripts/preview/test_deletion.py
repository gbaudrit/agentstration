"""Completed deletion is persisted only after verified cleanup, never inferred from expiry."""
import copy
from datetime import datetime, timedelta, timezone
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

import azure
import preview_feedback
import workflow_result
from test_azure import Cloud, azure_request

NOW = datetime(2026, 11, 26, 12, tzinfo=timezone.utc)


class DeletionTests(unittest.TestCase):
    def setUp(self):
        self.cloud, self.request = Cloud(), azure_request()
        self.execute('deploy')

    def execute(self, action, **kwargs):
        return azure.execute(action, self.request if action != 'destroy' else {**self.request, 'dataMode': 'reset'},
                             self.cloud.factory, route_factory=lambda *args: None, now=kwargs.pop('now', NOW), **kwargs)

    def test_status_preserves_actual_deletion_time_reason_and_idempotent_retry(self):
        first = self.execute('destroy', deletion_reason='ttl')
        second = self.execute('destroy', now=NOW + timedelta(hours=1))
        status = self.execute('status')
        self.assertEqual('destroyed', status['state'])
        self.assertEqual(first['deletion'], second['deletion'])
        self.assertEqual(first['deletion'], status['deletion'])
        self.assertEqual(NOW.isoformat(), status['deletion']['completedAt'])
        self.assertEqual('ttl', status['deletion']['reason'])
        text = preview_feedback.completed(status, 'status', 'owner/repo', 'https://github.com/owner/repo/actions/runs/1', self.request)
        self.assertIn('2026-11-26 12:00 UTC', text)
        self.assertIn('expired lifetime', text)
        self.assertNotIn('No active environment', text)
        summary = workflow_result.render(status)
        self.assertIn('Deletion completed: ' + NOW.isoformat(), summary)
        self.assertIn('Deletion reason: ttl', summary)

    def test_failed_cleanup_remains_pending_blocks_deploy_and_can_be_resumed(self):
        with patch.object(self.cloud, 'reset', side_effect=RuntimeError('fixture')):
            with self.assertRaises(RuntimeError):
                self.execute('destroy', deletion_reason='pr-closed')
        self.assertEqual('cleanup-pending', self.execute('status')['state'])
        self.assertIsNone(self.cloud.record['deletion']['completedAt'])
        with self.assertRaisesRegex(ValueError, 'incomplete'):
            self.execute('deploy')
        self.assertEqual('destroyed', self.execute('destroy')['state'])
        self.assertEqual('pr-closed', self.cloud.record['deletion']['reason'])

    def test_legacy_deleted_record_has_no_invented_date(self):
        self.execute('destroy')
        del self.cloud.record['deletion']
        status = self.execute('status')
        self.assertEqual('destroyed', status['state'])
        self.assertIsNone(status['deletion'])
        text = preview_feedback.completed(status, 'status', 'owner/repo', '', self.request)
        self.assertIn('Deleted: **not available**', text)
        self.assertNotIn(preview_feedback.expiry(status['expiresAt']), text)

    def test_fresh_deployment_clears_the_prior_deleted_state_and_uses_new_owner(self):
        self.execute('destroy')
        owner = self.cloud.record['ownerId']
        self.execute('deploy')
        self.assertNotEqual(owner, self.cloud.record['ownerId'])
        self.assertNotIn('deletion', self.cloud.record)
        self.assertEqual('running', self.execute('status')['state'])

    def test_reconciliation_guard_prevents_deleting_a_new_or_extended_lifecycle(self):
        owner = self.cloud.record['ownerId']
        self.cloud.record['ownerId'] = '00000000-0000-4000-8000-000000000123'
        for item in self.cloud.items:
            item['tags'][azure.OWNER] = self.cloud.record['ownerId']
        self.cloud.effects = []
        with self.assertRaisesRegex(ValueError, 'changed after reconciliation'):
            self.execute('destroy', guard=lambda current: current['ownerId'] == owner)
        self.assertEqual([], self.cloud.effects)

    def test_missing_record_with_empty_namespace_returns_none_without_new_ownership(self):
        self.cloud.record, self.cloud.items, self.cloud.effects = None, [], []
        target = self.request['target']
        result = azure.recorded_request(self.request['environmentId'], target['subscriptionId'], target['resourceGroup'],
                                        'owner/repo', client_factory=self.cloud.factory)
        self.assertIsNone(result)
        self.assertIsNone(self.cloud.record)
        self.assertEqual([], self.cloud.effects)

    def test_reappearing_owned_resources_prevent_false_deleted_status(self):
        items = copy.deepcopy(self.cloud.items)
        self.execute('destroy')
        self.cloud.items = items
        self.assertEqual('cleanup-pending', self.execute('status')['state'])

    def test_foreign_os_disk_is_never_adopted_even_when_its_name_matches(self):
        item = next(item for item in self.cloud.items if item['name'].endswith('-os'))
        item['tags'][azure.OWNER] = 'foreign'
        with self.assertRaisesRegex(ValueError, 'OS disk ownership'):
            self.execute('destroy')

    def test_tagged_orphan_os_disk_is_owned_but_legacy_detached_disk_requires_inspection(self):
        disk = next(copy.deepcopy(item) for item in self.cloud.items if item['name'].endswith('-os'))
        client = object.__new__(azure.Azure)
        client.request = self.request
        client.az = lambda *args: [disk] if args[:2] == ('resource', 'list') else {'managedBy': None}
        self.assertEqual([disk], client.resources(self.cloud.record))
        disk['tags'] = {}
        with self.assertRaisesRegex(ValueError, 'unverified OS disk'):
            client.resources(self.cloud.record)

    def test_os_disk_tags_are_written_only_after_owned_vm_and_attachment_proof(self):
        ids = azure.resource_ids(self.request)
        instance = {'location': self.request['target']['region'], 'tags': azure.tags(self.request, self.cloud.record),
                    'hardwareProfile': {'vmSize': 'Standard_B2ms'},
                    'osProfile': {'linuxConfiguration': {'ssh': {'publicKeys': [{'keyData': 'public-disposable-ssh-fixture'}]}}},
                    'storageProfile': {'imageReference': {'version': self.request['target']['imageVersion']},
                                       'osDisk': {'managedDisk': {'id': ids['osDisk']}}}}
        with tempfile.TemporaryDirectory() as directory:
            client = object.__new__(azure.Azure)
            client.request, client.directory = self.request, Path(directory)
            client.resources = lambda record: [{'id': ids['virtualMachine']}]
            calls = []
            attached = ids['virtualMachine']
            def az(*args, **kwargs):
                calls.append(args)
                if args[:2] == ('vm', 'show'):
                    return instance
                if args[:2] == ('disk', 'show'):
                    return {'managedBy': attached, 'tags': {'retained': 'fixture'}}
            client.az = az
            client.provision(self.cloud.record)
            update = next(args for args in calls if args[:2] == ('disk', 'update'))
            self.assertEqual(self.cloud.record['ownerId'], json.loads(update[-1].removeprefix('tags='))[azure.OWNER])
            self.assertEqual('fixture', json.loads(update[-1].removeprefix('tags='))['retained'])
            attached = '/foreign/vm'
            calls.clear()
            with self.assertRaisesRegex(ValueError, 'not attached'):
                client.provision(self.cloud.record)
            self.assertFalse(any(args[:2] == ('disk', 'update') for args in calls))
