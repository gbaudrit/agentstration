"""AEP callbacks retain HTTPS, target-local keys and public-only extension trust."""
import json
from pathlib import Path
import shutil
import ssl
import stat
import tempfile
import unittest

import extension_tls
from test_extension_deployments import mock_certificate_generation


class ExtensionTlsTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix='preview-tls-')
        self.addCleanup(temporary.cleanup)
        self.directory = Path(temporary.name)
        self.state = {'images': {'server': {}, 'worker': {}, 'extension-postgresql': {}, 'extension-foundry': {}}}

    def test_tls_key_is_target_local_reused_and_never_mounted_into_extensions_or_worker(self):
        generated = mock_certificate_generation(self)
        extension_tls.prepare(self.directory, self.state)
        extension_tls.prepare(self.directory, self.state)
        self.assertEqual(1, generated.call_count)
        self.assertEqual(0o600, stat.S_IMODE((self.directory / extension_tls.PRIVATE_KEY).stat().st_mode))
        overlay = extension_tls.overlay(self.directory, self.state)
        self.assertNotIn('worker', overlay['services'])
        self.assertEqual('https://server:8443', overlay['services']['server']['environment']['Agentstration__Aep__SecretAccess__PublicBaseUrl'])
        for name in ('extension-postgresql', 'extension-foundry'):
            self.assertEqual([{'source': 'aep-authority-certificate', 'target': extension_tls.CA_TARGET}], overlay['services'][name]['secrets'])
        self.assertNotIn('public-disposable-private-key-fixture', json.dumps(overlay))
        self.assertTrue(all('ports' not in service for service in overlay['services'].values()))

    def test_deselection_preserves_trust_and_reset_removes_both_files(self):
        mock_certificate_generation(self)
        extension_tls.prepare(self.directory, self.state)
        self.state['images'] = {'server': {}, 'worker': {}}
        self.assertEqual({}, extension_tls.overlay(self.directory, self.state)['services'])
        self.assertTrue((self.directory / extension_tls.PRIVATE_KEY).exists())
        extension_tls.reset(self.directory, self.state)
        self.assertNotIn('aepSecretAccessTls', self.state)
        self.assertFalse((self.directory / extension_tls.PRIVATE_KEY).exists())
        self.assertFalse((self.directory / extension_tls.CERTIFICATE).exists())

    def test_missing_or_symlinked_keys_are_not_silently_replaced(self):
        generated = mock_certificate_generation(self)
        extension_tls.prepare(self.directory, self.state)
        key = self.directory / extension_tls.PRIVATE_KEY
        key.unlink()
        with self.assertRaisesRegex(ValueError, 'Restore the original'):
            extension_tls.prepare(self.directory, self.state)
        key.symlink_to(self.directory / 'outside')
        with self.assertRaisesRegex(ValueError, 'symbolic links'):
            extension_tls.prepare(self.directory, self.state)
        self.assertEqual(1, generated.call_count)

    @unittest.skipUnless(shutil.which('openssl'), 'Optional native OpenSSL certificate qualification')
    def test_generated_certificate_and_key_load_as_a_matching_tls_identity_and_trusted_ca(self):
        extension_tls.prepare(self.directory, self.state)
        server = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
        server.load_cert_chain(self.directory / extension_tls.CERTIFICATE, self.directory / extension_tls.PRIVATE_KEY)
        client = ssl.create_default_context(cafile=str(self.directory / extension_tls.CERTIFICATE))
        self.assertGreaterEqual(client.cert_store_stats()['x509_ca'], 1)
