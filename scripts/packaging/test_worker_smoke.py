"""Offline assertions for AWP image compatibility and execution evidence."""
import copy
import unittest
from unittest.mock import Mock

import images
from worker_smoke import completed_flow, worker_evidence


class WorkerEvidenceTests(unittest.TestCase):
    def setUp(self):
        self.entry = images.catalogue("maf-worker")
        self.worker = {"workerId": "worker", "sessionId": "session", "enrollmentState": "Active",
                       "softwareVersion": self.entry["version"] + "+source", "maximumConcurrentAssignments": 1,
                       "capabilities": [{"runtimeKind": "microsoft-agent-framework", "capabilityVersion": "1.0",
                                         "executionMaterialVersions": ["1.0", "1.1"]}]}

    def test_registered_version_and_material_compatibility_are_required(self):
        self.assertEqual("session", worker_evidence(self.worker, self.entry, "worker")["sessionId"])
        for key, value in (("workerId", "another-worker"), ("softwareVersion", "9.0.0"),
                           ("enrollmentState", "Pending"), ("capabilities", [])):
            with self.subTest(key=key):
                worker = copy.deepcopy(self.worker)
                worker[key] = value
                with self.assertRaises(ValueError):
                    worker_evidence(worker, self.entry, "worker")

    def test_pending_and_completed_flow_evidence_preserve_exact_output_and_assignment_identity(self):
        api = Mock()
        api.call.return_value = {"status": 0}
        self.assertIsNone(completed_flow(api, "run", {"echo": "value"}, "worker", "session"))
        placement = {"state": "Succeeded", "assignmentId": "assignment", "attempts": [
            {"attemptId": "attempt", "workerId": "worker", "workerSessionId": "session"}]}
        api.call.side_effect = [{"status": 3, "output": {"echo": "value"}}, placement]
        self.assertEqual({"runId": "run", "assignmentId": "assignment", "attemptId": "attempt"},
                         completed_flow(api, "run", {"echo": "value"}, "worker", "session"))
        for actual in ("another-worker", "worker"):
            wrong = copy.deepcopy(placement)
            wrong["attempts"][0]["workerId"] = actual
            wrong["attempts"][0]["workerSessionId"] = "another-session"
            api.call.side_effect = [{"status": 3, "output": {"echo": "value"}}, wrong]
            with self.assertRaises(ValueError):
                completed_flow(api, "run", {"echo": "value"}, "worker", "session")
        api.call.side_effect = [{"status": 3, "output": {"echo": "wrong"}}]
        with self.assertRaises(ValueError):
            completed_flow(api, "run", {"echo": "value"}, "worker", "session")


if __name__ == "__main__":
    unittest.main()
