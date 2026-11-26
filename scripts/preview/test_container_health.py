"""Container health is bounded public metadata, including on older deployed guest tooling."""
import json
import subprocess
import unittest
from unittest.mock import patch

import azure
import container_health as health
import lifecycle
import preview_feedback as feedback
from test_azure import azure_request


class ContainerHealthTests(unittest.TestCase):
    def test_states_health_and_restart_counts_are_reported_per_selected_component(self):
        services = ['server', 'worker', 'extension-openapi', 'postgres']
        value = health.summarize(services, [('server', 'running', 'healthy', 0),
            ('worker', 'running', 'starting', 2), ('extension-openapi', 'restarting', 'unhealthy', 61),
            ('foreign', 'running', 'healthy', 0)])
        self.assertEqual(set(services), set(value))
        self.assertEqual({'state': 'restarting', 'health': 'unhealthy', 'restarts': 61}, value['extension-openapi'])
        self.assertEqual('missing', value['postgres']['state'])
        table = feedback.health_table({'health': value, 'images': dict.fromkeys(services)}, None)
        self.assertIn('| `extension-openapi` | restarting | unhealthy | 61 |', table)
        self.assertIn('| `server` | running | healthy | 0 |', table)
        self.assertNotIn('foreign', table)

    def test_duplicate_unknown_and_invalid_values_never_claim_healthy_or_export_raw_values(self):
        value = health.summarize(['server', 'worker'], [('server', 'running', 'healthy', 0),
            ('server', 'running', 'healthy', 0), ('worker', 'PRIVATE', 'PRIVATE', True)])
        self.assertEqual({'state': 'unknown', 'health': 'unknown', 'restarts': None}, value['server'])
        self.assertEqual(value['server'], value['worker'])
        self.assertNotIn('PRIVATE', json.dumps(value))
        for field, invalid in (('state', 'PRIVATE'), ('health', 'PRIVATE'), ('restarts', True)):
            row = {'state': 'running', 'health': 'healthy', 'restarts': 0, field: invalid}
            with self.subTest(field=field), self.assertRaises(ValueError):
                feedback.health_table({'health': {'server': row}, 'images': {'server': {}}}, None)

    def test_stopped_vm_without_fresh_observations_does_not_claim_missing_or_healthy_containers(self):
        table = feedback.health_table({'health': {}, 'images': {'server': {}, 'worker': {}}}, None)
        self.assertIn('| `server` | not observed | not observed | — |', table)
        self.assertNotIn('healthy', table)
        self.assertEqual({}, lifecycle.result(azure_request(), 'validated')['health'])

    def test_status_card_displays_component_health_without_the_redundant_access_sentence(self):
        request = azure_request()
        result = azure.azure_result(request, 'running', [], guest={'health': health.summarize(['server', 'worker'],
            [('server', 'running', 'healthy', 0), ('worker', 'running', 'healthy', 1)])})
        result['url'] = 'https://dev-example.agentstration.cloud'
        text = feedback.completed(result, 'status', 'owner/repo', 'https://github.com/owner/repo/actions/runs/1', request)
        self.assertIn('| `worker` | running | healthy | 1 |', text)
        self.assertIn('[**Open Agentstration**]', text)
        self.assertNotIn('Access protected by Cloudflare Access / Entra', text)

    def test_collection_selects_only_status_fields_without_configuration_errors_or_health_logs(self):
        calls = []
        def command(arguments, **kwargs):
            calls.append(arguments)
            self.assertTrue(kwargs['capture_output'])
            self.assertEqual(60, kwargs['timeout'])
            return subprocess.CompletedProcess(arguments, 0,
                'id1\nid2\n' if arguments[1] == 'ps' else '["server","running","healthy",0]\n["worker","exited","none",4]\n')
        with patch.object(subprocess, 'run', side_effect=command):
            value = health.collect('preview-pr-17', ['server', 'worker'])
        self.assertIn('label=com.docker.compose.project=preview-pr-17', calls[0])
        self.assertEqual(['id1', 'id2'], calls[1][-2:])
        self.assertEqual('exited', value['worker']['state'])
        for private in ('.Config.Env', '.State.Error', '.State.Health.Log'):
            self.assertNotIn(private, ' '.join(calls[1]))

    def test_collection_is_bounded_and_fails_without_exporting_inspect_output(self):
        with patch.object(subprocess, 'run', return_value=subprocess.CompletedProcess([], 0, 'id\n' * 65)) as command:
            with self.assertRaises(ValueError):
                health.collect('preview-pr-17', ['server'])
            self.assertEqual(1, command.call_count)

    def test_status_carries_current_collector_without_upgrading_or_redeploying_old_guest_tooling(self):
        script = azure.guest_script('status', azure_request())
        self.assertIn('azure_host.py status', script)
        self.assertNotIn('DOWNLOAD', script)
        source = script.split("python3 - <<'HEALTH'\n", 1)[1].split('\nHEALTH\n', 1)[0]
        # Compile exactly the script embedded in Run Command, independently of old guest modules.
        compile(source, '<trusted-health-probe>', 'exec')
        self.assertIn('AGENTSTRATION_HEALTH=', source)
        self.assertIn('preview-example', source)

    def test_guest_response_stays_below_run_command_limit_with_sixteen_extensions(self):
        names = ['extension-' + ('x' * 30) + chr(97 + i) for i in range(16)]
        services = ['server', 'worker', 'postgres', *names]
        value = {'state': 'running', 'health': health.summarize(services, [(n, 'restarting', 'unhealthy', 2147483647) for n in services]),
                 'observedImages': {n: 'localhost:5001/gbaudrit/agentstration-' + n + '@sha256:' + 'a' * 64 for n in services[:3]},
                 'extensionsVerified': [n.removeprefix('extension-') for n in names],
                 'expiresAt': '2026-11-26T18:30:00+00:00', 'accessReady': True,
                 'guestOperationSeconds': 1800.5, 'memoryAvailableKiB': 12345678}
        self.assertLess(len(('AGENTSTRATION_RESULT=' + json.dumps(value, separators=(',', ':'))).encode()), 4096)
        health_marker = 'AGENTSTRATION_HEALTH=' + json.dumps(value.pop('health'), separators=(',', ':'))
        self.assertLess(len(('AGENTSTRATION_RESULT=' + json.dumps(value, separators=(',', ':')) + '\n' + health_marker).encode()), 4096)
