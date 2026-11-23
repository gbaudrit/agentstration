"""Offline regression checks for the real authority enrollment smoke's assertions."""
import copy
import json
from pathlib import Path
import sqlite3
import tempfile
import unittest

from enrollment_smoke import enrollment_snapshot


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

    def test_duplicate_installation_is_not_accepted_as_restart_success(self):
        self.store(self.enrollment, self.registration, duplicate=True)
        with self.assertRaises(ValueError):
            enrollment_snapshot(self.database, self.delivery, self.instance)


if __name__ == "__main__":
    unittest.main()
