"""Recipe selection, target-only credentials and isolated dependency lifecycle."""
import copy
import json
from pathlib import Path
import stat
import tempfile
import unittest
from unittest.mock import patch

import contract
import extension_deployments as recipes
from extension_deployments import postgresql
import extensions
import extension_tls
import issue_commands
import preview
import preview_feedback
import lifecycle
import reconcile
import test_lifecycle
import test_reconcile
from test_contract import request, receipts
from test_extensions import receipt
from test_issue_commands import GitHub, REPOSITORY


def selected(**kwargs):
    return request(extension_deliveries={'postgresql': receipt('postgresql')}, **kwargs)


def mock_certificate_generation(test):
    def create(directory):
        preview.private_write(directory / extension_tls.CERTIFICATE, 'public-disposable-certificate-fixture')
        preview.private_write(directory / extension_tls.PRIVATE_KEY, 'public-disposable-private-key-fixture')
    patcher = patch.object(extension_tls, 'create', side_effect=create)
    generated = patcher.start()
    test.addCleanup(patcher.stop)
    return generated


class RecipeSelectionTests(unittest.TestCase):
    def test_modifier_selects_external_mode_without_becoming_an_extension_id(self):
        labels = ['preview:extension:postgresql', 'preview:extension:postgresql:externalDB']
        self.assertEqual(['postgresql'], extensions.from_labels(labels))
        self.assertEqual({'postgresql': {'database': 'external'}}, extensions.options_from_labels(labels))
        for invalid in ([labels[1]], labels + [labels[1]], [labels[0], labels[0]],
                        [labels[0], labels[1].lower()], ['preview:extension:foundry:externalDB']):
            with self.subTest(labels=invalid), self.assertRaises(ValueError):
                extensions.from_labels(invalid)

    def test_new_request_freezes_internal_dependency_digest_and_preserves_sqlite_default(self):
        value = selected(source_sha='e' * 40)
        self.assertEqual(5, value['schemaVersion'])
        self.assertEqual('sqlite', value['database'])
        self.assertEqual('e' * 40, value['sourceSha'])
        self.assertEqual('internal', value['extensionDeployments']['postgresql']['database'])
        self.assertIn('@sha256:', contract.images(value)['postgres-documents']['reference'])
        self.assertEqual(value, contract.loads(json.dumps(value)))
        external = selected(extension_options={'postgresql': {'database': 'external'}})
        self.assertNotIn('postgres-documents', contract.images(external))
        self.assertEqual({'postgresql': {'database': 'external'}}, external['extensionDeployments'])
        self.assertIn('postgres', contract.images(selected(database='postgresql')))
        self.assertIn('postgres-documents', contract.images(selected(database='postgresql')))

    def test_legacy_requests_remain_readable_without_implicitly_adding_a_database(self):
        value = selected()
        del value['extensionDeployments']
        value['schemaVersion'] = 2
        self.assertEqual(value, contract.validate(value))
        self.assertNotIn('postgres-documents', contract.images(value))

    def test_bad_modes_credentials_unknown_options_and_mutable_images_are_refused(self):
        for options in ([], {'unknown': {}}, {'postgresql': {'database': 'other'}},
                        {'postgresql': {'password': 'public-disposable-fixture'}}):
            with self.subTest(options=options), self.assertRaises(ValueError):
                selected(extension_options=options)
        for extra in ({'image': 'postgres:latest'}, {'password': 'public-disposable-fixture'}):
            value = selected()
            value['extensionDeployments']['postgresql'].update(extra)
            with self.subTest(extra=extra), self.assertRaises(ValueError):
                contract.validate(value)

    def test_internal_mode_requires_an_image_with_the_deployment_recipe_contract(self):
        old = receipt('postgresql')
        old['extension'].pop('deploymentRecipeVersion', None)
        with self.assertRaisesRegex(ValueError, 'recipe-compatible'):
            request(extension_deliveries={'postgresql': old})
        external = request(extension_deliveries={'postgresql': old}, extension_options={'postgresql': {'database': 'external'}})
        contract.validate(external)

    def test_feedback_exposes_the_managed_dependency_health_and_external_cleanup_boundary(self):
        value = selected()
        table = preview_feedback.health_table({'health': {'postgres-documents':
                    {'state': 'running', 'health': 'healthy', 'restarts': 0}}}, value)
        self.assertIn('postgres-documents', table)
        self.assertIn('healthy', table)
        self.assertIn('**internal**', ' '.join(preview_feedback.recipe_details(value)))
        external = selected(extension_options={'postgresql': {'database': 'external'}})
        self.assertIn('outside preview cleanup', ' '.join(preview_feedback.recipe_details(external)))

    def test_issue_admission_freezes_modifier_and_rechecks_it_before_execution(self):
        github = GitHub()
        github.issue['labels'] = [{'name': name} for name in
                                  ['preview:extension:postgresql', 'preview:extension:postgresql:externalDB']]
        self.assertTrue(github.admit())
        options = github.selection()
        self.assertEqual({'postgresql': {'database': 'external'}}, options['extension_options'])
        issue_commands.tracking(options, REPOSITORY, github)
        github.issue['labels'].pop()
        with self.assertRaisesRegex(ValueError, 'modifiers changed'):
            issue_commands.tracking(options, REPOSITORY, github)


class RecipePreparationTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix='document-recipe-')
        self.addCleanup(temporary.cleanup)
        self.directory = Path(temporary.name) / 'preview'
        mock_certificate_generation(self)

    def prepare(self, external=False, settings=None, selected_extension=True):
        plans = recipes.freeze(['postgresql'], {'postgresql': {'database': 'external'}} if external else {}) if selected_extension else {}
        return preview.prepare(self.directory, *receipts(), 'documents', extension_deliveries=
                               {'postgresql': receipt('postgresql')} if selected_extension else {},
                               extension_configuration=settings, extension_deployments=plans)

    def test_password_is_generated_only_on_target_and_retained_across_updates_and_reselection(self):
        value = selected()
        with patch.object(postgresql.secrets, 'token_urlsafe', wraps=postgresql.secrets.token_urlsafe) as generated:
            self.assertTrue('password' not in json.dumps(value).lower())
            self.assertEqual(0, generated.call_count)
            state = self.prepare()
        password = (self.directory / postgresql.SECRET_FILE).read_text()
        self.assertEqual(0o600, stat.S_IMODE((self.directory / postgresql.SECRET_FILE).stat().st_mode))
        self.assertEqual('sqlite', state['database'])
        self.prepare()
        self.prepare(selected_extension=False)
        self.prepare()
        self.assertTrue(password == (self.directory / postgresql.SECRET_FILE).read_text(), 'Generated credentials changed during an ordinary update.')
        self.assertTrue(password not in json.dumps(state), 'Credentials leaked into public host metadata.')

    def test_overlay_includes_private_dependency_health_and_bootstrap_mounts_without_plaintext_password(self):
        state = self.prepare()
        overlay = extensions.overlay(self.directory, state)
        database = overlay['services']['postgres-documents']
        self.assertNotIn('ports', database)
        self.assertEqual('pg_isready -U "$${POSTGRES_USER}" -d "$${POSTGRES_DB}"', database['healthcheck']['test'][1])
        self.assertNotIn('POSTGRES_PASSWORD', database['environment'])
        server = overlay['services']['server']
        self.assertEqual('postgres-documents', server['environment'][postgresql.PARAMETERS + 'Host'])
        self.assertNotIn(postgresql.SECRET_VARIABLE, server['environment'])
        self.assertIn({'source': 'aep-postgresql', 'target': '/run/secrets/aep-postgresql'}, server['secrets'])
        self.assertIn('postgresql-documents-password', overlay['secrets'])
        password = (self.directory / postgresql.SECRET_FILE).read_text()
        self.assertTrue(password not in json.dumps(overlay), 'Credentials leaked into Compose metadata.')
        version = state['extensionConfigurations']['postgresql']
        settings = preview.read_json(extensions.configuration_path(self.directory, 'postgresql', version))
        self.assertEqual(['postgres-documents'], settings['PostgreSqlDocuments']['AllowedHostsWithoutTls'])
        self.assertEqual([state['instanceId']], settings['PostgreSqlDocuments']['AllowedClients'])

    def test_missing_persisted_password_and_mode_changes_fail_closed(self):
        self.prepare()
        with self.assertRaisesRegex(ValueError, 'explicit redeploy'):
            self.prepare(external=True)
        (self.directory / postgresql.SECRET_FILE).unlink()
        with self.assertRaisesRegex(ValueError, 'Restore the original'):
            self.prepare()

    def test_external_connection_is_private_and_not_forwarded_as_extension_configuration(self):
        # This value is deliberately public disposable test data, never an actual external credential.
        connection = {'Host': 'database.example.test', 'Port': 5432, 'Database': 'documents',
                      'Username': 'documents', 'Password': 'public-disposable-fixture'}
        settings = {'postgresql': {'PreviewDatabase': connection}}
        before = copy.deepcopy(settings)
        state = self.prepare(external=True, settings=settings)
        self.assertEqual(before, settings)
        self.assertNotIn('postgres-documents', state['images'])
        overlay = extensions.overlay(self.directory, state)
        self.assertNotIn('postgres-documents', overlay['services'])
        self.assertFalse(overlay['volumes'].get('postgres-documents'))
        self.assertEqual('database.example.test', overlay['services']['server']['environment'][postgresql.PARAMETERS + 'Host'])
        self.assertTrue(connection['Password'] not in json.dumps(overlay), 'External credentials leaked into Compose.')
        version = state['extensionConfigurations']['postgresql']
        actual = preview.read_json(extensions.configuration_path(self.directory, 'postgresql', version))
        self.assertNotIn('PreviewDatabase', actual)
        self.assertNotIn('AllowedHostsWithoutTls', actual['PostgreSqlDocuments'])
        self.prepare(external=True)

    def test_external_mode_with_missing_settings_fails_before_creating_an_internal_password(self):
        with self.assertRaisesRegex(ValueError, 'external document database connection settings'):
            self.prepare(external=True)
        self.assertFalse((self.directory / postgresql.SECRET_FILE).exists())

    def test_external_credentials_can_be_provisioned_on_target_without_ci_transport(self):
        self.directory.mkdir()
        private = {'Host': 'database.example.test', 'Port': 5432, 'Database': 'documents',
                   'Username': 'documents', 'Password': 'public-disposable-fixture'}
        preview.private_write(self.directory / postgresql.EXTERNAL_FILE, json.dumps(private))
        state = self.prepare(external=True)
        self.assertNotIn('postgres-documents', state['images'])
        self.assertTrue((self.directory / postgresql.SECRET_FILE).read_text() == private['Password'])
        recipes.reset(self.directory, state)
        for name in (postgresql.SECRET_FILE, postgresql.EXTERNAL_FILE, postgresql.ENTRYPOINT_FILE):
            self.assertFalse((self.directory / name).exists())
        # Reset only touches host credentials. It has no SQL connection or external deletion operation.
        self.assertEqual({}, state['extensionDeploymentHistory'])

    def test_unrelated_host_files_and_credential_symlinks_are_not_adopted(self):
        self.directory.mkdir()
        (self.directory / 'unrelated').write_text('public disposable fixture')
        with self.assertRaisesRegex(ValueError, 'unrelated nonempty'):
            self.prepare(external=True)
        (self.directory / 'unrelated').unlink()
        (self.directory / postgresql.EXTERNAL_FILE).symlink_to(self.directory / 'outside')
        with self.assertRaisesRegex(ValueError, 'unrelated nonempty'):
            self.prepare(external=True)

    def test_external_private_file_is_bounded_and_rejects_duplicate_fields_without_echoing_values(self):
        self.directory.mkdir()
        path = self.directory / postgresql.EXTERNAL_FILE
        marker = 'public-disposable-sensitive-fixture'
        for content in ('{"Password":"' + marker + '","Password":"other"}', marker * 1000):
            path.write_text(content)
            with self.subTest(size=len(content)), self.assertRaises(ValueError) as caught:
                postgresql.read_external(path)
            self.assertNotIn(marker, str(caught.exception))


class RecipeLifecycleTests(unittest.TestCase):
    def setUp(self):
        test_lifecycle.LifecycleTests.setUp(self)
        mock_certificate_generation(self)

    def test_deselection_retains_database_volume_and_credentials_until_explicit_reset(self):
        value = selected()
        first = lifecycle.execute('deploy', value, self.root)
        directory = self.root / value['environmentId']
        password = (directory / postgresql.SECRET_FILE).read_text()
        labels = {lifecycle.PROJECT: 'preview-' + value['environmentId'],
                  lifecycle.OWNER: preview.read_json(directory / 'deployment.json')['deploymentOwnerId']}
        self.host.resources['volumes'].append({'Name': 'fixture-document-volume', 'Labels': labels})
        core = request()
        lifecycle.execute('deploy', core, self.root)
        self.assertFalse(any(item['Config']['Labels']['com.docker.compose.service'] == 'postgres-documents'
                             for item in self.host.resources['containers']))
        self.assertTrue(any(item['Name'] == 'fixture-document-volume' for item in self.host.resources['volumes']))
        again = lifecycle.execute('deploy', value, self.root)
        self.assertEqual(first['expiresAt'], again['expiresAt'])
        self.assertTrue(password == (directory / postgresql.SECRET_FILE).read_text(), 'Deselection replaced a persisted database credential.')
        removed = lifecycle.execute('destroy', {**value, 'dataMode': 'reset'}, self.root)
        self.assertEqual('destroyed', removed['state'])
        self.assertFalse(any(self.host.resources.values()))
        self.assertFalse((directory / postgresql.SECRET_FILE).exists())

    def test_explicit_reset_allows_mode_change_without_ever_creating_or_destroying_external_database_resources(self):
        internal = selected()
        lifecycle.execute('deploy', internal, self.root)
        external = selected(extension_options={'postgresql': {'database': 'external'}})
        private = {'postgresql': {'PreviewDatabase': {'Host': 'external.example.test', 'Port': 5432,
                   'Database': 'documents', 'Username': 'documents', 'Password': 'public-disposable-fixture'}}}
        with self.assertRaisesRegex(ValueError, 'explicit redeploy'):
            lifecycle.execute('deploy', external, self.root, extension_configuration=private)
        lifecycle.execute('destroy', {**internal, 'dataMode': 'reset'}, self.root)
        lifecycle.execute('deploy', external, self.root, extension_configuration=private)
        self.assertNotIn('postgres-documents', contract.images(external))
        lifecycle.execute('destroy', {**external, 'dataMode': 'reset'}, self.root)
        self.assertFalse(any(self.host.resources.values()))

    def test_auto_reconciliation_detects_mode_changes_without_treating_a_new_infrastructure_pin_as_new_intent(self):
        approval = {'sourceSha': 'a' * 40, 'optInEvent': 77}
        value = selected()
        record = {'request': value, 'automation': approval, 'destroyed': False, 'initialized': True}
        self.assertFalse(reconcile.eligible_change(record, approval, ['postgresql']))
        self.assertTrue(reconcile.eligible_change(record, approval, ['postgresql'], {'postgresql': {'database': 'external'}}))
        value['extensionDeployments']['postgresql']['image'] = 'docker.io/library/postgres:17@sha256:' + 'a' * 64
        self.assertFalse(reconcile.eligible_change(record, approval, ['postgresql']))

    def test_azure_mode_change_is_refused_under_lease_before_public_record_or_resources_change(self):
        import azure
        from test_azure import Cloud, azure_request
        cloud = Cloud()
        internal = azure_request(extension_deliveries={'postgresql': receipt('postgresql')})
        transfer = cloud.transfer
        with patch.object(cloud, 'transfer', side_effect=lambda **kwargs: {**transfer(**kwargs), 'extensionsVerified': ['postgresql']}):
            azure.execute('deploy', internal, cloud.factory, route_factory=lambda *args: None)
        original = copy.deepcopy(cloud.record)
        cloud.effects.clear()
        external = azure_request(extension_deliveries={'postgresql': receipt('postgresql')},
                                 extension_options={'postgresql': {'database': 'external'}})
        with self.assertRaisesRegex(ValueError, 'explicit redeploy'):
            azure.execute('deploy', external, cloud.factory, route_factory=lambda *args: None)
        self.assertEqual(original, cloud.record)
        self.assertEqual([], cloud.effects)
        self.assertFalse(cloud.leased)


class RecipeReconciliationTests(unittest.TestCase):
    setUp = test_reconcile.ReconciliationTests.setUp
    execute = test_reconcile.ReconciliationTests.execute
    label = test_reconcile.ReconciliationTests.label

    def test_changed_database_mode_resets_once_and_rechecks_the_modifier_under_lease(self):
        from test_azure import Cloud, azure_request
        self.label('preview:extension:postgresql')
        self.label(reconcile.REDEPLOY, 79)
        original = Cloud.transfer

        def transfer(cloud, **kwargs):
            return {**original(cloud, **kwargs), 'extensionsVerified': list(cloud.request.get('extensions', {}))}

        def resolve(options, *args, **kwargs):
            value = azure_request(extension_deliveries={'postgresql': receipt('postgresql')},
                                  extension_options=options.get('extension_options'))
            value['environmentId'] = 'pr-1'
            return value

        with patch.object(Cloud, 'transfer', transfer), patch.object(reconcile.manual, 'resolve', resolve):
            self.assertFalse(self.execute()['failures'])
            cloud = self.registry.clouds['pr-1']
            self.label('preview:extension:postgresql:externalDB', 80)
            self.assertEqual('redeploy', self.execute()['actions'][0]['operation'])
            self.assertEqual(1, cloud.effects.count('reset'))
            self.assertFalse(self.execute()['actions'])
            self.assertEqual(1, cloud.effects.count('reset'))
            self.api.pull['labels'] = [label for label in self.api.pull['labels']
                                       if label['name'] != 'preview:extension:postgresql:externalDB']
            before = copy.deepcopy(cloud.record)
            executor = self.registry.execute

            def stale(action, request, **kwargs):
                if action == 'deploy':
                    self.api.pull['labels'].append({'name': 'preview:extension:postgresql:externalDB'})
                return executor(action, request, **kwargs)

            self.registry.execute = stale
            self.assertTrue(self.execute()['failures'])
            self.assertEqual(before, cloud.record)
            self.assertEqual(1, cloud.effects.count('reset'))


if __name__ == '__main__':
    unittest.main()
