"""Read-only provenance and component selection from completed PR workflows."""
import re
import preview

IMAGE_JOB = re.compile(r'oci-images \(([a-z][a-z0-9-]{0,31})\) / validate-and-build')


class Waiting(ValueError):
    def __init__(self, message, failed=False):
        super().__init__(message)
        self.failed = failed


def positive(value):
    preview.require(type(value) is int and 1 <= value <= 99999999999999999999, 'Invalid Actions identifier.')
    return value


def items(call, root, path, key):
    result = []
    for page in range(1, 11):
        value = call(root + path + ('&' if '?' in path else '?') + 'per_page=100&page=' + str(page))
        batch = value.get(key)
        preview.require(isinstance(batch, list) and len(batch) <= 100, 'Invalid bounded Actions inventory.')
        result += batch
        if len(batch) < 100:
            return result
    raise ValueError('Actions inventory exceeds its bound.')


def context(repository, call, number, sha, run_id=None, attempt=None, component=None):
    """A missing component job means reuse; a successful component job requires its exact source image."""
    preview.require(preview.SHA.fullmatch(sha), 'Expected an exact PR source SHA.')
    root = 'repos/' + repository
    workflow = call(root + '/actions/workflows/ci.yml')
    latest = {}
    for run in items(call, root, '/actions/runs?event=pull_request&head_sha=' + sha, 'workflow_runs'):
        if (run.get('event') != 'pull_request' or run.get('head_sha') != sha
                or (run.get('head_repository') or {}).get('full_name', '').lower() != repository
                or run.get('pull_requests') and number not in [p.get('number') for p in run['pull_requests']]):
            continue
        identifier = positive(run['workflow_id'])
        if identifier not in latest or run['id'] > latest[identifier]['id']:
            latest[identifier] = run
    ci = latest.get(workflow['id'])
    if not ci:
        raise Waiting('Waiting for the PR CI workflow.')
    ci = call(root + '/actions/runs/' + str(positive(ci['id'])))
    preview.require(ci.get('workflow_id') == workflow['id'] and ci.get('path') == '.github/workflows/ci.yml'
                    and ci.get('event') == 'pull_request' and ci.get('head_sha') == sha
                    and (ci.get('head_repository') or {}).get('full_name', '').lower() == repository
                    and (ci.get('repository') or {}).get('full_name', '').lower() == repository,
                    'CI run does not belong to the selected repository and PR source.')
    preview.require(run_id is None or ci['id'] == run_id, 'A newer CI run superseded this publication.')
    preview.require(attempt is None or ci['run_attempt'] == attempt, 'A newer CI attempt superseded this publication.')
    latest[workflow['id']] = ci
    if any(r.get('status') != 'completed' for r in latest.values()):
        raise Waiting('Waiting for all PR workflows to finish.')
    if ci.get('conclusion') != 'success' or any(r.get('conclusion') not in ('success', 'skipped') for r in latest.values()):
        raise Waiting('PR workflows failed or were cancelled; publication is blocked.', failed=True)
    components = {}
    for job in items(call, root, '/actions/runs/' + str(ci['id']) + '/jobs?filter=all', 'jobs'):
        match = IMAGE_JOB.fullmatch(job.get('name', ''))
        if not match:
            continue
        preview.require(job.get('run_id') == ci['id'], 'Image validation belongs to another run.')
        previous = components.get(match[1])
        if previous is None or (job['run_attempt'], job['id']) > (previous['run_attempt'], previous['id']):
            components[match[1]] = job
    for job in components.values():
        preview.require(job.get('status') == 'completed' and job.get('conclusion') == 'success'
                        and 1 <= job['run_attempt'] <= ci['run_attempt'], 'A selected OCI validation did not succeed.')
    result = {'run': positive(ci['id']), 'attempt': positive(ci['run_attempt']), 'components': sorted(components)}
    if component is not None:
        preview.require(component in components, 'Unchanged components must reuse a published image, without promotion.')
        name = 'oci-image-' + component + '-' + str(ci['id']) + '-' + str(components[component]['run_attempt'])
        artifacts = [a for a in items(call, root, '/actions/runs/' + str(ci['id']) + '/artifacts', 'artifacts') if a.get('name') == name]
        if not artifacts or artifacts[0].get('expired') is not False:
            raise Waiting('The validated OCI archive is missing or expired. Rerun CI; publication never rebuilds.')
        preview.require(len(artifacts) == 1, 'Ambiguous CI archive.')
        artifact = artifacts[0]
        preview.require(not artifact.get('workflow_run') or artifact['workflow_run'].get('id') == ci['id'], 'Archive belongs to another run.')
        result.update(artifact=positive(artifact['id']), name=name)
    return result
