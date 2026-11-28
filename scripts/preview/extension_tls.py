"""Target-local TLS for AEP Secret capability callbacks; no public ingress or proxy."""
import shutil
import subprocess

CERTIFICATE = 'aep-authority.crt'
PRIVATE_KEY = 'aep-authority.key'
CA_TARGET = '/run/secrets/aep-authority-ca'


def create(directory):
    import preview
    preview.require(shutil.which('openssl'), 'Install OpenSSL on the target host for secured AEP callbacks.')
    # OpenSSL runs only on the target. Neither the key nor a password appears in arguments/output.
    result = subprocess.run(['openssl', 'req', '-x509', '-newkey', 'ec', '-pkeyopt', 'ec_paramgen_curve:P-256',
        '-noenc', '-sha256', '-days', '3650', '-subj', '/CN=server',
        '-addext', 'subjectAltName=DNS:server', '-addext', 'basicConstraints=critical,CA:TRUE',
        '-addext', 'keyUsage=critical,digitalSignature,keyCertSign', '-addext', 'extendedKeyUsage=serverAuth',
        '-keyout', str(directory / PRIVATE_KEY), '-out', str(directory / CERTIFICATE)],
        capture_output=True, timeout=30)
    preview.require(result.returncode == 0, 'Target-local AEP certificate generation failed; inspect the private host state.')


def prepare(directory, state):
    import preview
    paths = [directory / name for name in (CERTIFICATE, PRIVATE_KEY)]
    preview.require(all(not path.is_symlink() for path in paths), 'AEP TLS paths must not be symbolic links.')
    if state.get('aepSecretAccessTls'):
        preview.require(all(path.is_file() for path in paths), 'Restore the original AEP TLS certificate and key.')
    else:
        preview.require(not any(path.exists() for path in paths), 'Restore matching host state for existing AEP TLS credentials.')
        create(directory)
        state['aepSecretAccessTls'] = True
    for path in paths:
        path.chmod(0o600)


def overlay(directory, state):
    names = [name for name in state['images'] if name.startswith('extension-')]
    if not names or not state.get('aepSecretAccessTls'):
        return {'services': {}, 'secrets': {}}
    services = {name: {'environment': {'SSL_CERT_FILE': CA_TARGET},
                       'secrets': [{'source': 'aep-authority-certificate', 'target': CA_TARGET}]} for name in names}
    services['server'] = {'environment': {
        'ASPNETCORE_URLS': 'http://+:8080;https://+:8443',
        'ASPNETCORE_Kestrel__Certificates__Default__Path': '/run/secrets/aep-authority-certificate',
        'ASPNETCORE_Kestrel__Certificates__Default__KeyPath': '/run/secrets/aep-authority-private-key',
        'Agentstration__Aep__SecretAccess__PublicBaseUrl': 'https://server:8443'},
        'secrets': [{'source': 'aep-authority-certificate', 'target': '/run/secrets/aep-authority-certificate'},
                    {'source': 'aep-authority-private-key', 'target': '/run/secrets/aep-authority-private-key'}]}
    return {'services': services, 'secrets': {
        'aep-authority-certificate': {'file': str(directory / CERTIFICATE)},
        'aep-authority-private-key': {'file': str(directory / PRIVATE_KEY)}}}


def reset(directory, state):
    import preview
    if state.get('aepSecretAccessTls'):
        paths = [directory / name for name in (CERTIFICATE, PRIVATE_KEY)]
        preview.require(all(not path.is_symlink() for path in paths), 'AEP TLS paths must not be symbolic links.')
        for path in paths:
            path.unlink(missing_ok=True)
        state.pop('aepSecretAccessTls')
