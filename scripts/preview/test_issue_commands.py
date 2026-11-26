"""Offline issue admission and feedback contracts; never dispatch real deployments."""
import copy
from contextlib import redirect_stdout
import io
import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

import issue_commands as commands
import manual
from test_azure import azure_request

REPOSITORY = 'gbaudrit/agentstration'
PREFIX = 'repos/' + REPOSITORY
SHA = 'a' * 40


class GitHub:
    def __init__(self, body='/preview deploy'):
        self.comment = {'id': 700, 'body': body, 'issue_url': 'https://api.github.com/' + PREFIX + '/issues/17',
                        'created_at': '2026-11-26T09:00:00Z', 'updated_at': '2026-11-26T09:00:00Z',
                        'user': {'id': 41, 'login': 'maintainer', 'type': 'User'}}
        self.issue = {'number': 17, 'state': 'open', 'body': 'Unrelated mention: PR #999'}
        self.permission = {'permission': 'write'}
        self.nodes = [self.pull(123)]
        self.more = False
        self.graphql_errors = None
        self.fork = False
        self.calls = []
        self.card = None
        self.head_sha = SHA

    @staticmethod
    def pull(number):
        return {'number': number, 'state': 'OPEN', 'repository': {'nameWithOwner': REPOSITORY},
                'headRepository': {'nameWithOwner': REPOSITORY}}

    def __call__(self, path, method='GET', body=None):
        self.calls.append((path, method, copy.deepcopy(body)))
        if method == 'POST' and path != 'graphql':
            if path.endswith('/comments'):
                self.card = {'id': 701, 'issue_url': self.comment['issue_url'], 'body': body['body'],
                             'user': {'login': 'github-actions[bot]', 'type': 'Bot'}}
                return copy.deepcopy(self.card)
            return None
        if path.endswith('/issues/comments/701'):
            if method == 'PATCH':
                self.card['body'] = body['body']
            return copy.deepcopy(self.card)
        if path.endswith('/issues/comments/700'):
            return copy.deepcopy(self.comment)
        if path.endswith('/collaborators/maintainer/permission'):
            return copy.deepcopy(self.permission)
        if path.endswith('/issues/17'):
            return copy.deepcopy(self.issue)
        if path == 'graphql':
            if self.graphql_errors:
                return {'errors': self.graphql_errors}
            return {'data': {'repository': {'issue': {'closedByPullRequestsReferences': {
                'nodes': copy.deepcopy(self.nodes), 'pageInfo': {'hasNextPage': self.more}}}}}}
        if '/pulls/' in path:
            return {'number': int(path.rsplit('/', 1)[1]), 'state': self.issue['state'],
                    'base': {'repo': {'full_name': REPOSITORY}},
                    'head': {'repo': {'full_name': 'foreign/fork' if self.fork else REPOSITORY}, 'sha': self.head_sha}}
        if '/commits/' in path:
            return {'sha': SHA}
        raise AssertionError('Unexpected GitHub endpoint: ' + path)

    def event(self):
        return {'action': 'created', 'repository': {'full_name': REPOSITORY, 'default_branch': 'main'},
                'issue': copy.deepcopy(self.issue), 'comment': copy.deepcopy(self.comment)}

    def admit(self, event=None):
        selector = manual.PublishedImages(REPOSITORY, run=lambda arguments: json.dumps(self(arguments[2])).encode())
        return commands.admit(event or self.event(), REPOSITORY, self, selector)

    def mutations(self):
        return [call for call in self.calls if call[1] in ('POST', 'PATCH') and call[0] != 'graphql']

    def selection(self):
        dispatches = [body for path, method, body in self.calls if path.endswith('/dispatches')]
        assert len(dispatches) == 1
        return json.loads(dispatches[0]['inputs']['selection'])

    def feedback(self):
        return '\n'.join(body['body'] for path, method, body in self.calls if (path.endswith('/comments') and method == 'POST') or (path.endswith('/issues/comments/701') and method == 'PATCH'))


class AdmissionTests(unittest.TestCase):
    def test_unique_native_link_freezes_head_and_dispatches_only_main_without_executing_source(self):
        github = GitHub()
        self.assertTrue(github.admit())
        selected = github.selection()
        self.assertEqual(SHA, selected['revision'])
        self.assertEqual('issue-17', selected['environment_id'])
        self.assertEqual(('17', '700'), (selected['tracking_issue'], selected['tracking_comment']))
        self.assertEqual(('sqlite', 'published', 'preserve'), (selected['database'], selected['worker_revision'], selected['data_mode']))
        dispatch = next(body for path, _, body in github.calls if path.endswith('/dispatches'))
        self.assertEqual('main', dispatch['ref'])
        query = next(body for path, _, body in github.calls if path == 'graphql')
        self.assertIn('closedByPullRequestsReferences', query['query'])
        self.assertIn('includeClosedPrs:false', query['query'])
        self.assertIn('first:50', query['query'])
        self.assertIn('/commit/' + SHA, github.feedback())

    def test_no_link_and_multiple_links_explain_selection_without_dispatch(self):
        for nodes, expected in (([], 'No linked open PR'), ([GitHub.pull(12), GitHub.pull(42)], '#12, #42')):
            with self.subTest(expected=expected):
                github = GitHub()
                github.nodes = nodes
                self.assertFalse(github.admit())
                self.assertFalse(any(path.endswith('/dispatches') for path, _, _ in github.calls))
                self.assertIn(expected, github.feedback())
                self.assertIn('ref=', github.feedback())

    def test_explicit_branch_tag_sha_or_pr_bypasses_ambiguous_discovery_and_uses_postgresql(self):
        for ref in ('main', 'release/next', 'v0.3.0-alpha.1', SHA, 'pr:123'):
            with self.subTest(ref=ref):
                github = GitHub('/preview deploy ref=' + ref + ' database=postgresql')
                github.nodes = []
                self.assertTrue(github.admit())
                self.assertEqual((SHA, 'postgresql'), (github.selection()['revision'], github.selection()['database']))
                self.assertNotIn('graphql', [path for path, _, _ in github.calls])

    def test_fork_and_unknown_or_closed_link_context_cannot_deploy(self):
        for change in ('auto-fork', 'explicit-fork', 'closed-link', 'foreign-base', 'overflow', 'graphql-error'):
            with self.subTest(change=change):
                github = GitHub('/preview deploy ref=pr:123' if change == 'explicit-fork' else '/preview deploy')
                if change == 'auto-fork':
                    github.nodes[0]['headRepository'] = {'nameWithOwner': 'foreign/fork'}
                elif change == 'explicit-fork':
                    github.fork = True
                elif change == 'closed-link':
                    github.nodes[0]['state'] = 'CLOSED'
                elif change == 'foreign-base':
                    github.nodes[0]['repository']['nameWithOwner'] = 'foreign/base'
                elif change == 'overflow':
                    github.more = True
                else:
                    github.graphql_errors = [{'message': 'private-provider-body'}]
                self.assertFalse(github.admit())
                self.assertFalse(any(path.endswith('/dispatches') for path, _, _ in github.calls))
                self.assertNotIn('private-provider-body', github.feedback())

    def test_live_write_permission_is_required_even_for_owner_association(self):
        for permission in ('read', 'triage', 'none'):
            with self.subTest(permission=permission):
                github = GitHub()
                github.permission = {'permission': permission}
                event = github.event()
                event['comment']['author_association'] = 'OWNER'
                self.assertFalse(github.admit(event))
                self.assertEqual([], github.mutations())
                self.assertNotIn('graphql', [path for path, _, _ in github.calls])
        for permission in ({'permission': 'admin'}, {'permission': 'maintain'}, {'permission': 'custom', 'user': {'permissions': {'push': True}}}):
            github = GitHub()
            github.permission = permission
            self.assertTrue(github.admit())

    def test_bot_and_non_command_events_are_ignored(self):
        for change in ('bot', 'ordinary', 'edited-event'):
            github = GitHub()
            if change == 'bot':
                github.comment['user']['type'] = 'Bot'
            elif change == 'ordinary':
                github.comment['body'] = 'Looks good'
            event = github.event()
            if change == 'edited-event':
                event['action'] = 'edited'
            self.assertFalse(github.admit(event))
            self.assertEqual([], github.mutations())

    def test_stale_edited_foreign_issue_or_repository_context_never_dispatches(self):
        for change in ('edit', 'body-change', 'user-change', 'issue', 'repository', 'default-branch'):
            with self.subTest(change=change):
                github = GitHub()
                event = github.event()
                if change == 'edit':
                    github.comment['updated_at'] = '2026-11-26T09:01:00Z'
                elif change == 'body-change':
                    event['comment']['body'] = '/preview destroy'
                elif change == 'user-change':
                    event['comment']['user']['id'] = 99
                elif change == 'issue':
                    github.comment['issue_url'] = 'https://api.github.com/' + PREFIX + '/issues/99'
                elif change == 'repository':
                    event['repository']['full_name'] = 'foreign/repository'
                else:
                    event['repository']['default_branch'] = 'develop'
                try:
                    self.assertFalse(github.admit(event))
                except ValueError:
                    pass
                self.assertFalse(any(path.endswith('/dispatches') for path, _, _ in github.calls))

    def test_closed_issue_refuses_deploy_but_allows_recorded_status_and_full_reset(self):
        github = GitHub()
        github.issue['state'] = 'closed'
        self.assertFalse(github.admit())
        for operation in ('status', 'destroy'):
            github = GitHub('/preview ' + operation)
            github.issue['state'] = 'closed'
            self.assertTrue(github.admit())
            selected = github.selection()
            self.assertEqual(operation, selected['operation'])
            self.assertEqual('reset' if operation == 'destroy' else 'preserve', selected['data_mode'])
            self.assertEqual('', selected['revision'])
            self.assertFalse(any('/pulls/' in path or '/commits/' in path for path, _, _ in github.calls))

    def test_shell_markup_duplicate_and_unsupported_arguments_fail_without_dispatch(self):
        for text in ('/preview deploy ref=$(whoami)', '/preview deploy ref=`id`', '/preview deploy ref=main;id',
                     '/preview deploy\nref=main', '/preview deploy ref=main ref=next', '/preview deploy ref=pr:0',
                     '/preview deploy provider=foreign', '/preview deploy database=other', '/preview status ref=main',
                     '/preview destroy data_mode=preserve', '/preview deploy ' + 'x' * 512):
            with self.subTest(command=text[:60]):
                github = GitHub(text)
                self.assertFalse(github.admit())
                self.assertFalse(any(path.endswith('/dispatches') for path, _, _ in github.calls))


class TrackingAndFeedbackTests(unittest.TestCase):
    def test_tracking_rechecks_live_permission_and_intent_without_resolving_moving_refs(self):
        github = GitHub()
        github.admit()
        selected = github.selection()
        github.calls = []
        self.assertEqual('17', commands.tracking(selected, REPOSITORY, github))
        self.assertFalse(any('/pulls/' in path or '/commits/' in path or path == 'graphql' for path, _, _ in github.calls))
        github.permission = {'permission': 'read'}
        with self.assertRaisesRegex(ValueError, 'no longer'):
            commands.tracking(selected, REPOSITORY, github)

    def test_tracking_rejects_cross_environment_mutated_command_and_unfrozen_or_unknown_fields(self):
        for key, value in (('environment_id', 'issue-18'), ('operation', 'destroy'), ('database', 'postgresql'),
                           ('revision', 'main'), ('ttl_hours', 168), ('untrusted', 'private-value')):
            github = GitHub()
            github.admit()
            selection = github.selection()
            selection[key] = value
            with self.subTest(key=key), self.assertRaises(ValueError):
                commands.tracking(selection, REPOSITORY, github)
        github = GitHub()
        github.admit()
        selected = github.selection()
        github.comment['updated_at'] = '2026-11-26T10:00:00Z'
        with self.assertRaises(ValueError):
            commands.tracking(selected, REPOSITORY, github)

    def test_original_ten_input_manual_form_remains_valid_without_any_github_call(self):
        selection = {**commands.parse('/preview deploy ref=main'), 'environment_id': 'manual-example'}
        self.assertEqual('', commands.tracking(selection, REPOSITORY, lambda *args: self.fail('Unexpected API call')))

    def run_mode(self, mode, variables, github):
        with patch.dict(os.environ, {'GITHUB_REPOSITORY': REPOSITORY, 'GITHUB_REF': 'refs/heads/main',
                        'GITHUB_RUN_ID': '100', **variables}, clear=True), patch('sys.argv', ['issue_commands.py', mode]), \
                patch.object(commands, 'api', github), redirect_stdout(io.StringIO()) as output:
            status = commands.main()
        return status, output.getvalue()

    def test_tracking_cli_outputs_normalized_options_and_issue_without_cloud_secrets(self):
        github = GitHub()
        github.admit()
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / 'output'
            status, _ = self.run_mode('tracking', {'SELECTION_JSON': json.dumps(github.selection()), 'GITHUB_OUTPUT': str(output)}, github)
            self.assertEqual(0, status)
            values = dict(line.split('=', 1) for line in output.read_text().splitlines())
            self.assertEqual(('17', 'deploy'), (values['issue-number'], values['operation']))
            self.assertEqual(SHA, json.loads(values['selection'])['revision'])

    def test_plan_announces_exact_request_before_execution_and_failure_never_exports_outputs(self):
        github = GitHub()
        github.admit()
        selected = github.selection()
        github.calls = []
        request = {**azure_request(), 'environmentId': 'issue-17'}
        status, _ = self.run_mode('plan', {'TRACKING_ISSUE': '17', 'OPERATION': 'deploy', 'SELECTION_JSON': json.dumps(selected), 'REQUEST_JSON': json.dumps(request)}, github)
        self.assertEqual(0, status)
        self.assertIn(request['sourceSha'], github.feedback())
        self.assertIn(request['deliveries']['server']['digest'], github.feedback())
        self.assertIn(request['deliveries']['worker']['digest'], github.feedback())
        self.assertIn('L’arrêt automatique n’est pas encore actif', github.feedback())
        github.calls = []
        status, _ = self.run_mode('report', {'TRACKING_ISSUE': '17', 'OPERATION': 'deploy', 'EXECUTION_RESULT': 'failure',
                                          'RESULT_JSON': 'private-provider-body'}, github)
        self.assertEqual(0, status)
        self.assertIn('aucun lien d’accès non vérifié', github.feedback())
        self.assertNotIn('private-provider-body', github.feedback())

    def test_revocation_or_request_substitution_stops_the_pre_execution_announcement(self):
        for change in ('revoked', 'sha', 'database'):
            github = GitHub()
            github.admit()
            selected = github.selection()
            github.calls = []
            request = {**azure_request(), 'environmentId': 'issue-17'}
            if change == 'revoked':
                github.permission = {'permission': 'read'}
            elif change == 'sha':
                request['sourceSha'] = 'c' * 40
            else:
                selected['database'] = 'postgresql'
            status, _ = self.run_mode('plan', {'TRACKING_ISSUE': '17', 'OPERATION': 'deploy',
                    'SELECTION_JSON': json.dumps(selected), 'REQUEST_JSON': json.dumps(request)}, github)
            self.assertEqual(1, status)
            self.assertEqual([], github.mutations())

    def test_only_successful_running_protected_url_is_published(self):
        for state, url, valid in (('running', 'https://dev-issue-17.agentstration.cloud', True),
                                  ('running', 'https://evil.example', False),
                                  ('stopped', 'https://dev-issue-17.agentstration.cloud', False),
                                  ('destroyed', None, True)):
            with self.subTest(state=state, url=url):
                github = GitHub()
                result = {'environmentId': 'issue-17', 'state': state, 'url': url}
                status, _ = self.run_mode('report', {'TRACKING_ISSUE': '17', 'OPERATION': 'destroy' if state == 'destroyed' else 'deploy',
                                          'EXECUTION_RESULT': 'success', 'RESULT_JSON': json.dumps(result)}, github)
                self.assertEqual(0 if valid else 1, status)
                self.assertEqual(valid, bool(github.feedback()))
                if not valid:
                    self.assertNotIn('evil.example', github.feedback())

    def test_reset_cannot_claim_success_with_remaining_infrastructure(self):
        github = GitHub()
        result = {'environmentId': 'issue-17', 'state': 'destroyed', 'url': None,
                  'providerResourceIds': {'resources': ['owned-disk']}}
        status, _ = self.run_mode('report', {'TRACKING_ISSUE': '17', 'OPERATION': 'destroy',
                'EXECUTION_RESULT': 'success', 'RESULT_JSON': json.dumps(result)}, github)
        self.assertEqual(1, status)
        self.assertEqual([], github.mutations())

    def test_api_serializes_multiline_json_without_shell_interpolation(self):
        def run(arguments):
            self.assertEqual(['gh', 'api', PREFIX + '/issues/17/comments', '--method', 'POST', '--input'], arguments[:-1])
            self.assertEqual({'body': 'line1\n`literal` $(literal)'}, json.loads(Path(arguments[-1]).read_text()))
            return b'{"id":701}'
        with patch.object(manual, 'command', side_effect=run):
            self.assertEqual({'id': 701}, commands.api(PREFIX + '/issues/17/comments', 'POST', {'body': 'line1\n`literal` $(literal)'}))


class PullRequestTests(unittest.TestCase):
    run_mode = TrackingAndFeedbackTests.run_mode

    def github(self, operation='deploy'):
        github = GitHub('/preview ' + operation)
        github.issue['pull_request'] = {'url': 'https://api.github.com/' + PREFIX + '/pulls/17'}
        return github

    def test_pr_uses_own_frozen_head_labels_and_environment_then_preserves_it_when_head_moves(self):
        github = self.github()
        github.issue['labels'] = [{'name': 'preview:extension:foundry'}]
        self.assertTrue(github.admit())
        selected = github.selection()
        self.assertEqual(('pr-17', 'pull_request', SHA, ['foundry']),
                         (selected['environment_id'], selected['tracking_kind'], selected['revision'], selected['extensions']))
        self.assertFalse(any(path == 'graphql' or '/commits/' in path or '/pulls/123' in path for path, _, _ in github.calls))
        github.head_sha = 'b' * 40
        self.assertEqual('17', commands.tracking(selected, REPOSITORY, github))
        self.assertEqual(SHA, selected['revision'])
        github.issue['labels'] = []
        with self.assertRaisesRegex(ValueError, 'labels changed'):
            commands.tracking(selected, REPOSITORY, github)

    def test_pr_rejects_forks_closed_deploy_and_other_source_overrides(self):
        for change in ('fork', 'closed', 'branch', 'other-pr', 'event-kind'):
            with self.subTest(change=change):
                github = self.github()
                if change == 'fork':
                    github.fork = True
                elif change == 'closed':
                    github.issue['state'] = 'closed'
                elif change in ('branch', 'other-pr'):
                    github.comment['body'] += ' ref=' + ('main' if change == 'branch' else 'pr:123')
                event = github.event()
                if change == 'event-kind':
                    del event['issue']['pull_request']
                self.assertFalse(github.admit(event))
                self.assertFalse(any(path.endswith('/dispatches') for path, _, _ in github.calls))
        github = self.github()
        github.comment['body'] += ' ref=pr:17'
        self.assertTrue(github.admit())

    def test_closed_pr_cleanup_does_not_need_head_repository_catalogue_or_registry(self):
        for operation in ('status', 'destroy'):
            github = self.github(operation)
            github.issue.update(state='closed', labels=[{'name': 'preview:extension:no-longer-supported'}])
            github.fork = True  # Even a deleted head repository is irrelevant to cleanup.
            self.assertTrue(github.admit())
            self.assertEqual('17', commands.tracking(github.selection(), REPOSITORY, github))
            self.assertEqual('pr-17', github.selection()['environment_id'])
            self.assertFalse(any('/pulls/' in path or '/commits/' in path or path == 'graphql' for path, _, _ in github.calls))

    def test_each_command_updates_only_its_bot_owned_card_at_every_stage(self):
        github = self.github()
        self.assertTrue(github.admit())
        selected = github.selection()
        request = {**azure_request(), 'environmentId': 'pr-17'}
        variables = {'TRACKING_ISSUE': '17', 'OPERATION': 'deploy', 'SELECTION_JSON': json.dumps(selected),
                     'REQUEST_JSON': json.dumps(request)}
        self.assertEqual(0, self.run_mode('plan', variables, github)[0])
        import contract
        result = {'environmentId': 'pr-17', 'state': 'running', 'url': 'https://dev-pr-17.agentstration.cloud',
                  'sourceSha': SHA, 'database': 'sqlite', 'images': contract.images(request), 'expiresAt': '2026-11-26T18:30:00+00:00'}
        self.assertEqual(0, self.run_mode('report', {**variables, 'EXECUTION_RESULT': 'success', 'RESULT_JSON': json.dumps(result)}, github)[0])
        posts = [path for path, method, _ in github.calls if method == 'POST' and path.endswith('/comments')]
        patches = [path for path, method, _ in github.calls if method == 'PATCH']
        self.assertEqual([PREFIX + '/issues/17/comments'], posts)
        self.assertEqual([PREFIX + '/issues/comments/701'] * 2, patches)
        card = github.card['body']
        for text in ('Prévisualisation prête', 'dev-pr-17.agentstration.cloud', SHA[:12], '**sqlite**',
                     '26/11/2026 à 18:30 UTC', '/preview destroy', '<details>', request['deliveries']['server']['digest']):
            self.assertIn(text, card)
        # A later status/failure summary is allowed even if the PR closes or labels change during execution.
        github.issue.update(state='closed', labels=[])
        self.assertEqual(0, self.run_mode('report', {**variables, 'EXECUTION_RESULT': 'failure', 'RESULT_JSON': 'PRIVATE RESPONSE'}, github)[0])
        self.assertNotIn('PRIVATE RESPONSE', github.card['body'])
        self.assertNotIn('Ouvrir Agentstration', github.card['body'])

    def test_card_ownership_context_and_entire_frozen_selection_are_verified(self):
        for change in ('user', 'other-bot', 'other-issue', 'marker', 'source', 'feedback-id', 'extension'):
            github = self.github()
            github.admit()
            selected = github.selection()
            if change == 'user':
                github.card['user'] = {'login': 'maintainer', 'type': 'User'}
            elif change == 'other-bot':
                github.card['user']['login'] = 'other[bot]'
            elif change == 'other-issue':
                github.card['issue_url'] = 'https://api.github.com/' + PREFIX + '/issues/18'
            elif change == 'marker':
                github.card['body'] = 'unrelated bot comment'
            elif change == 'source':
                selected['revision'] = 'b' * 40
            elif change == 'extension':
                selected['extensions'] = ['foundry']
                github.issue['labels'] = [{'name': 'preview:extension:foundry'}]
            else:
                selected['tracking_feedback'] = '700'  # Original human command must never be patched.
            github.calls = []
            with self.subTest(change=change), self.assertRaises(ValueError):
                commands.tracking(selected, REPOSITORY, github)
            self.assertEqual([], github.mutations())

    def test_failure_reports_update_the_card_even_when_boundary_has_no_outputs(self):
        for stage in ('boundary', 'select', 'recorded', 'announce', 'execute'):
            github = self.github()
            github.admit()
            selected = github.selection()
            variables = {'TRACKING_ISSUE': '17', 'OPERATION': 'deploy', 'SELECTION_JSON': json.dumps(selected),
                         'STAGE_RESULTS': json.dumps({stage: {'result': 'failure', 'outputs': {'ignored': 'PRIVATE'}}}),
                         'EXECUTION_RESULT': 'skipped', 'RESULT_JSON': 'PRIVATE'}
            self.assertEqual(0, self.run_mode('report', variables, github)[0])
            self.assertIn('**À faire :**', github.card['body'])
            self.assertNotIn('PRIVATE', github.card['body'])
            if stage != 'execute':
                self.assertIn('n’a pas été lancée', github.card['body'])
            else:
                self.assertIn('Des ressources peuvent subsister', github.card['body'])

    def test_missing_image_and_package_access_have_specific_safe_remediation(self):
        for code, expected in (('image-unavailable', 'extension=server'), ('registry-access', 'Manage Actions access')):
            github = self.github()
            github.admit()
            status, _ = self.run_mode('report', {'TRACKING_ISSUE': '17', 'OPERATION': 'deploy',
                'SELECTION_JSON': json.dumps(github.selection()), 'EXECUTION_RESULT': 'failure',
                'STAGE_RESULTS': '{"select":"failure"}', 'SELECTION_FAILURE': json.dumps({'code': code, 'component': 'server'}),
                'RESULT_JSON': 'PRIVATE'}, github)
            self.assertEqual(0, status)
            self.assertIn(expected, github.card['body'])
            self.assertNotIn('PRIVATE', github.card['body'])

    def test_dispatch_failure_updates_existing_card_and_does_not_create_second_reply(self):
        github = self.github()
        def call(path, method='GET', body=None):
            if path.endswith('/dispatches'):
                raise manual.Unavailable('Dispatch failed.')
            return github(path, method, body)
        self.assertFalse(commands.admit(github.event(), REPOSITORY, call))
        self.assertEqual(1, sum(path.endswith('/comments') and method == 'POST' for path, method, _ in github.calls))
        self.assertIn('lancement du workflow a échoué', github.card['body'])

    def test_legacy_issue_selection_without_card_or_kind_remains_accepted(self):
        github = GitHub()
        github.admit()
        selected = github.selection()
        del selected['tracking_kind'], selected['tracking_feedback']
        self.assertEqual('17', commands.tracking(selected, REPOSITORY, github))

    def test_removing_the_card_cannot_bypass_pr_source_binding(self):
        github = self.github()
        github.admit()
        selected = github.selection()
        del selected['tracking_feedback']
        selected['revision'] = 'b' * 40
        with self.assertRaisesRegex(ValueError, 'admitted feedback card'):
            commands.tracking(selected, REPOSITORY, github)


if __name__ == '__main__':
    unittest.main()
