"""Exercise live metadata admission and reconciliation through the existing Azure lifecycle with fake providers."""
import copy
import json
from datetime import datetime, timedelta, timezone
import unittest
from unittest.mock import Mock
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
        return azure.execute(action, request, cloud.factory, now=NOW, route_factory=lambda *args: None, **kwargs)

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
    def setUp(self):
        self.api = API()
        self.github = reconcile.GitHub(REPOSITORY, self.api)
        self.registry, self.metadata = Registry(), Metadata()
        self.selector = manual.PublishedImages(REPOSITORY, self.metadata.run)

    def execute(self, apply=True, records=None, resources=None, **kwargs):
        return reconcile.reconcile(self.github, self.registry.records() if records is None else records,
            resources or [], VARIABLES, apply=apply, execute=self.registry.execute, selector=self.selector, now=NOW, **kwargs)

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
