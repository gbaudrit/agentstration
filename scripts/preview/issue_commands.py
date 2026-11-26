#!/usr/bin/env python3
"""Issue and PR commands dispatch trusted main tooling; comment text is never executable code."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import tempfile

import contract
import extensions
import manual
import preview
import preview_feedback as feedback


def api(path, method='GET', body=None):
    arguments = ['gh', 'api', path, '--method', method]
    with tempfile.TemporaryDirectory(prefix='preview-github-') as directory:
        if body is not None:
            file = Path(directory) / 'body.json'
            preview.private_write(file, json.dumps(body))
            arguments += ['--input', str(file)]
        try:
            raw = manual.command(arguments)
        except manual.Unavailable:
            # Labels and provider/API errors are untrusted. Only name fixed API stages.
            stage = 'GitHub metadata lookup'
            permission = 'repository metadata/read access'
            if method == 'POST' and path.endswith('/comments') or method == 'PATCH' and '/issues/comments/' in path:
                stage, permission = 'Preview comment publication', 'issues:write for issues and pull-requests:write for PRs'
            elif path.endswith('/permission'):
                stage = 'Requester permission verification'
            elif '/issues/comments/' in path:
                stage = 'Command or feedback comment lookup'
            elif path.endswith('/dispatches'):
                stage, permission = 'Trusted-main workflow dispatch', 'actions:write'
            raise manual.Unavailable(stage + ' failed; check ' + permission + '. Remote response is withheld.') from None
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


def marker(options):
    # Bind the bot-owned card to the complete admitted selection, including its frozen SHA.
    selection = {key: value for key, value in options.items() if key != 'tracking_feedback'}
    digest = hashlib.sha256(json.dumps(selection, sort_keys=True).encode()).hexdigest()
    return '<!-- agentstration-preview-command:' + digest + ' -->'


def managed_comment(options, repository, call):
    identifier = options.get('tracking_feedback')
    preview.require(isinstance(identifier, str) and re.fullmatch(r'[1-9][0-9]{0,19}', identifier), 'Invalid feedback identifier.')
    comment = call(f'repos/{repository}/issues/comments/{identifier}')
    preview.require(comment.get('id') == int(identifier)
                    and comment.get('issue_url', '').lower() == f'https://api.github.com/repos/{repository}/issues/{options["tracking_issue"]}'
                    and comment.get('user', {}).get('type') == 'Bot'
                    and comment['user'].get('login') == 'github-actions[bot]'
                    and comment.get('body', '').startswith(marker(options) + '\n'),
                    'Feedback must be the bot-owned comment for this admitted command.')
    return identifier


def publish(options, repository, body, call):
    preview.require(len(body) <= 11500, 'Preview feedback exceeds its limit.')
    if 'tracking_feedback' not in options:
        return reply(repository, int(options['tracking_issue']), body, call)  # Legacy in-flight commands.
    identifier = managed_comment(options, repository, call)
    return call(f'repos/{repository}/issues/comments/{identifier}', 'PATCH', {'body': marker(options) + '\n' + body})


def context(options, repository, issue, current, call):
    kind = options.get('tracking_kind', 'issue')
    preview.require(kind in ('issue', 'pull_request') and current.get('number') == issue
                    and ('pull_request' in current) == (kind == 'pull_request'), 'Unexpected issue/PR context.')
    preview.require(options['operation'] != 'deploy' or current.get('state') == 'open', 'Reopen the issue or PR before deploying its preview.')
    if kind == 'pull_request' and options['operation'] == 'deploy':
        pull = call(f'repos/{repository}/pulls/{issue}')
        preview.require(pull.get('number') == issue and pull.get('state') == 'open'
                        and (pull.get('base', {}).get('repo') or {}).get('full_name', '').lower() == repository
                        and (pull.get('head', {}).get('repo') or {}).get('full_name', '').lower() == repository,
                        'Only open same-repository PRs may deploy; fork PR previews are not enabled.')
        return pull
    return None


def safe_error(error):
    return str(error) if type(error) is ValueError or isinstance(error, manual.Unavailable) else 'Preview request failed; inspect the trusted workflow run. Remote content is withheld.'


def admit(event, repository, call=api, selector=None):
    repository = repository_name(repository)
    if event.get('action') != 'created':
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
    options = None
    try:
        preview.require(comment.get('body') == body and comment.get('user', {}).get('id') == event['comment'].get('user', {}).get('id'),
                        'The command changed after its event; post a new comment.')
        options = parse(body)
        current = call(f'repos/{repository}/issues/{issue}')
        options['tracking_kind'] = 'pull_request' if 'pull_request' in event['issue'] else 'issue'
        pull = context(options, repository, issue, current, call)
        if options['operation'] == 'deploy':
            options['extensions'] = extensions.from_labels(current.get('labels', []))
            if pull:
                preview.require(options['revision'] in ('', 'pr:' + str(issue)),
                                'On a PR, use /preview deploy without ref; it deploys that PR. Use an issue or the manual form for another revision.')
                # Use the same live PR response for ownership and SHA; no moving-ref second lookup.
                options['revision'] = pull['head']['sha']
                preview.require(isinstance(options['revision'], str) and preview.SHA.fullmatch(options['revision']), 'Invalid PR head SHA.')
            else:
                source = options['revision'] or linked_ref(repository, issue, call)
                selector = selector or manual.PublishedImages(repository)
                options['revision'] = selector.revision(source)
        prefix = 'pr-' if options['tracking_kind'] == 'pull_request' else 'issue-'
        options.update(environment_id=prefix + str(issue), tracking_issue=str(issue), tracking_comment=str(comment['id']))
        manual.validate_options(options)
        run_url = 'https://github.com/' + repository + '/actions/workflows/preview-issue-deployment.yml'
        card = reply(repository, issue, marker(options) + '\n' + feedback.accepted(options, repository, run_url), call)
        options['tracking_feedback'] = str(positive(card['id']))
        call(f'repos/{repository}/actions/workflows/preview-issue-deployment.yml/dispatches', 'POST',
             {'ref': 'main', 'inputs': {'selection': json.dumps(options)}})
        return True
    except (ValueError, RuntimeError, KeyError, TypeError, AttributeError) as error:
        message = '**Command not executed**\n\n' + safe_error(error)
        if options and options.get('tracking_feedback'):
            publish(options, repository, message + '\n\nWorkflow dispatch failed. Check Actions permissions and post a new command.', call)
        else:
            reply(repository, issue, message, call)
        return False


def tracking(options, repository, call=api):
    repository = repository_name(repository)
    expected = {'operation', 'environment_id', 'revision', 'provider', 'database', 'vm_size_profile',
                'ttl_hours', 'data_mode', 'test_profile', 'worker_revision'}
    optional = {'extensions', 'tracking_issue', 'tracking_comment', 'tracking_kind', 'tracking_feedback'}
    preview.require(isinstance(options, dict) and expected <= set(options) <= expected | optional,
                    'Unexpected form or issue tracking fields.')
    tracked = {'tracking_issue', 'tracking_comment'} & set(options)
    preview.require(not tracked or tracked == {'tracking_issue', 'tracking_comment'}, 'Incomplete command tracking.')
    preview.require(not ({'tracking_kind', 'tracking_feedback'} & set(options)) or tracked, 'Feedback requires command tracking.')
    preview.require(('tracking_kind' in options) == ('tracking_feedback' in options),
                    'New command contexts require their admitted feedback card; legacy issue contexts omit both fields.')
    manual.validate_options(options)
    issue, comment_id = options.get('tracking_issue', ''), options.get('tracking_comment', '')
    if issue == '' and comment_id == '':
        return ''
    preview.require(isinstance(issue, str) and re.fullmatch(r'[1-9][0-9]{0,9}', issue)
                    and isinstance(comment_id, str) and re.fullmatch(r'[1-9][0-9]{0,19}', comment_id), 'Invalid issue tracking identifiers.')
    kind = options.get('tracking_kind', 'issue')
    prefix = 'pr-' if kind == 'pull_request' else 'issue-'
    preview.require(options['environment_id'] == prefix + issue, 'Issue tracking cannot target another environment.')
    comment = comment_context(repository, int(issue), int(comment_id), call)
    preview.require(authorized(comment, repository, call), 'The command author no longer has repository write access.')
    original = parse(comment.get('body'))
    preview.require(all(options[key] == original[key] for key in ('operation', 'database', 'provider', 'vm_size_profile',
                        'data_mode', 'test_profile', 'worker_revision')) and str(options['ttl_hours']) == str(original['ttl_hours']),
                    'The dispatched options differ from the issue command.')
    preview.require(options['operation'] != 'deploy' or preview.SHA.fullmatch(options['revision']), 'Issue deployment source must be frozen to a full SHA.')
    current = call(f'repos/{repository}/issues/{issue}')
    context(options, repository, int(issue), current, call)
    if kind == 'pull_request':
        preview.require(original['revision'] in ('', 'pr:' + issue), 'PR command cannot select another source.')
    if 'tracking_feedback' in options:
        managed_comment(options, repository, call)
    if options["operation"] == "deploy":
        preview.require(options.get("extensions", []) == extensions.from_labels(current.get("labels", [])),
                        "Extension labels changed after admission; post a new /preview deploy command.")
    return issue


def plan_message(request, operation, run_url, repository='gbaudrit/agentstration'):
    request = contract.loads(json.dumps(request))
    contract.operation(operation, request)
    return feedback.planned(request, operation, repository, run_url)


def failure_stage():
    stages = contract.parse(os.environ.get('STAGE_RESULTS', '{}'))
    if isinstance(stages, dict) and all(isinstance(value, dict) for value in stages.values()):
        stages = {name: value.get('result') for name, value in stages.items()}
    preview.require(isinstance(stages, dict) and set(stages) <= {'boundary', 'select', 'recorded', 'announce', 'execute'}
                    and all(value in ('success', 'failure', 'cancelled', 'skipped') for value in stages.values()), 'Invalid workflow stage results.')
    return next((name for name in ('boundary', 'select', 'recorded', 'announce', 'execute') if stages.get(name) == 'failure'),
                'cancelled' if 'cancelled' in stages.values() else 'execute')


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
            options = contract.parse(os.environ['SELECTION_JSON']) if os.environ.get('SELECTION_JSON') else {'tracking_issue': issue, 'environment_id': 'issue-' + issue, 'operation': operation}
            preview.require(options.get('tracking_issue') == issue and options.get('operation') == operation, 'Invalid feedback command context.')
            environment = options['environment_id']
            preview.require(environment == ('pr-' if options.get('tracking_kind') == 'pull_request' else 'issue-') + issue, 'Invalid feedback environment.')
            request = None
            if args.mode == 'plan':
                preview.require(tracking(options, repository, api) == issue and options['operation'] == operation,
                                'Invalid command authorization before execution.')
                request = contract.loads(os.environ['REQUEST_JSON'])
                preview.require(request['environmentId'] == environment, 'Feedback targets another environment.')
                preview.require(request['dataMode'] == options['data_mode']
                                and (operation != 'deploy' or request['sourceSha'] == options['revision']
                                     and request['database'] == options['database']), 'Frozen request differs from the issue command.')
                if operation == 'deploy':
                    preview.require(list(request.get('extensions', {})) == options.get('extensions', []),
                                    'Frozen extension deliveries differ from the issue labels.')
                message = plan_message(request, operation, run_url, repository)
            elif os.environ['EXECUTION_RESULT'] == 'success':
                result = contract.parse(os.environ['RESULT_JSON'])
                preview.require(result['environmentId'] == environment, 'Result targets another environment.')
                state = result['state']
                preview.require(state in ('running', 'destroyed', 'stopped', 'absent', 'unhealthy', 'provisioning', 'drifted'), 'Invalid preview result state.')
                url = result.get('url')
                preview.require(url is None or isinstance(url, str) and re.fullmatch(r'https://dev-' + re.escape(environment) + r'\.[a-z0-9.-]+', url), 'Invalid protected preview URL.')
                preview.require(not url or state == 'running', 'An inactive preview cannot publish a URL.')
                if operation == 'destroy':
                    preview.require(state in ('destroyed', 'absent') and not url
                                    and not result.get('providerResourceIds', {}).get('resources'), 'Reset has not confirmed removal of owned resources.')
                elif operation == 'deploy':
                    preview.require(state == 'running' and url, 'Deployment has no verified protected URL.')
                if os.environ.get('REQUEST_JSON'):
                    request = contract.loads(os.environ['REQUEST_JSON'])
                    preview.require(request['environmentId'] == environment, 'Result request targets another environment.')
                    # Report only canonical request metadata, not arbitrary provider fields.
                    preview.require(result.get('sourceSha') == request['sourceSha'] and result.get('database') == request['database']
                                    and result.get('images') == contract.images(request), 'Result differs from the frozen request.')
                message = feedback.completed(result, operation, repository, run_url, request)
            else:
                failure = contract.parse(os.environ.get('SELECTION_FAILURE') or '{}')
                message = feedback.failed(operation, repository, run_url, failure_stage(), options, failure)
            publish(options, repository, message, api)
    except (ValueError, RuntimeError, KeyError, TypeError, AttributeError, OSError) as error:
        print(safe_error(error))
        return 1
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
