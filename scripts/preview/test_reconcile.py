"""Exercise live metadata admission and reconciliation through the existing Azure lifecycle with fake providers."""
import copy
import json
from datetime import datetime, timedelta, timezone
import unittest
from unittest.mock import Mock
from unittest.mock import patch
import capacity

import azure
import manual
import reconcile
from test_azure import Cloud, azure_request
from test_manual import Metadata, VARIABLES

NOW = datetime(2026, 11, 26, 12, tzinfo=timezone.utc)
REPOSITORY = 'owner/repo'
ROOT = 'repos/' + REPOSITORY


class API:
    def __init__(self):
        self.pull = {'number': 1, 'state': 'open', 'draft': False, 'labels': [{'name': reconcile.LABEL}],
                     'base': {'repo': {'full_name': REPOSITORY}},
                     'head': {'sha': 'a' * 40, 'repo': {'full_name': REPOSITORY}}}
        self.events = [{'id': 77, 'event': 'labeled', 'label': {'name': reconcile.LABEL},
                        'created_at': (NOW - timedelta(hours=4)).isoformat(),
                        'actor': {'login': 'maintainer', 'type': 'User'}}]
        self.permission = 'write'
        self.comments, self.calls = [], []

    def __call__(self, path, method='GET', body=None):
        self.calls.append((path, method, copy.deepcopy(body)))
        bare = path.partition('?')[0]
        if bare == ROOT + '/pulls':
            return [copy.deepcopy(self.pull)] if self.pull['state'] == 'open' else []
        if bare == ROOT + '/pulls/1':
            return copy.deepcopy(self.pull)
        if bare == ROOT + '/issues/1/events':
            return copy.deepcopy(self.events)
        if bare.endswith('/collaborators/maintainer/permission'):
            return {'permission': self.permission}
        if bare == ROOT + '/issues/1/labels':
            if method == 'POST':
                self.pull['labels'] += [{'name': name} for name in body['labels']]
            return copy.deepcopy(self.pull['labels'])
        if '/labels/' in bare and method == 'DELETE':
            label = bare.split('/labels/', 1)[1].replace('%3A', ':')
            self.pull['labels'] = [item for item in self.pull['labels'] if item['name'] != label]
            return None
        if bare == ROOT + '/issues/1/comments':
            if method == 'POST':
                self.comments.append({'id': 900, 'body': body['body'], 'issue_url': 'https://api.github.com/' + ROOT + '/issues/1',
                                      'user': {'login': 'github-actions[bot]', 'type': 'Bot'}})
                return copy.deepcopy(self.comments[-1])
            return copy.deepcopy(self.comments)
        if bare == ROOT + '/issues/comments/900':
            if method == 'PATCH':
                self.comments[0]['body'] = body['body']
            return copy.deepcopy(self.comments[0])
        raise AssertionError('Unexpected API endpoint: ' + bare)


class Registry:
    def __init__(self):
        self.clouds, self.calls = {}, []

    def execute(self, action, request, **kwargs):
        self.calls.append((action, copy.deepcopy(request), kwargs))
        cloud = self.clouds.setdefault(request['environmentId'], Cloud())
        value = azure.execute(action, request, cloud.factory, now=NOW, route_factory=lambda *args: None, **kwargs)
        # This provider fixture simulates successful protection; real Routes verification has its own tests.
        if value['state'] == 'running':
            value['url'] = 'https://dev-' + request['environmentId'] + '.agentstration.cloud'
        return value

    def create(self, github, age=0, automatic=False):
        request = azure_request()
        request.update(environmentId='pr-1', ttlHours=2)
        cloud = self.clouds.setdefault('pr-1', Cloud())
        azure.execute('deploy', request, cloud.factory, now=NOW - timedelta(hours=age), route_factory=lambda *args: None,
                      automation_context=github.approval(github.pull(1)) if automatic else None)
        return cloud

    def records(self):
        return [{'prefix': azure.prefix(cloud.record['request']), 'record': copy.deepcopy(cloud.record)}
                for cloud in self.clouds.values() if cloud.record]


class ReconciliationTests(unittest.TestCase):
    def test_new_pr_revision_can_reuse_older_server_and_worker_without_inventing_image_sources(self):
        self.metadata.ci_components = []
        self.api.pull['head']['sha'] = 'b' * 40
        value = self.execute()
        self.assertEqual([], value['failures'])
        self.assertEqual([], value['pending'])
        request = self.registry.calls[-1][1]
        self.assertEqual(4, request['schemaVersion'])
        self.assertEqual('b' * 40, request['sourceSha'])
        self.assertEqual('a' * 40, request['deliveries']['server']['sourceSha'])
        self.assertEqual('c' * 40, request['deliveries']['worker']['sourceSha'])
        self.assertEqual([], self.execute()['actions'])

    def setUp(self):
        self.api = API()
        self.github = reconcile.GitHub(REPOSITORY, self.api)
        self.registry, self.metadata = Registry(), Metadata()
        self.selector = manual.PublishedImages(REPOSITORY, self.metadata.run)

    def execute(self, apply=True, records=None, resources=None, **kwargs):
        return reconcile.reconcile(self.github, self.registry.records() if records is None else records,
            resources or [], VARIABLES, apply=apply, execute=self.registry.execute, selector=self.selector, now=NOW, **kwargs)

    def label(self, label, identifier=78):
        self.api.pull['labels'].append({'name': label})
        self.api.events.append({**self.api.events[0], 'id': identifier, 'label': {'name': label}})

    def publish_new_head(self):
        self.api.pull['head']['sha'] = 'b' * 40
        image = 'ghcr.io/owner/repo-server'
        self.metadata.configs[image]['config']['Labels']['org.opencontainers.image.revision'] = 'b' * 40
        self.metadata.manifests[image] = json.dumps({'schemaVersion': 2, 'image': 'server', 'revision': 'b' * 40}).encode()

    def test_redeploy_modifier_resets_each_new_intent_once_but_not_each_poll(self):
        cloud = self.registry.create(self.github, automatic=True)
        owner = cloud.record['ownerId']
        self.label(reconcile.REDEPLOY)
        value = self.execute()
        self.assertEqual([], value['failures'])
        self.assertEqual('redeploy', value['actions'][0]['operation'])
        self.assertNotEqual(owner, cloud.record['ownerId'])
        self.assertEqual(1, cloud.effects.count('reset'))
        self.assertEqual([], self.execute()['actions'])
        self.assertEqual(1, cloud.effects.count('reset'))
        self.publish_new_head()
        self.assertEqual('redeploy', self.execute()['actions'][0]['operation'])
        self.assertEqual(2, cloud.effects.count('reset'))
        self.api.pull['labels'] = [item for item in self.api.pull['labels'] if item['name'] != reconcile.REDEPLOY]
        self.assertEqual('deploy', self.execute()['actions'][0]['operation'])
        self.assertEqual(2, cloud.effects.count('reset'))

    def test_redeploy_modifier_alone_or_unverified_grant_never_resets(self):
        self.api.pull['labels'] = [{'name': reconcile.REDEPLOY}]
        self.assertEqual([], self.execute()['actions'])
        cloud = self.registry.create(self.github)
        self.api.pull['labels'].append({'name': reconcile.LABEL})
        value = self.execute()
        self.assertTrue(value['failures'])
        self.assertNotIn('reset', cloud.effects)

    def test_same_intent_startup_and_health_recovery_never_reset_again(self):
        self.label(reconcile.REDEPLOY)
        cloud = self.registry.create(self.github, automatic=True)
        cloud.record['initialized'] = False
        self.assertEqual('deploy', self.execute()['actions'][0]['operation'])
        self.assertNotIn('reset', cloud.effects)
        original = self.registry.execute
        def unhealthy(action, request, **kwargs):
            return {'state': 'unhealthy'} if action == 'status' else original(action, request, **kwargs)
        self.registry.execute = unhealthy
        self.assertEqual('deploy', self.execute()['actions'][0]['operation'])
        self.assertNotIn('reset', cloud.effects)

    def test_reset_mode_is_rechecked_under_the_provider_lease(self):
        cloud = self.registry.create(self.github, automatic=True)
        self.label(reconcile.REDEPLOY)
        original = self.registry.execute
        def revoked(action, request, **kwargs):
            if action == 'deploy':
                self.api.pull['labels'] = [item for item in self.api.pull['labels'] if item['name'] != reconcile.REDEPLOY]
            return original(action, request, **kwargs)
        self.registry.execute = revoked
        self.assertTrue(self.execute()['failures'])
        self.assertNotIn('reset', cloud.effects)

    def test_keep_retains_expired_closed_and_opted_out_previews_until_explicit_destroy(self):
        for condition in ('expired', 'closed', 'opt-out'):
            with self.subTest(condition=condition):
                self.setUp()
                cloud = self.registry.create(self.github, age=3 if condition == 'expired' else 0, automatic=True)
                if condition == 'closed':
                    self.api.pull['state'] = 'closed'
                if condition == 'opt-out':
                    self.api.pull['labels'] = []
                self.label(reconcile.KEEP)
                value = self.execute()
                self.assertEqual([], value['failures'])
                self.assertNotIn('reset', cloud.effects)
                self.assertFalse(cloud.record['destroyed'])
                self.registry.execute('destroy', {**cloud.record['request'], 'dataMode': 'reset'})
                self.assertEqual([], self.execute()['actions'])
                self.assertTrue(cloud.record['destroyed'])

    def test_keep_does_not_create_or_resurrect_an_environment(self):
        self.api.pull['labels'] = []
        self.label(reconcile.KEEP)
        self.assertEqual([], self.execute()['actions'])
        self.assertEqual({}, self.registry.clouds)

    def test_unverified_keep_cannot_bypass_expiry_and_removal_resumes_cleanup(self):
        cloud = self.registry.create(self.github, age=3, automatic=True)
        self.api.pull['labels'].append({'name': reconcile.KEEP})
        self.assertEqual('destroy', self.execute()['actions'][0]['operation'])
        self.setUp()
        cloud = self.registry.create(self.github, age=3, automatic=True)
        self.label(reconcile.KEEP)
        self.assertEqual([], self.execute()['actions'])
        self.api.pull['labels'] = [item for item in self.api.pull['labels'] if item['name'] != reconcile.KEEP]
        self.assertEqual('ttl', self.execute()['actions'][0]['reason'])

    def test_keep_added_during_cleanup_planning_blocks_the_stale_delete(self):
        cloud = self.registry.create(self.github, age=3, automatic=True)
        original = self.registry.execute
        def retained(action, request, **kwargs):
            if action == 'destroy':
                self.label(reconcile.KEEP)
            return original(action, request, **kwargs)
        self.registry.execute = retained
        self.assertTrue(self.execute()['failures'])
        self.assertNotIn('reset', cloud.effects)

    def test_pending_deletion_finishes_even_if_keep_is_added_after_deletion_started(self):
        cloud = self.registry.create(self.github, age=3, automatic=True)
        self.api.pull['labels'].append({'name': 'preview:status:deployed'})
        with patch.object(cloud, 'reset', side_effect=RuntimeError('fixture')):
            self.assertTrue(self.execute()['failures'])
        self.assertNotIn('preview:status:deployed', [item['name'] for item in self.api.pull['labels']])
        self.label(reconcile.KEEP)
        self.assertEqual('destroy', self.execute()['actions'][0]['operation'])
        self.assertTrue(cloud.record['destroyed'])

    def test_retained_issue_preview_stays_reserved_and_does_not_require_an_open_pr(self):
        cloud = self.registry.create(self.github, age=3)
        cloud.record['request']['environmentId'] = 'issue-1'
        value = {'number': 1, 'state': 'closed', 'labels': [{'name': reconcile.KEEP}]}
        self.label(reconcile.KEEP)
        original = self.api.__call__
        def api(path, method='GET', body=None):
            if path == ROOT + '/issues/1':
                return copy.deepcopy(value)
            return original(path, method, body)
        self.github.call = api
        self.api.pull['labels'] = []
        self.assertIsNone(reconcile.removal_reason(cloud.record, self.github, NOW))
        request = {**cloud.record['request'], 'environmentId': 'pr-2'}
        with patch.dict('os.environ', {'AZURE_PREVIEW_MAX_ENVIRONMENTS': '1'}):
            with self.assertRaises(capacity.Unavailable):
                capacity.enforce(request, self.registry.records(), [])

    def test_reset_waits_for_images_without_erasing_the_existing_preview(self):
        cloud = self.registry.create(self.github, automatic=True)
        self.label(reconcile.REDEPLOY)
        self.metadata.missing_all_tags = True
        value = self.execute()
        self.assertEqual('image-unavailable', value['pending'][0]['code'])
        self.assertNotIn('reset', cloud.effects)

    def test_status_probe_or_label_api_failure_is_bounded_and_does_not_delete_data(self):
        cloud = self.registry.create(self.github)
        self.api.pull['labels'] = []
        self.registry.execute = Mock(side_effect=RuntimeError('PRIVATE-REMOTE-CONTENT'))
        value = self.execute()
        self.assertEqual('status-unavailable', value['failures'][0]['code'])
        self.assertNotIn('PRIVATE-REMOTE-CONTENT', json.dumps(value))
        self.assertNotIn('reset', cloud.effects)
        self.setUp()
        cloud = self.registry.create(self.github)
        self.api.pull['labels'] = []
        self.github.sync_status = Mock(side_effect=RuntimeError('PRIVATE-REMOTE-CONTENT'))
        value = self.execute()
        self.assertEqual('status-labels-failed', value['failures'][0]['code'])
        self.assertNotIn('PRIVATE-REMOTE-CONTENT', json.dumps(value))
        self.assertFalse(cloud.record['destroyed'])

    def test_deployed_label_follows_live_status_and_is_never_an_opt_in(self):
        cloud = self.registry.create(self.github)
        self.api.pull['labels'] = []
        original = self.registry.execute
        def protected(action, request, **kwargs):
            value = original(action, request, **kwargs)
            if value['state'] == 'running':
                value['url'] = 'https://dev-pr-1.agentstration.cloud'
            return value
        self.registry.execute = protected
        self.assertEqual([], self.execute()['actions'])
        self.assertIn('preview:status:deployed', [item['name'] for item in self.api.pull['labels']])
        self.assertEqual([], self.execute()['actions'])
        self.registry.execute = lambda *args, **kwargs: {'state': 'unhealthy'}
        self.execute()
        self.assertNotIn('preview:status:deployed', [item['name'] for item in self.api.pull['labels']])
        self.registry.execute = original
        self.registry.execute('destroy', {**cloud.record['request'], 'dataMode': 'reset'})
        self.api.pull['labels'].append({'name': 'preview:status:deployed'})
        self.execute()
        self.assertNotIn('preview:status:deployed', [item['name'] for item in self.api.pull['labels']])

    def test_waiting_update_keeps_old_availability_and_success_clears_pending(self):
        self.registry.create(self.github, automatic=True)
        self.publish_new_head()
        self.metadata.missing_all_tags = True
        value = self.execute()
        self.assertEqual([], value['failures'])
        labels = {item['name'] for item in self.api.pull['labels']}
        self.assertTrue({'preview:status:deployed', 'preview:status:pending'} <= labels)
        self.assertNotIn('preview:status:error', labels)
        self.metadata.missing_all_tags = False
        self.assertEqual([], self.execute()['failures'])
        labels = {item['name'] for item in self.api.pull['labels']}
        self.assertIn('preview:status:deployed', labels)
        self.assertFalse({'preview:status:pending', 'preview:status:error'} & labels)

    def test_failed_update_keeps_old_availability_and_recovery_clears_error(self):
        self.registry.create(self.github, automatic=True)
        self.publish_new_head()
        original = self.registry.execute
        def failed(action, request, **kwargs):
            if action == 'deploy':
                raise RuntimeError('PRIVATE')
            return original(action, request, **kwargs)
        self.registry.execute = failed
        self.assertTrue(self.execute()['failures'])
        labels = {item['name'] for item in self.api.pull['labels']}
        self.assertTrue({'preview:status:deployed', 'preview:status:error'} <= labels)
        self.assertNotIn('preview:status:pending', labels)
        self.registry.execute = original
        self.assertEqual([], self.execute()['failures'])
        labels = {item['name'] for item in self.api.pull['labels']}
        self.assertIn('preview:status:deployed', labels)
        self.assertFalse({'preview:status:pending', 'preview:status:error'} & labels)

    def test_healthy_poll_keeps_unresolved_error_and_its_actionable_card(self):
        self.registry.create(self.github, automatic=True)
        self.github.publish('pr-1', '**Automatic preview not deployed**\n\nRetry the failed update.')
        original = self.api.comments[0]['body']
        self.api.pull['labels'].append({'name': 'preview:status:error'})
        self.assertEqual([], self.execute()['actions'])
        self.assertEqual(original, self.api.comments[0]['body'])
        self.assertIn('preview:status:error', {item['name'] for item in self.api.pull['labels']})
        self.publish_new_head()
        self.assertEqual([], self.execute()['failures'])
        self.assertNotIn('preview:status:error', {item['name'] for item in self.api.pull['labels']})
        self.assertIn('Preview ready', self.api.comments[0]['body'])

    def test_dry_run_resolves_private_images_but_never_mutates_cloud_or_comments(self):
        value = self.execute(apply=False)
        self.assertTrue(value['dryRun'])
        self.assertEqual('deploy', value['actions'][0]['operation'])
        self.assertEqual([], self.registry.calls)
        self.assertFalse(any(method != 'GET' for _, method, _ in self.api.calls))

    def test_opt_in_creates_one_preview_and_unchanged_intent_does_not_redeploy(self):
        value = self.execute()
        self.assertEqual([], value['failures'])
        cloud = self.registry.clouds['pr-1']
        self.assertEqual('a' * 40, cloud.record['automation']['sourceSha'])
        self.assertEqual(77, cloud.record['automation']['optInEvent'])
        self.assertEqual(1, len(self.api.comments))
        self.registry.calls = []
        self.assertEqual([], self.execute()['actions'])
        self.assertEqual(['status'], [call[0] for call in self.registry.calls])

    def test_unchanged_opted_in_preview_retries_failed_health_without_reset(self):
        cloud = self.registry.create(self.github, automatic=True)
        original = self.registry.execute
        def unhealthy(action, request, **kwargs):
            if action == 'status':
                return {'state': 'unhealthy'}
            return original(action, request, **kwargs)
        self.registry.execute = unhealthy
        value = self.execute()
        self.assertEqual([], value['failures'])
        self.assertEqual('deploy', value['actions'][0]['operation'])
        self.assertNotIn('reset', cloud.effects)

    def test_failed_route_publication_is_retried_even_after_guest_initialization(self):
        cloud = self.registry.create(self.github, automatic=True)
        approval = self.github.approval(self.github.pull(1))
        cloud.record['access'] = {'verified': False}
        self.assertTrue(reconcile.eligible_change(cloud.record, approval, []))

    def test_published_new_head_updates_preserving_owner_database_target_and_expiry(self):
        cloud = self.registry.create(self.github, age=1, automatic=True)
        previous = copy.deepcopy(cloud.record)
        self.api.pull['head']['sha'] = 'b' * 40
        image = 'ghcr.io/owner/repo-server'
        self.metadata.configs[image]['config']['Labels']['org.opencontainers.image.revision'] = 'b' * 40
        self.metadata.manifests[image] = json.dumps({'schemaVersion': 2, 'image': 'server', 'revision': 'b' * 40}).encode()
        value = self.execute()
        self.assertEqual([], value['failures'])
        self.assertEqual('b' * 40, cloud.record['request']['sourceSha'])
        self.assertEqual(previous['ownerId'], cloud.record['ownerId'])
        self.assertEqual(previous['expiresAt'], cloud.record['expiresAt'])
        self.assertEqual(previous['request']['target'], cloud.record['request']['target'])
        self.assertNotIn('reset', cloud.effects)

    def test_forks_drafts_bots_and_untrusted_label_actors_cannot_deploy(self):
        for fault in ('fork', 'draft', 'bot', 'read', 'missing-event'):
            with self.subTest(fault=fault):
                self.setUp()
                if fault == 'fork':
                    self.api.pull['head']['repo']['full_name'] = 'foreign/fork'
                elif fault == 'draft':
                    self.api.pull['draft'] = True
                elif fault == 'bot':
                    self.api.events[0]['actor']['type'] = 'Bot'
                elif fault == 'read':
                    self.api.permission = 'read'
                else:
                    self.api.events = []
                self.assertTrue(self.execute()['failures'])
                self.assertEqual([], self.registry.calls)
                self.assertEqual([], self.metadata.calls)

    def test_missing_images_wait_without_creating_or_changing_existing_environment(self):
        self.metadata.missing_all_tags = True
        value = self.execute()
        self.assertEqual('image-unavailable', value['pending'][0]['code'])
        self.assertEqual([], value['failures'])
        self.assertEqual([], self.registry.calls)
        self.assertIn('OCI images', self.api.comments[0]['body'])

    def test_capacity_is_pending_and_does_not_create_an_environment(self):
        self.registry.execute = Mock(side_effect=capacity.Unavailable('fixture'))
        value = self.execute()
        self.assertEqual([], value['failures'])
        self.assertEqual('capacity', value['pending'][0]['code'])
        self.assertEqual({}, self.registry.clouds)

    def test_changed_extension_selection_is_actionable_without_changing_the_source(self):
        cloud = self.registry.create(self.github, automatic=True)
        approval = self.github.approval(self.github.pull(1))
        self.assertFalse(reconcile.eligible_change(cloud.record, approval, []))
        self.assertTrue(reconcile.eligible_change(cloud.record, approval, ['foundry']))

    def test_expired_environment_is_removed_once_without_automatic_resurrection(self):
        cloud = self.registry.create(self.github, age=3, automatic=True)
        value = self.execute()
        self.assertEqual([], value['failures'])
        self.assertEqual(['destroy'], [call[0] for call in self.registry.calls])
        self.assertTrue(cloud.record['destroyed'])
        self.assertEqual('ttl', cloud.record['deletion']['reason'])
        self.registry.calls = []
        self.assertEqual([], self.execute()['actions'])
        self.assertEqual([], self.registry.calls)

    def test_feedback_failure_does_not_undo_confirmed_cleanup_or_export_remote_content(self):
        cloud = self.registry.create(self.github, age=3, automatic=True)
        self.github.publish = Mock(side_effect=RuntimeError('PRIVATE-REMOTE-CONTENT'))
        value = self.execute()
        self.assertTrue(cloud.record['destroyed'])
        self.assertEqual('feedback-failed', value['failures'][0]['code'])
        self.assertNotIn('PRIVATE-REMOTE-CONTENT', json.dumps(value))
        self.assertEqual('destroyed', self.registry.execute('status', cloud.record['request'])['state'])

    def test_closed_pr_or_removed_opt_in_cleans_saved_images_without_registry_reads(self):
        for reason in ('pr-closed', 'label-removed'):
            with self.subTest(reason=reason):
                self.setUp()
                cloud = self.registry.create(self.github, automatic=True)
                if reason == 'pr-closed':
                    self.api.pull['state'] = 'closed'
                    self.api.pull['head']['repo'] = None
                else:
                    self.api.pull['labels'] = []
                self.selector = Mock(side_effect=AssertionError('No registry access during cleanup'))
                value = self.execute()
                self.assertEqual([], value['failures'])
                self.assertEqual(reason, cloud.record['deletion']['reason'])
                self.assertEqual([], self.metadata.calls)

    def test_new_source_is_rechecked_under_provider_lease_before_any_creation(self):
        original = self.registry.execute
        def changed(action, request, **kwargs):
            self.api.pull['head']['sha'] = 'b' * 40
            return original(action, request, **kwargs)
        self.registry.execute = changed
        self.assertTrue(self.execute()['failures'])
        self.assertIsNone(self.registry.clouds['pr-1'].record)
        self.assertEqual([], self.registry.clouds['pr-1'].effects)

    def test_stale_automatic_creation_does_not_take_over_a_new_manual_lifecycle(self):
        original = self.registry.execute
        def created_manually(action, request, **kwargs):
            cloud = self.registry.clouds.setdefault('pr-1', Cloud())
            azure.execute('deploy', request, cloud.factory, now=NOW, route_factory=lambda *args: None)
            cloud.effects.clear()
            return original(action, request, **kwargs)
        self.registry.execute = created_manually
        self.assertTrue(self.execute()['failures'])
        cloud = self.registry.clouds['pr-1']
        self.assertNotIn('automation', cloud.record)
        self.assertEqual([], cloud.effects)

    def test_stale_expiry_snapshot_cannot_delete_a_recreated_environment(self):
        cloud = self.registry.create(self.github, age=3, automatic=True)
        snapshot = self.registry.records()
        cloud.record['expiresAt'] = (NOW + timedelta(hours=2)).isoformat()
        self.api.pull['labels'] = []
        # The stale TTL plan is rechecked against the current expiry; unrelated label removal is a separate reason.
        value = self.execute(records=snapshot)
        self.assertTrue(value['failures'])
        self.assertFalse(cloud.record['destroyed'])
        self.assertNotIn('reset', cloud.effects)

    def test_known_orphan_resources_are_retried_but_unknown_names_are_never_deleted(self):
        cloud = self.registry.create(self.github, automatic=True)
        cloud.record['destroyed'] = True
        value = self.execute(resources=[{'name': azure.prefix(cloud.record['request']) + '-data'}])
        self.assertEqual('destroy', value['actions'][0]['operation'])
        self.assertEqual([], value['failures'])
        self.setUp()
        value = self.execute(resources=[{'name': 'pv-' + 'f' * 20 + '-vm'}])
        self.assertEqual('unrecorded-resources', value['failures'][0]['code'])
        self.assertEqual([], self.registry.calls)

    def test_deletion_without_prior_auto_association_requires_a_later_explicit_opt_in(self):
        cloud = self.registry.create(self.github)
        self.registry.execute('destroy', {**cloud.record['request'], 'dataMode': 'reset'})
        approval = self.github.approval(self.github.pull(1))
        self.assertFalse(reconcile.eligible_change(cloud.record, approval, []))
        approval['optInAt'] = (NOW + timedelta(minutes=1)).isoformat()
        self.assertTrue(reconcile.eligible_change(cloud.record, approval, []))
        del cloud.record['deletion']
        self.assertFalse(reconcile.eligible_change(cloud.record, approval, []))

    def test_feedback_cannot_overwrite_a_human_comment_with_the_same_marker(self):
        self.github.publish('pr-1', 'status')
        self.api.comments[0]['user'] = {'type': 'User', 'login': 'maintainer'}
        # This creates its own bot card, never a patch to the human card.
        self.github.publish('pr-1', 'new status')
        self.assertFalse(any(method == 'PATCH' for _, method, _ in self.api.calls))

    def test_cloudflare_orphans_are_classified_without_deletion_or_unbounded_names(self):
        records = [{'prefix': 'pv-' + 'a' * 20, 'record': {'access': {'tunnelName': 'owned', 'hostname': 'dev-pr-1.agentstration.cloud'}}}]
        occupied, unknown = reconcile.route_inventory(records, [{'name': 'owned'}, {'name': 'agentstration-' + 'b' * 64}],
            [{'name': 'dev-pr-1.agentstration.cloud'}, {'name': 'dev-other.agentstration.cloud', 'comment': 'agentstration.preview.owner=fixture'}], 'agentstration.cloud')
        self.assertEqual({'pv-' + 'a' * 20}, occupied)
        self.assertEqual(2, unknown)

    def test_cloudflare_optional_dns_comments_do_not_turn_unowned_records_into_orphans(self):
        prefix = 'pv-' + 'a' * 20
        records = [{'prefix': prefix, 'record': {'access': {
            'tunnelName': 'owned', 'hostname': 'dev-pr-1.agentstration.cloud'}}}]
        for comment in (None, '', 'ordinary DNS note'):
            with self.subTest(comment=comment):
                occupied, unknown = reconcile.route_inventory(records, [], [
                    {'name': 'dev-unrelated.agentstration.cloud', 'comment': comment},
                    {'name': 'dev-pr-1.agentstration.cloud', 'comment': comment},
                    {'name': 'dev-no-comment.agentstration.cloud'},
                    {'name': 'dev-marked.agentstration.cloud', 'comment': 'agentstration.preview.owner=fixture'}
                ], 'agentstration.cloud')
                self.assertEqual({prefix}, occupied)
                self.assertEqual(1, unknown)
