"""PostgreSQL document dependencies and host-local bootstrap credentials."""
import json
import re
import secrets

EXTERNAL_LABEL = 'externalDB'
SERVICE = 'postgres-documents'
SECRET_FILE = 'postgresql-documents.password'
EXTERNAL_FILE = 'postgresql-documents.external.json'
CONNECTION_FILE = 'postgresql-documents.connection.json'
ENTRYPOINT_FILE = 'postgresql-documents-bootstrap.sh'
PARAMETERS = 'Agentstration__Bootstrap__Parameters__PostgreSqlDocuments'
SECRET_VARIABLE = 'Agentstration__Bootstrap__Secrets__PostgreSqlDocumentsPassword'


def modifier(name):
    import preview
    preview.require(name == EXTERNAL_LABEL, 'Unknown PostgreSQL document deployment modifier.')
    return {'database': 'external'}


def compatible(plan, delivery):
    import preview
    preview.require(plan['database'] == 'external' or preview.delivery_entry(delivery).get('deploymentRecipeVersion') == 1,
                    'Publish a recipe-compatible extension image before using its internal database.')


def freeze(options):
    import preview
    preview.require(isinstance(options, dict) and set(options) <= {'database'}
                    and options.get('database', 'internal') in ('internal', 'external'), 'Invalid PostgreSQL document deployment mode.')
    plan = {'database': options.get('database', 'internal')}
    if plan['database'] == 'internal':
        plan['image'] = preview.read_json(preview.ROOT / 'deploy/preview/postgresql.json')['reference']
    return validate(plan)


def validate(plan):
    import preview
    preview.require(isinstance(plan, dict) and plan.get('database') in ('internal', 'external'),
                    'Invalid PostgreSQL document deployment mode.')
    expected = {'database', 'image'} if plan['database'] == 'internal' else {'database'}
    preview.require(set(plan) == expected and (plan['database'] != 'internal' or
                    isinstance(plan['image'], str) and preview.POSTGRES_IMAGE.fullmatch(plan['image'])),
                    'PostgreSQL documents require a pinned official image and public recipe metadata only.')
    return plan


def images(plan):
    validate(plan)
    return {SERVICE: {'reference': plan['image'], 'platform': 'linux/amd64'}} if plan['database'] == 'internal' else {}


def describe(plan):
    return ('PostgreSQL document database: **' + plan['database'] + '**.' +
            (' Its database and data are outside preview cleanup.' if plan['database'] == 'external' else ''))


def preprovisioned_files(plan):
    return {EXTERNAL_FILE} if plan['database'] == 'external' else set()


def require_preserving_change(previous, requested):
    import preview
    preview.require(previous['database'] == requested['database'],
                    'Changing PostgreSQL document database mode requires an explicit redeploy/reset.')


def read_external(path):
    import contract
    import preview
    preview.require(path.is_file() and not path.is_symlink() and path.stat().st_size <= 16384,
                    'External document database settings require a bounded private regular file.')
    path.chmod(0o600)
    try:
        return contract.parse(path.read_text(encoding='utf-8'))
    except (ValueError, UnicodeError):
        raise ValueError('Invalid private external document database configuration.') from None


def connection(directory, plan):
    if plan['database'] == 'internal':
        return {'Host': SERVICE, 'Port': 5432, 'Database': 'agentstration_documents', 'Username': 'agentstration_documents'}
    value = read_external(directory / EXTERNAL_FILE)
    return {key: value[key] for key in ('Host', 'Port', 'Database', 'Username')}


def prepare(directory, state, settings, plan, previous):
    import preview
    validate(plan)
    preview.require(not previous or previous['database'] == plan['database'],
                    'Changing PostgreSQL document database mode requires an explicit redeploy/reset.')
    options = settings.setdefault('PostgreSqlDocuments', {})
    preview.require(isinstance(options, dict), 'PostgreSQL document settings must be an object.')
    password = directory / SECRET_FILE
    external = settings.pop('PreviewDatabase', None)
    if plan['database'] == 'internal':
        preview.require(external is None, 'External database credentials require the externalDB modifier.')
        if previous:
            preview.require(password.is_file() and not password.is_symlink(), 'Restore the original document database password.')
        else:
            preview.require(not password.exists(), 'Unrecorded document database credentials exist; restore matching host state.')
            preview.private_write(password, secrets.token_urlsafe(48))
        preview.require(re.fullmatch(r'[A-Za-z0-9_-]{64}', password.read_text()), 'Restore the original generated document database password.')
    else:
        # Optional host provisioning avoids transmitting an existing external credential through CI.
        file = directory / EXTERNAL_FILE
        preview.require(not file.is_symlink(), 'External database configuration must not be a symbolic link.')
        if external is None:
            preview.require(file.is_file(), 'Provide private external document database connection settings on the target host or through protected extension configuration.')
            external = read_external(file)
        preview.require(isinstance(external, dict) and set(external) == {'Host', 'Port', 'Database', 'Username', 'Password'},
                        'External document database settings require Host, Port, Database, Username and Password.')
        preview.require(type(external['Port']) is int and 1 <= external['Port'] <= 65535
                        and all(isinstance(external[key], str) and 1 <= len(external[key]) <= 4096
                                and not any(ord(c) < 32 for c in external[key]) for key in ('Host', 'Database', 'Username', 'Password')),
                        'Invalid private external document database settings.')
        preview.require(re.fullmatch(r'[A-Za-z0-9][A-Za-z0-9.-]{0,252}', external['Host']), 'Invalid external document database host.')
        identity = {key: external[key] for key in ('Host', 'Port', 'Database', 'Username')}
        recorded = directory / CONNECTION_FILE
        if previous:
            preview.require(file.is_file() and read_external(file) == external and password.is_file()
                            and not password.is_symlink() and password.read_text() == external['Password']
                            and recorded.is_file() and not recorded.is_symlink() and preview.read_json(recorded) == identity,
                            'Changing external document connection settings requires an explicit redeploy/reset.')
        preview.private_write(recorded, json.dumps(identity))
        preview.private_write(file, json.dumps(external))
        preview.private_write(password, external['Password'])
    password.chmod(0o600)
    options['AllowedClients'] = [state['instanceId']]
    if plan['database'] == 'internal':
        options.update(AllowedHostsWithoutTls=[SERVICE], ProvisionSchema=True)
    # The secret is read only inside the target container. Compose and Docker Config.Env contain no password.
    script = '#!/bin/sh\nset -eu\nexport ' + SECRET_VARIABLE + '="$(cat /run/secrets/postgresql-documents-password)"\nexec "$@"\n'
    preview.private_write(directory / ENTRYPOINT_FILE, script)
    import extension_tls
    extension_tls.prepare(directory, state)


def overlay(directory, state, plan):
    labels = {'io.agentstration.preview.environment': state['environmentId'],
              'io.agentstration.preview.owner': state.get('deploymentOwnerId', 'unmanaged')}
    values = connection(directory, plan)
    result = {'services': {
        'server': {'environment': {PARAMETERS + key: str(value) for key, value in values.items()},
                   'entrypoint': ['/bin/sh', '/run/secrets/postgresql-documents-bootstrap'],
                   'command': ['dotnet', 'Agentstration.Web.dll'],
                   'secrets': [{'source': 'postgresql-documents-password', 'target': '/run/secrets/postgresql-documents-password'},
                               {'source': 'postgresql-documents-bootstrap', 'target': '/run/secrets/postgresql-documents-bootstrap'}]},
        'extension-postgresql': {'environment': {'Aep__SharedKeyFile__ClientId': state['instanceId']}}},
        'volumes': {}, 'secrets': {
            'postgresql-documents-password': {'file': str(directory / SECRET_FILE)},
            'postgresql-documents-bootstrap': {'file': str(directory / ENTRYPOINT_FILE)}}}
    if plan['database'] == 'internal':
        import preview
        service = preview.read_json(preview.ROOT / 'deploy/preview/extensions/postgresql.json')['service']
        service.update(image=plan['image'], labels=labels)
        result['services'][SERVICE] = service
        result['volumes'][SERVICE] = {'labels': labels}
        result['services']['extension-postgresql']['depends_on'] = {SERVICE: {'condition': 'service_healthy'}}
    return result


def reset(directory):
    import preview
    paths = [directory / name for name in (SECRET_FILE, EXTERNAL_FILE, CONNECTION_FILE, ENTRYPOINT_FILE)]
    preview.require(all(not path.is_symlink() for path in paths), 'Recipe credential paths must not be symbolic links.')
    for path in paths:
        path.unlink(missing_ok=True)


def evidence(directory, plan):
    """Explicit smoke qualification only: inspect private values locally, publish assertions only."""
    import preview
    password = (directory / SECRET_FILE).read_text()
    state = preview.read_json(directory / 'deployment.json')
    for service in state['images']:
        container = preview.compose(directory, 'ps', '--quiet', service, capture=True).strip()
        metadata = json.loads(preview.subprocess.check_output(['docker', 'inspect', container], text=True))[0]
        preview.require(password not in json.dumps(metadata), 'A document database credential appeared in Docker metadata.')
    environment = preview.compose(directory, 'exec', '-T', 'server', 'cat', '/proc/1/environ', capture=True)
    preview.require(SECRET_VARIABLE + '=' + password + '\x00' in environment,
                    'The target server did not receive its local bootstrap credential.')
    value = {'database': plan['database'], 'passwordPrivateInDockerMetadata': True, 'bootstrapEnvironmentVerified': True}
    if plan['database'] == 'internal':
        cluster = preview.compose(directory, 'exec', '-T', SERVICE, 'psql', '--username', 'agentstration_documents',
                                  '--dbname', 'agentstration_documents', '--no-align', '--tuples-only',
                                  '--command', 'SELECT system_identifier FROM pg_control_system()', capture=True).strip()
        preview.require(re.fullmatch(r'[0-9]{1,20}', cluster), 'The document database cluster identity is unavailable.')
        value['clusterId'] = cluster
    return value


def qualify(request, registry, root):
    """Explicit CI fixture only; never activate privileged Bootstrap during real deployment."""
    from datetime import datetime, timedelta, timezone
    import azure_host
    import lifecycle
    import preview
    import preview_smoke
    from worker_smoke import Api, until
    preview.require(request['database'] == 'sqlite' and request['extensionDeployments']['postgresql']['database'] == 'internal',
                    'Document qualification requires disposable SQLite/internal-database fixtures.')
    directory = root / 'state' / request['environmentId']
    local = azure_host.local_request(request)
    bootstrap = {'InitialBootstrapEnabled': True,
                 'InitialProfiles': ['development', 'preview-postgresql-documents'],
                 'Secrets': {'AdminPassword': preview_smoke.bootstrap_password()}}
    try:
        preview.prepare(directory, request['deliveries']['server'], request['deliveries']['worker'], request['environmentId'],
            registry=local['registry'], bootstrap=bootstrap, extension_deliveries=request['extensions'],
            extension_deployments=request['extensionDeployments'])
        azure_host.run('deploy', request, registry, root)
        api = Api(preview.server_address(directory))
        api.call('/api/auth/local/login', {'userName': 'admin', 'password': bootstrap['Secrets']['AdminPassword']}, expected=204)
        api.opener.addheaders.append(('X-Agentstration-Context', '/instance'))
        storage = until(lambda: api.call('/api/storages/preview-postgresql-documents?scopeRef=%2Finstance'), 'document bootstrap')
        collection = storage['definition']['collections'][0]['uid']
        path = '/api/storages/preview-postgresql-documents/collections/' + collection + '/documents'
        saved = api.call(path + '?scopeRef=%2Finstance', {'requestId': 'public-disposable-preview-write',
            'notAfter': (datetime.now(timezone.utc) + timedelta(minutes=2)).isoformat(), 'id': 'fixture',
            'value': {'id': 'fixture', 'title': 'Public disposable preview document'}})
        observed = api.call(path + '/fixture?scopeRef=%2Finstance')
        preview.require(saved['revision'] == observed['revision'], 'The product did not read back the saved document.')
        return {'initialProfileApplied': True, 'documentWriteReadThroughProductApi': True, 'securedSecretCallback': True}
    finally:
        lifecycle.execute('destroy', {**local, 'dataMode': 'reset'}, root / 'state')
        preview.require(not any(lifecycle.inventory('preview-' + request['environmentId']).values()),
                        'The document Bootstrap fixture retained owned resources.')
