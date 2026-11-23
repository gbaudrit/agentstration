"""Offline regression checks for the real authority enrollment smoke's assertions."""
import copy
import http.server
import threading
import json
from pathlib import Path
import sqlite3
import tempfile
import unittest
from unittest.mock import patch

from enrollment_smoke import announce, enrollment_snapshot, request


class EnrollmentEvidenceTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.database = Path(self.temporary.name) / "control-plane.db"
        self.instance = "10000000-0000-0000-0000-000000000001"
        self.delivery = {"extension": {"extensionId": "Agentstration.Extensions.Foundry", "version": "0.1.0"}}
        self.enrollment = {"uid": "enrollment-uid", "generation": 3, "definition": {
            "instanceId": self.instance, "extensionId": "Agentstration.Extensions.Foundry", "extensionVersion": "0.1.0",
            "state": "available", "targetScopeRef": "/instance", "enrollmentMode": "sharedKeyFile",
            "registrationName": "foundry", "endpoint": "http://foundry:8080/"}}
        self.registration = {"uid": "registration-uid", "generation": 1, "metadata": {"name": "foundry"},
                             "scopeRef": "/instance", "definition": {"enabled": True,
                             "expectedExtensionId": "Agentstration.Extensions.Foundry", "enrollmentMode": "sharedKeyFile",
                             "authenticationMode": "staticBearer", "endpoint": "http://foundry:8080/"}}
        with sqlite3.connect(self.database) as connection:
            connection.execute("CREATE TABLE ControlPlaneResources (Kind TEXT, Payload TEXT)")

    def store(self, enrollment, registration, duplicate=False):
        with sqlite3.connect(self.database) as connection:
            connection.execute("DELETE FROM ControlPlaneResources")
            connection.executemany("INSERT INTO ControlPlaneResources VALUES (?, ?)", [
                ("AepEnrollmentRequest", json.dumps(enrollment)), ("ExtensionRegistration", json.dumps(registration)),
                # The smoke must never inspect or include a Secret payload in its evidence.
                ("Secret", "not-json-and-not-readable-by-the-smoke")])
            if duplicate:
                connection.execute("INSERT INTO ControlPlaneResources VALUES (?, ?)",
                                   ("AepEnrollmentRequest", json.dumps(enrollment)))

    def test_available_enrollment_evidence_contains_only_stable_identifiers(self):
        self.store(self.enrollment, self.registration)
        result = enrollment_snapshot(self.database, self.delivery, self.instance)
        self.assertEqual({"instanceId": self.instance, "enrollmentUid": "enrollment-uid", "enrollmentGeneration": 3,
                          "registrationUid": "registration-uid", "registrationGeneration": 1,
                          "registrationName": "foundry"}, result)

    def test_unready_mismatched_or_wrong_scope_enrollment_cannot_pass(self):
        for key, value in (("state", "pending"), ("state", "verificationFailed"), ("instanceId", "another-instance"),
                           ("extensionId", "another-extension"), ("extensionVersion", "9.0.0"),
                           ("targetScopeRef", "/tenants/other"), ("enrollmentMode", "pairingCode")):
            with self.subTest(key=key, value=value):
                enrollment = copy.deepcopy(self.enrollment)
                enrollment["definition"][key] = value
                self.store(enrollment, self.registration)
                with self.assertRaises(ValueError):
                    enrollment_snapshot(self.database, self.delivery, self.instance)

    def test_disabled_wrong_endpoint_or_unauthenticated_registration_cannot_pass(self):
        for key, value in (("enabled", False), ("endpoint", "http://other:8080/"),
                           ("authenticationMode", "none"), ("expectedExtensionId", "another-extension")):
            with self.subTest(key=key):
                registration = copy.deepcopy(self.registration)
                registration["definition"][key] = value
                self.store(self.enrollment, registration)
                with self.assertRaises(ValueError):
                    enrollment_snapshot(self.database, self.delivery, self.instance)

    def test_http_probe_accepts_plain_text_health_and_reads_json_protocol_responses(self):
        class Handler(http.server.BaseHTTPRequestHandler):
            def log_message(self, *_args):
                pass

            def do_GET(self):
                health = self.path == "/health/ready"
                body = b"Healthy" if health else b'{"state":"available"}'
                self.send_response(200)
                self.send_header("Content-Type", "text/plain" if health else "application/json")
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                self.wfile.write(body)

        with http.server.ThreadingHTTPServer(("127.0.0.1", 0), Handler) as server:
            worker = threading.Thread(target=server.serve_forever)
            worker.start()
            try:
                base = f"http://127.0.0.1:{server.server_port}"
                self.assertIsNone(request(base, "/health/ready"))
                self.assertEqual({"state": "available"}, request(base, "/protocol"))
            finally:
                server.shutdown()
                worker.join()

    def test_signed_reannouncement_matches_the_aep_dotnet_protocol_vector(self):
        # Generated with AepEnrollmentProofs.Sign and AepProtocol.JsonOptions, not Python serialization.
        manifest = {"extension": {"id": "Agentstration.Extensions.Foundry", "name": "Microsoft Foundry",
                    "version": "0.1.0", "description": "Optional Microsoft Foundry model-deployment discovery for Agentstration."}}
        with patch("enrollment_smoke.time.time", return_value=1), patch("enrollment_smoke.request") as request:
            announce("http://authority:8080", manifest, self.instance, "public-disposable-protocol-test-fixture")
        self.assertEqual("sharedKeyFile", request.call_args.args[2]["method"])
        self.assertEqual("vdwgwdeVSHD7A7ekwfOkpgp6QA73Y5U68cfXyot9Zqo=",
                         request.call_args.args[3]["X-AEP-Enrollment-Signature"])

    def test_duplicate_installation_is_not_accepted_as_restart_success(self):
        self.store(self.enrollment, self.registration, duplicate=True)
        with self.assertRaises(ValueError):
            enrollment_snapshot(self.database, self.delivery, self.instance)


if __name__ == "__main__":
    unittest.main()
