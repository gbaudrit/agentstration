#!/usr/bin/env python3
"""Issue commands dispatch trusted main tooling; comment text is never executable code."""
import argparse
import json
import os
from pathlib import Path
import re
import tempfile

import contract
import manual
import preview


def api(path, method='GET', body=None):
    arguments = ['gh', 'api', path, '--method', method]
    with tempfile.TemporaryDirectory(prefix='preview-github-') as directory:
        if body is not None:
            file = Path(directory) / 'body.json'
            preview.private_write(file, json.dumps(body))
            arguments += ['--input', str(file)]
        raw = manual.command(arguments)
    return json.loads(raw) if raw else None


def repository_name(repository):
    preview.require(isinstance(repository, str) and re.fullmatch(r'[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+', repository),
                    'Invalid repository context.')
    return repository.lower()


def positive(value):
    preview.require(type(value) is int and 1 <= value <= 99999999999999999999, 'Invalid GitHub identifier.')
    return value


def parse(body):
    preview.require(isinstance(body, str) and len(body) <= 512, 'Use one bounded /preview command.')
    text = body.strip()
    preview.require(re.fullmatch(r'/preview (deploy|status|destroy)(?: [A-Za-z0-9_./:=+-]+)*', text),
                    'Use /preview deploy [ref=PR_OR_REVISION] [database=sqlite|postgresql], /preview status or /preview destroy.')
    words = text.split()
    values = {}
    for word in words[2:]:
        key, separator, value = word.partition('=')
        preview.require(separator and key in ('ref', 'database') and key not in values and value,
                        'Only one ref and one database argument are supported.')
        values[key] = value
    operation = words[1]
    preview.require(operation == 'deploy' or not values, 'Status/destroy use the recorded issue environment, without arguments.')
    ref = values.get('ref', '')
    if ref:
        preview.require(re.fullmatch(r'[A-Za-z0-9][A-Za-z0-9._/:-]{0,199}', ref)
                        and (not ref.startswith('pr:') or re.fullmatch(r'pr:[1-9][0-9]{0,9}', ref)),
                        'Use ref=pr:NUMBER, a branch, tag or full SHA.')
    database = values.get('database', 'sqlite')
    preview.require(database in ('sqlite', 'postgresql'), 'Database must be sqlite or postgresql.')
    return {'operation': operation, 'revision': ref, 'database': database, 'provider': 'azure-vm',
            'vm_size_profile': '1', 'ttl_hours': 2, 'data_mode': 'reset' if operation == 'destroy' else 'preserve',
            'test_profile': 'readiness', 'worker_revision': 'published'}


def authorized(comment, repository, call):
    user = comment.get('user', {})
    login = user.get('login', '')
    if user.get('type') != 'User' or not isinstance(login, str) or not re.fullmatch(r'[A-Za-z0-9][A-Za-z0-9-]{0,38}', login):
        return False
    permission = call(f'repos/{repository}/collaborators/{login}/permission')
    return permission.get('permission') in ('write', 'maintain', 'admin') or permission.get('user', {}).get('permissions', {}).get('push') is True


def comment_context(repository, issue, comment_id, call):
    comment = call(f'repos/{repository}/issues/comments/{positive(comment_id)}')
    preview.require(comment.get('issue_url', '').lower() == f'https://api.github.com/repos/{repository}/issues/{positive(issue)}',
                    'The command belongs to another issue.')
    preview.require(comment.get('id') == comment_id and comment.get('created_at')
                    and comment.get('created_at') == comment.get('updated_at'),
                    'Edited commands are refused; post a new comment.')
    return comment


def linked_ref(repository, issue, call):
    owner, name = repository.split('/')
    query = '''query($owner:String!,$name:String!,$number:Int!) {
      repository(owner:$owner,name:$name) { issue(number:$number) {
        closedByPullRequestsReferences(first:50,includeClosedPrs:false) {
          nodes { number state repository { nameWithOwner } headRepository { nameWithOwner } }
          pageInfo { hasNextPage }
        }
      } }
    }'''
    value = call('graphql', 'POST', {'query': query, 'variables': {'owner': owner, 'name': name, 'number': issue}})
    preview.require(not value.get('errors'), 'Linked PR lookup failed; use an explicit ref.')
    connection = value['data']['repository']['issue']['closedByPullRequestsReferences']
    nodes = connection['nodes']
    preview.require(isinstance(nodes, list) and len(nodes) <= 50 and connection['pageInfo']['hasNextPage'] is False,
                    'Linked PR inventory exceeds the limit; use an explicit ref.')
    pulls = {}
    for pull in nodes:
        preview.require(pull.get('state') == 'OPEN' and pull.get('repository', {}).get('nameWithOwner', '').lower() == repository,
                        'Unexpected linked PR context; use an explicit ref.')
        pulls[positive(pull['number'])] = pull
    preview.require(pulls, 'No linked open PR. Use /preview deploy ref=pr:NUMBER or ref=BRANCH_TAG_OR_SHA.')
    if len(pulls) != 1:
        numbers = ', '.join('#' + str(number) for number in sorted(pulls))
        raise ValueError('Multiple linked open PRs: ' + numbers + '. Choose one with /preview deploy ref=pr:NUMBER.')
    number, pull = next(iter(pulls.items()))
    preview.require((pull.get('headRepository') or {}).get('nameWithOwner', '').lower() == repository,
                    'Fork PR previews are not enabled; use a same-repository revision.')
    return 'pr:' + str(number)


def reply(repository, issue, body, call):
    preview.require(len(body) <= 12000, 'Preview feedback exceeds its limit.')
    return call(f'repos/{repository}/issues/{positive(issue)}/comments', 'POST', {'body': body})


def safe_error(error):
    return str(error) if type(error) is ValueError or isinstance(error, manual.Unavailable) else 'Preview request failed; inspect the trusted workflow run. Remote content is withheld.'


def admit(event, repository, call=api, selector=None):
    repository = repository_name(repository)
    if event.get('action') != 'created' or 'pull_request' in event.get('issue', {}):
        return False
    body = event.get('comment', {}).get('body', '')
    if not isinstance(body, str) or not body.strip().startswith('/preview'):
        return False
    preview.require(event.get('repository', {}).get('full_name', '').lower() == repository
                    and event['repository'].get('default_branch') == 'main', 'Issue commands require the repository main context.')
    issue = positive(event['issue']['number'])
    comment = comment_context(repository, issue, event['comment']['id'], call)
    # No feedback or dispatch on behalf of untrusted users; association labels are not permissions.
    if not authorized(comment, repository, call):
        return False
    try:
        preview.require(comment.get('body') == body and comment.get('user', {}).get('id') == event['comment'].get('user', {}).get('id'),
                        'The command changed after its event; post a new comment.')
        options = parse(body)
        current = call(f'repos/{repository}/issues/{issue}')
        preview.require(current.get('number') == issue and 'pull_request' not in current, 'Expected an ordinary repository issue.')
        preview.require(options['operation'] != 'deploy' or current.get('state') == 'open', 'Reopen the issue before deploying its preview.')
        source = ''
        if options['operation'] == 'deploy':
            source = options['revision'] or linked_ref(repository, issue, call)
            selector = selector or manual.PublishedImages(repository)
            # Resolve once. Moving refs cannot change the source after admission.
            options['revision'] = selector.revision(source)
        options.update(environment_id='issue-' + str(issue), tracking_issue=str(issue), tracking_comment=str(comment['id']))
        manual.validate_options(options)
        call(f'repos/{repository}/actions/workflows/preview-issue-deployment.yml/dispatches', 'POST',
             {'ref': 'main', 'inputs': {'selection': json.dumps(options)}})
        selected = ('Source `' + options['revision'] + '`' + (' from `' + source + '`' if source.startswith('pr:') else '')
                    + '. Published digests will be announced before infrastructure execution.\n\n') if source else ''
        reply(repository, issue, 'Preview **' + options['operation'] + '** requested for `' + options['environment_id'] + '`.\n\n'
              + selected + '[Follow Actions](https://github.com/' + repository + '/actions/workflows/preview-issue-deployment.yml).'
              + ('\n\nDestroy removes all owned infrastructure and application data.' if options['operation'] == 'destroy' else ''), call)
        return True
    except (ValueError, RuntimeError, KeyError, TypeError, AttributeError) as error:
        reply(repository, issue, safe_error(error), call)
        return False


def tracking(options, repository, call=api):
    repository = repository_name(repository)
    expected = {'operation', 'environment_id', 'revision', 'provider', 'database', 'vm_size_profile',
                'ttl_hours', 'data_mode', 'test_profile', 'worker_revision'}
    preview.require(isinstance(options, dict) and set(options) in (expected, expected | {'tracking_issue', 'tracking_comment'}),
                    'Unexpected form or issue tracking fields.')
    manual.validate_options(options)
    issue, comment_id = options.get('tracking_issue', ''), options.get('tracking_comment', '')
    if issue == '' and comment_id == '':
        return ''
    preview.require(isinstance(issue, str) and re.fullmatch(r'[1-9][0-9]{0,9}', issue)
                    and isinstance(comment_id, str) and re.fullmatch(r'[1-9][0-9]{0,19}', comment_id), 'Invalid issue tracking identifiers.')
    preview.require(options['environment_id'] == 'issue-' + issue, 'Issue tracking cannot target another environment.')
    comment = comment_context(repository, int(issue), int(comment_id), call)
    preview.require(authorized(comment, repository, call), 'The command author no longer has repository write access.')
    original = parse(comment.get('body'))
    preview.require(all(options[key] == original[key] for key in ('operation', 'database', 'provider', 'vm_size_profile',
                        'data_mode', 'test_profile', 'worker_revision')) and str(options['ttl_hours']) == str(original['ttl_hours']),
                    'The dispatched options differ from the issue command.')
    preview.require(options['operation'] != 'deploy' or preview.SHA.fullmatch(options['revision']), 'Issue deployment source must be frozen to a full SHA.')
    current = call(f'repos/{repository}/issues/{issue}')
    preview.require('pull_request' not in current and current.get('number') == int(issue)
                    and (options['operation'] != 'deploy' or current.get('state') == 'open'), 'The issue is no longer eligible for deployment.')
    return issue


def plan_message(request, operation, run_url):
    request = contract.loads(json.dumps(request))
    contract.operation(operation, request)
    lines = ['Preview **' + operation + '** selection for `' + request['environmentId'] + '`.',
             'Source: `' + request['sourceSha'] + '`; database: `' + request['database'] + '`.',
             *[name + ': `' + image['reference'] + '`' for name, image in contract.images(request).items()],
             '[Workflow run](' + run_url + ').']
    if operation == 'destroy':
        lines.append('Full reset: all owned infrastructure and application data will be removed.')
    elif operation == 'deploy':
        lines.append('TTL is recorded; automatic expiry cleanup is not implemented yet. Use `/preview destroy` after testing.')
    return '\n\n'.join(lines)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('mode', choices=('admit', 'tracking', 'plan', 'report'))
    args = parser.parse_args()
    try:
        repository = repository_name(os.environ['GITHUB_REPOSITORY'])
        preview.require(os.environ['GITHUB_REF'] == 'refs/heads/main', 'Issue previews require trusted main tooling.')
        if args.mode == 'admit':
            preview.require(os.environ['GITHUB_EVENT_NAME'] == 'issue_comment', 'Expected an issue-comment event.')
            with open(os.environ['GITHUB_EVENT_PATH']) as file:
                payload = file.read(1024 * 1024 + 1)
            preview.require(len(payload) <= 1024 * 1024, 'Issue event exceeds its limit.')
            admit(json.loads(payload), repository, api)
        elif args.mode == 'tracking':
            options = contract.parse(os.environ['SELECTION_JSON'])
            issue = tracking(options, repository, api)
            with open(os.environ['GITHUB_OUTPUT'], 'a') as output:
                output.write('issue-number=' + issue + '\n')
                output.write('selection=' + json.dumps(options) + '\n')
                output.write('operation=' + options['operation'] + '\n')
        else:
            issue = os.environ['TRACKING_ISSUE']
            preview.require(re.fullmatch(r'[1-9][0-9]{0,9}', issue), 'Invalid feedback issue.')
            run_id = positive(int(os.environ['GITHUB_RUN_ID']))
            run_url = 'https://github.com/' + repository + '/actions/runs/' + str(run_id)
            operation = os.environ['OPERATION']
            preview.require(operation in ('validate', 'deploy', 'status', 'destroy'), 'Invalid feedback operation.')
            if args.mode == 'plan':
                options = contract.parse(os.environ['SELECTION_JSON'])
                preview.require(tracking(options, repository, api) == issue and options['operation'] == operation,
                                'Invalid command authorization before execution.')
                request = contract.loads(os.environ['REQUEST_JSON'])
                preview.require(request['environmentId'] == 'issue-' + issue, 'Feedback targets another environment.')
                preview.require(request['dataMode'] == options['data_mode']
                                and (operation != 'deploy' or request['sourceSha'] == options['revision']
                                     and request['database'] == options['database']), 'Frozen request differs from the issue command.')
                message = plan_message(request, operation, run_url)
            elif os.environ['EXECUTION_RESULT'] == 'success':
                result = contract.parse(os.environ['RESULT_JSON'])
                preview.require(result['environmentId'] == 'issue-' + issue, 'Result targets another environment.')
                state = result['state']
                preview.require(state in ('running', 'destroyed', 'stopped', 'absent', 'unhealthy', 'provisioning', 'drifted'), 'Invalid preview result state.')
                url = result.get('url')
                preview.require(url is None or isinstance(url, str) and re.fullmatch(r'https://dev-issue-' + issue + r'\.[a-z0-9.-]+', url), 'Invalid protected preview URL.')
                preview.require(not url or state == 'running', 'An inactive preview cannot publish a URL.')
                if operation == 'destroy':
                    preview.require(state in ('destroyed', 'absent') and not url
                                    and not result.get('providerResourceIds', {}).get('resources'), 'Reset has not confirmed removal of owned resources.')
                elif operation == 'deploy':
                    preview.require(state == 'running' and url, 'Deployment has no verified protected URL.')
                message = 'Preview `' + result['environmentId'] + '`: **' + state + '**.\n\n'
                if url:
                    message += '[Open protected preview](' + url + ').\n\n'
                if operation == 'destroy':
                    message += 'Full reset completed: owned resources and application data removed.\n\n'
                message += '[Workflow run](' + run_url + ').'
            else:
                message = 'Preview **' + operation + '** did not complete successfully. No verified preview URL is reported. [Inspect the trusted workflow run](' + run_url + ').'
            reply(repository, int(issue), message, api)
    except (ValueError, RuntimeError, KeyError, TypeError, AttributeError, OSError) as error:
        print(safe_error(error))
        return 1
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
