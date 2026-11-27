#!/usr/bin/env python3
"""Run the GUID example on isolated local storage; never accesses Azure or a model."""
import argparse
import http.cookiejar
import json
import os
from pathlib import Path
import shutil
import secrets
import uuid
import socket
import subprocess
import tempfile
import time
import urllib.error
import urllib.request

GUID = '2e65e8a4-0f73-49b6-ae24-636a65d7e793'
ROOT = Path(__file__).resolve().parents[2]


def smoke(published, worker_published):
    with tempfile.TemporaryDirectory(prefix='agentstration-guid-example-') as temporary:
        directory = Path(temporary)
        app = directory / 'app'
        shutil.copytree(published, app)
        shutil.copytree(ROOT / 'deploy/bootstrap/profiles', app / 'bootstrap/profiles')
        shutil.copytree(ROOT / 'deploy/profiles', app / 'deploy/profiles')
        with socket.socket() as port:
            port.bind(('127.0.0.1', 0))
            number = port.getsockname()[1]
        base = 'http://127.0.0.1:' + str(number)
        instance, worker, credential = (str(uuid.uuid4()) for _ in range(3))
        key = directory / 'worker.key'
        key.write_text(secrets.token_urlsafe(48))
        key.chmod(0o600)
        configuration = {**os.environ,
            'Agentstration__InstanceId':instance,
            'Agentstration__AwpWorkerTrust__Enabled':'true',
            'Agentstration__AwpWorkerTrust__InstanceId':instance,
            'Agentstration__AwpWorkerTrust__Credentials__0__WorkerId':worker,
            'Agentstration__AwpWorkerTrust__Credentials__0__CredentialId':credential,
            'Agentstration__AwpWorkerTrust__Credentials__0__SharedKeyFile':str(key),
            'Logging__LogLevel__Default':'Warning',
            'Logging__LogLevel__Microsoft.EntityFrameworkCore.Database.Command':'Warning', 'ASPNETCORE_ENVIRONMENT': 'Development', 'ASPNETCORE_URLS': base,
            'AI__Provider': 'Deterministic', 'Data__Directory': str(directory / 'data'),
            'Agentstration__Bootstrap__InitialBootstrapEnabled': 'true',
            'Agentstration__Bootstrap__EnableAdditionalSettingsBootstrap': 'true',
            'Agentstration__AdditionalSettings__0': 'deploy/profiles/previews/' + GUID + '/' + GUID + '.appsettings.json',
            # Public disposable Development fixture, used only on loopback and deleted on completion.
            'Agentstration__Bootstrap__Secrets__AdminPassword': 'admin'}
        opener = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))
        def request(path, data=None):
            payload = None if data is None else json.dumps(data).encode()
            with opener.open(urllib.request.Request(base + path, payload, {'Content-Type':'application/json'}), timeout=10) as response:
                raw = response.read()
                if not raw:
                    return None
                try:
                    return json.loads(raw)
                except ValueError:
                    return raw.decode('utf-8')
        def start(log):
            process = subprocess.Popen(['dotnet', str(app / 'Agentstration.Web.dll'), '--contentRoot', str(app)],
                env=configuration, cwd=app, stdout=log, stderr=log)
            deadline = time.monotonic() + 120
            while time.monotonic() < deadline and process.poll() is None:
                try:
                    request('/health/ready')
                    return process
                except (ValueError, urllib.error.URLError, TimeoutError):
                    time.sleep(.25)
            process.terminate()
            process.wait(timeout=15)
            raise RuntimeError('GUID example did not become ready; private startup log is withheld.')
        def stop(process):
            process.terminate()
            process.wait(timeout=30)
        with (directory / 'private-startup.log').open('wb') as log:
            process = start(log)
            worker_environment = {**os.environ, 'ASPNETCORE_URLS':'http://127.0.0.1:0',
                'Logging__LogLevel__Default':'Warning',
                'Agentstration__RuntimeWorker__AuthorityUrl':base,
                'Agentstration__RuntimeWorker__InstanceId':instance,
                'Agentstration__RuntimeWorker__WorkerId':worker,
                'Agentstration__RuntimeWorker__CredentialId':credential,
                'Agentstration__RuntimeWorker__SharedKeyFile':str(key),
                'Agentstration__RuntimeWorker__AllowInsecureHttp':'true'}
            worker_process = subprocess.Popen(['dotnet', str(worker_published / 'Agentstration.Runtime.Worker.MicrosoftAgentFramework.dll')],
                env=worker_environment, cwd=worker_published, stdout=log, stderr=log)
            try:
                request('/health')
                request('/api/auth/local/login', {'userName':'admin','password':'admin'})
                before = request('/api/entries/bootstrap-echo')
                result = request('/api/flows/bootstrap-echo/runs', {'input':{'message':'guid-example'}})
                # Flow API accepts durable work; inspect the returned canonical Flow run.
                run_id = result['id']
                deadline = time.monotonic() + 30
                run = result
                while time.monotonic() < deadline:
                    run = request('/api/flows/bootstrap-echo/runs/' + run_id)
                    if str(run.get('status')).lower() in ('succeeded', 'failed', 'cancelled', 'timedout', '3', '4', '5', '6'):
                        break
                    time.sleep(.1)
                if str(run.get('status')).lower() not in ('succeeded', '3'):
                    raise RuntimeError('Deterministic GUID Flow did not complete.')
            finally:
                stop(worker_process)
                stop(process)
            process = start(log)
            try:
                request('/api/auth/local/login', {'userName':'admin','password':'admin'})
                after = request('/api/entries/bootstrap-echo')
                if before != after:
                    raise RuntimeError('GUID example changed the existing Entry on restart.')
            finally:
                stop(process)
        print(json.dumps({'guid':GUID,'readiness':True,'deterministicFlow':'succeeded','restartPreservedEntry':True}))


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--published', type=Path, required=True, help='Release dotnet publish output for Agentstration.Web')
    parser.add_argument('--worker-published', type=Path, required=True, help='Release publish output for the MAF Worker')
    args = parser.parse_args()
    smoke(args.published.resolve(), args.worker_published.resolve())
