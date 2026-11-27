"""Capacity reservations and the independent provider lease bound simultaneous previews."""
import copy
from datetime import datetime, timezone
import os
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

import azure
import capacity
from test_azure import Cloud, azure_request


def entry(name, destroyed=False, size='Standard_B2ms'):
    request = azure_request()
    request['environmentId'] = name
    request['target']['vmSize'] = size
    cloud = Cloud()
    azure.execute('deploy', request, cloud.factory, route_factory=lambda *args: None)
    cloud.record['destroyed'] = destroyed
    return {'prefix': azure.prefix(request), 'record': cloud.record}


class CapacityTests(unittest.TestCase):
    def test_inventory_is_bounded_and_blobs_must_match_their_recorded_scope(self):
        one = entry('one')
        with tempfile.TemporaryDirectory() as directory:
            client = object.__new__(azure.Azure)
            client.request, client.directory = one['record']['request'], Path(directory)
            blobs = [{'name': one['prefix'] + '/lifecycle.json'}, {'name': one['prefix'] + '/transfer/fixture.tar'}]
            downloads = []
            def storage(*args):
                if args[:2] == ('blob', 'list'):
                    self.assertIn('1001', args)
                    return blobs
                downloads.append(args)
                Path(args[args.index('--file') + 1]).write_text(json.dumps(one['record']))
            client.storage = storage
            self.assertEqual([one], client.list_records())
            self.assertEqual(1, len(downloads))
            self.assertEqual(0o600, (Path(directory) / 'inventory.json').stat().st_mode & 0o777)
            blobs[0]['name'] = 'pv-' + 'f' * 20 + '/lifecycle.json'
            with self.assertRaisesRegex(ValueError, 'Ownership blob differs'):
                client.list_records()
            blobs[:] = [{'name': 'fixture'}] * 1001
            downloads.clear()
            with self.assertRaisesRegex(ValueError, 'exceeds'):
                client.list_records()
            self.assertEqual([], downloads)

    def test_updates_replace_their_reservation_and_destroyed_lifecycles_release_it(self):
        one, two = entry('one'), entry('two')
        capacity.enforce(one['record']['request'], [one, two], [])
        three = entry('three')['record']['request']
        with self.assertRaisesRegex(ValueError, 'environment capacity'):
            capacity.enforce(three, [one, two], [])
        two['record']['destroyed'] = True
        capacity.enforce(three, [one, two], [])

    def test_stopped_or_incomplete_and_residual_environments_remain_reserved(self):
        one, two = entry('one'), entry('two', destroyed=True)
        two['record']['deletion'] = {'requestedAt': '2026-11-26T12:00:00Z', 'completedAt': None, 'reason': 'manual'}
        with self.assertRaises(ValueError):
            capacity.enforce(entry('three')['record']['request'], [one, two], [])
        del two['record']['deletion']
        with self.assertRaises(ValueError):
            capacity.enforce(entry('three')['record']['request'], [one, two], [{'name': two['prefix'] + '-data'}])

    def test_vcpu_limit_and_invalid_or_unknown_capacity_fail_closed(self):
        one = entry('one', size='Standard_B4ms')
        with self.assertRaisesRegex(ValueError, 'vCPU capacity'):
            capacity.enforce(entry('two')['record']['request'], [one], [])
        for value in ('0', '-1', 'invalid', '1001'):
            with self.subTest(value=value), patch.dict(os.environ, {'AZURE_PREVIEW_MAX_ENVIRONMENTS': value}), self.assertRaises(ValueError):
                capacity.enforce(one['record']['request'], [one], [])
        with self.assertRaisesRegex(ValueError, 'Unrecorded'):
            capacity.enforce(one['record']['request'], [one], [{'name': 'pv-' + 'a' * 20 + '-vm'}])

    def test_global_lease_precedes_inventory_and_is_released_on_quota_failure(self):
        one = entry('one')
        with tempfile.TemporaryDirectory() as directory:
            client = object.__new__(azure.Azure)
            client.request, client.directory = one['record']['request'], Path(directory)
            calls = []
            def storage(*args, **kwargs):
                calls.append(args)
                if args[:2] == ('blob', 'exists'):
                    return {'exists': True}
                if args[:3] == ('blob', 'lease', 'acquire'):
                    return 'global-lease'
            client.storage = storage
            def records():
                self.assertEqual(('blob', 'lease', 'acquire'), calls[-1][:3])
                return [one]
            client.list_records = records
            client.az = lambda *args: []
            with patch.object(capacity, 'enforce', side_effect=ValueError('quota')):
                with self.assertRaisesRegex(ValueError, 'quota'):
                    with client.capacity():
                        self.fail('Capacity overflow must never enter provisioning')
            self.assertEqual(('blob', 'lease', 'release'), calls[-1][:3])

    def test_competing_global_lease_cannot_enter_inventory_or_release_the_other_writer(self):
        with tempfile.TemporaryDirectory() as directory:
            client = object.__new__(azure.Azure)
            client.request, client.directory = azure_request(), Path(directory)
            calls = []
            def storage(*args):
                calls.append(args)
                if args[:2] == ('blob', 'exists'):
                    return {'exists': True}
                raise RuntimeError('lease already held')
            client.storage = storage
            client.list_records = lambda: self.fail('A competing writer cannot inspect/reserve capacity')
            with self.assertRaises(RuntimeError):
                with client.capacity():
                    self.fail('A competing writer cannot provision')
            self.assertFalse(any(args[:3] == ('blob', 'lease', 'release') for args in calls))
