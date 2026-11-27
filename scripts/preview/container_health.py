"""Bounded container status fields only; never export inspect/configuration or health logs."""
import json
import subprocess

STATES = {'created', 'running', 'paused', 'restarting', 'removing', 'exited', 'dead', 'missing', 'unknown'}
HEALTH = {'healthy', 'unhealthy', 'starting', 'none', 'unknown'}


def summarize(services, observations):
    result = {}
    for service in services:
        rows = [row for row in observations if row[0] == service]
        state, health, restarts = ('missing', 'unknown', None) if not rows else ('unknown', 'unknown', None)
        if len(rows) == 1:
            _, state, health, restarts = rows[0]
        result[service] = {'state': state if isinstance(state, str) and state in STATES else 'unknown',
                           'health': health if isinstance(health, str) and health in HEALTH else 'unknown',
                           'restarts': restarts if type(restarts) is int and 0 <= restarts <= 2147483647 else None}
    return result


def from_containers(services, containers):
    return summarize(services, [(item['Config']['Labels'].get('com.docker.compose.service'),
        item['State'].get('Status', 'running' if item['State'].get('Running') else 'unknown'),
        item['State'].get('Health', {}).get('Status', 'none'), item.get('RestartCount')) for item in containers])


def collect(project, services):
    def docker(*arguments):
        return subprocess.run(['docker', *arguments], text=True, capture_output=True, check=True, timeout=60).stdout
    identifiers = docker('ps', '--all', '--filter', 'label=com.docker.compose.project=' + project,
                         '--format', '{{.ID}}').splitlines()
    if len(identifiers) > 64:
        raise ValueError('Container inventory exceeds its bound.')
    # Select only enum/counter fields at the Docker boundary. No .Config.Env, .State.Error or health log.
    template = '[{{json (index .Config.Labels "com.docker.compose.service")}},{{json .State.Status}},' + \
               '{{if .State.Health}}{{json .State.Health.Status}}{{else}}"none"{{end}},{{.RestartCount}}]'
    raw = docker('inspect', '--format', template, *identifiers) if identifiers else ''
    if len(raw.encode()) > 65536:
        raise ValueError('Container health output exceeds its bound.')
    return summarize(services, [json.loads(line) for line in raw.splitlines()])
