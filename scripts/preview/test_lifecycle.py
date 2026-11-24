"""Lifecycle ownership, retry, lock and safe-result contracts against a bounded Docker fake."""
import copy
from datetime import datetime, timezone
import json
from pathlib import Path
import stat
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

import contract
import lifecycle
import preview
from test_contract import receipts, request


class Host:
    def __init__(self):
        self.daemon = "daemon-a"
        self.resources = {"containers": [], "volumes": [], "networks": []}
        self.mutations = []
        self.claim_owner = None
        self.fail_up = False

    def inventory(self, project):
        return {kind: [copy.deepcopy(item) for item in values if
                       (item.get("Config", {}).get("Labels", {}) if kind == "containers" else item["Labels"]).get(lifecycle.PROJECT) == project]
                for kind, values in self.resources.items()}

    def docker(self, *args):
        if args[:2] == ("info", "--format"):
            return self.daemon
        self.mutations.append(args)
        if args[:2] == ("volume", "create"):
            labels = dict(args[index + 1].split("=", 1) for index, arg in enumerate(args) if arg == "--label")
            if self.claim_owner:
                labels[lifecycle.OWNER] = self.claim_owner
            if not any(item["Name"] == args[-1] for item in self.resources["volumes"]):
                self.resources["volumes"].append({"Name": args[-1], "Labels": labels})
            return args[-1]
        if args[0] == "stop":
            return args[-1]
        kind = "containers" if args[0] == "rm" else "volumes" if args[:2] == ("volume", "rm") else "networks"
        self.resources[kind] = [item for item in self.resources[kind] if item.get("Id", item.get("Name")) != args[-1]]
        return args[-1]

    def compose(self, directory, *args, **_kwargs):
        if args[0] == "up":
            if self.fail_up:
                raise RuntimeError("public-disposable-startup-failure")
            state = preview.read_json(directory / "deployment.json")
            labels = {lifecycle.PROJECT: "preview-" + state["environmentId"], lifecycle.OWNER: state["deploymentOwnerId"]}
            for service, image in state["images"].items():
                identity = state["environmentId"] + "-" + service
                self.resources["containers"] = [item for item in self.resources["containers"] if item["Id"] != identity]
                self.resources["containers"].append({"Id": identity,
                    "Config": {"Image": image["reference"], "Labels": {**labels, "com.docker.compose.service": service}},
                    "State": {"Running": True, "Health": {"Status": "healthy"}},
                    "NetworkSettings": {"Ports": {"8080/tcp": [{"HostIp": "127.0.0.1", "HostPort": str(state["port"] or 5090)}]}}})
            for kind, identity in (("volumes", state["environmentId"] + "-data"), ("networks", state["environmentId"] + "-network")):
                self.resources[kind] = [item for item in self.resources[kind] if item.get("Id", item.get("Name")) != identity]
                self.resources[kind].append({"Id" if kind == "networks" else "Name": identity, "Labels": labels})
        return ""


class LifecycleTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix="lifecycle-contract-")
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.host = Host()
        for target, replacement in (("docker", self.host.docker), ("inventory", self.host.inventory)):
            patcher = patch.object(lifecycle, target, side_effect=replacement)
            patcher.start()
            self.addCleanup(patcher.stop)
        patcher = patch.object(preview, "compose", side_effect=self.host.compose)
        patcher.start()
        self.addCleanup(patcher.stop)
        patcher = patch.object(preview, "verify", return_value={})
        self.verify = patcher.start()
        self.addCleanup(patcher.stop)

    def execute(self, action, value=None, **kwargs):
        return lifecycle.execute(action, value or request(), self.root, **kwargs)

    def test_validation_has_no_provider_or_filesystem_side_effect(self):
        value = request(provider="azure-vm", target={"subscriptionId": "a" * 32, "region": "westeurope", "vmSize": "Standard_B2ms"})
        result = self.execute("validate", value)
        self.assertEqual("validated", result["state"])
        self.assertEqual({}, result["providerResourceIds"])
        self.assertEqual([], list(self.root.iterdir()))
        with self.assertRaises(ValueError):
            self.execute("deploy", value)
        self.assertFalse(self.host.mutations)

    def test_absent_status_and_destroy_do_not_create_an_environment(self):
        self.assertEqual("absent", self.execute("status")["state"])
        self.assertEqual("destroyed", self.execute("destroy")["state"])
        self.assertFalse((self.root / "example").exists())
        self.assertFalse(self.host.mutations)

    def test_repeated_deploy_preserves_identity_credentials_expiry_and_returns_safe_resource_ids(self):
        now = datetime(2026, 11, 24, tzinfo=timezone.utc)
        first = self.execute("deploy", now=now)
        directory = self.root / "example"
        state = preview.read_json(directory / "deployment.json")
        secret = (directory / "worker.key").read_bytes()
        second = self.execute("deploy", now=datetime(2026, 11, 25, tzinfo=timezone.utc))
        self.assertEqual(first["expiresAt"], second["expiresAt"])
        self.assertEqual(state, preview.read_json(directory / "deployment.json"))
        self.assertEqual(secret, (directory / "worker.key").read_bytes())
        self.assertEqual("running", second["state"])
        self.assertEqual("http://127.0.0.1:5080", second["url"])
        self.assertTrue(second["providerResourceIds"]["containers"])
        self.assertFalse(secret.decode() in json.dumps(second))
        self.assertEqual("running", self.execute("status")["state"])
        self.assertEqual(2, self.verify.call_count)

    def test_destroy_preserves_data_by_default_and_reset_is_idempotent(self):
        self.execute("deploy")
        original_volumes = copy.deepcopy(self.host.resources["volumes"])
        self.execute("destroy")
        self.execute("destroy")
        self.assertEqual(original_volumes, self.host.resources["volumes"])
        self.assertEqual("stopped", self.execute("status")["state"])
        self.execute("deploy")
        self.execute("destroy", request(data_mode="reset"))
        self.execute("destroy", request(data_mode="reset"))
        self.assertFalse(any(self.host.resources.values()))
        self.assertEqual("absent", self.execute("status")["state"])

    def test_foreign_container_volume_or_network_blocks_every_mutation(self):
        self.execute("deploy")
        for kind in self.host.resources:
            with self.subTest(kind=kind):
                item = copy.deepcopy(self.host.resources[kind][0])
                labels = item["Config"]["Labels"] if kind == "containers" else item["Labels"]
                labels[lifecycle.OWNER] = "foreign-owner"
                self.host.resources[kind].append(item)
                before = len(self.host.mutations)
                for action in ("deploy", "status", "destroy"):
                    with self.assertRaises(ValueError):
                        self.execute(action)
                self.assertEqual(before, len(self.host.mutations))
                self.host.resources[kind].pop()

    def test_unmanaged_environment_cannot_be_adopted_or_deleted(self):
        self.host.resources["volumes"].append({"Name": "foreign-data", "Labels": {lifecycle.PROJECT: "preview-example"}})
        for action in ("deploy", "destroy", "status"):
            with self.assertRaises(ValueError):
                self.execute(action)
        self.assertFalse(self.host.mutations)
        self.assertFalse((self.root / "example").exists())

    def test_atomic_claim_detects_racing_owners_before_starting_containers(self):
        self.host.claim_owner = "foreign-owner"
        with self.assertRaises(ValueError):
            self.execute("deploy")
        self.assertFalse(self.host.resources["containers"])
        self.assertEqual("foreign-owner", self.host.resources["volumes"][0]["Labels"][lifecycle.OWNER])

    def test_two_environments_and_destroy_are_isolated(self):
        self.execute("deploy")
        other = request()
        other["environmentId"] = "other"
        second = self.execute("deploy", other)
        self.execute("destroy", request(data_mode="reset"))
        self.assertEqual("running", self.execute("status", other)["state"])
        self.assertEqual(second["providerResourceIds"], self.execute("status", other)["providerResourceIds"])

    def test_lost_state_daemon_changes_and_stale_destroy_requests_are_rejected(self):
        self.execute("deploy")
        self.host.daemon = "daemon-b"
        with self.assertRaises(ValueError):
            self.execute("destroy")
        self.host.daemon = "daemon-a"
        stale = request()
        stale["target"]["port"] = 5099
        with self.assertRaises(ValueError):
            self.execute("destroy", stale)
        (self.root / "example/lifecycle.json").unlink()
        with self.assertRaises(ValueError):
            self.execute("destroy")

    def test_partial_startup_can_retry_with_the_original_identity_and_key(self):
        self.host.fail_up = True
        with self.assertRaises(RuntimeError):
            self.execute("deploy")
        directory = self.root / "example"
        key = (directory / "worker.key").read_bytes()
        owner = preview.read_json(directory / "deployment.json")["deploymentOwnerId"]
        self.host.fail_up = False
        self.assertEqual("running", self.execute("deploy")["state"])
        self.assertEqual(owner, preview.read_json(directory / "deployment.json")["deploymentOwnerId"])
        self.assertEqual(key, (directory / "worker.key").read_bytes())

    def test_per_environment_locks_reject_concurrent_mutation_without_blocking_other_ids(self):
        with lifecycle.locked(self.root, "example"):
            with self.assertRaises(ValueError):
                self.execute("deploy")
            with lifecycle.locked(self.root, "other"):
                pass
        self.assertFalse(self.host.mutations)

    def test_public_cli_plans_and_validates_without_contacting_a_provider(self):
        server, worker = receipts()
        inputs = {"server": server, "worker": worker, "target": {"port": 5080, "daemonId": "daemon-a"}}
        for name, value in inputs.items():
            (self.root / (name + ".json")).write_text(json.dumps(value))
        planned = self.root / "request.json"
        command = [sys.executable, str(Path(lifecycle.__file__))]
        result = subprocess.run([*command, "plan", "--server-delivery", str(self.root / "server.json"),
                                 "--worker-delivery", str(self.root / "worker.json"), "--target", str(self.root / "target.json"),
                                 "--environment-id", "example", "--output", str(planned)], capture_output=True, text=True)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(request(), contract.loads(planned.read_text()))
        self.assertEqual(0o600, stat.S_IMODE(planned.stat().st_mode))
        checked = subprocess.run([*command, "validate", "--request", str(planned)], capture_output=True, text=True)
        self.assertEqual(0, checked.returncode, checked.stderr)
        self.assertEqual("validated", json.loads(checked.stdout)["state"])

    def test_public_cli_rejects_operation_flag_confusion_and_never_echoes_rejected_values(self):
        value = request()
        value["password"] = "public-disposable-test-value"
        path = self.root / "invalid.json"
        path.write_text(json.dumps(value))
        command = [sys.executable, str(Path(lifecycle.__file__)), "validate", "--request", str(path)]
        checked = subprocess.run(command, capture_output=True, text=True)
        self.assertNotEqual(0, checked.returncode)
        self.assertNotIn(value["password"], checked.stdout + checked.stderr)
        checked = subprocess.run([*command, "--data-mode", "reset"], capture_output=True, text=True)
        self.assertNotEqual(0, checked.returncode)
        self.assertIn("unrecognized arguments", checked.stderr)

    def test_mismatched_credential_ownership_cannot_be_silently_reassigned(self):
        self.execute("deploy")
        path = self.root / "example/deployment.json"
        state = preview.read_json(path)
        state["deploymentOwnerId"] = "b" * 32
        path.write_text(json.dumps(state))
        before = len(self.host.mutations)
        with self.assertRaises(ValueError):
            self.execute("deploy")
        self.assertEqual(before, len(self.host.mutations))


if __name__ == "__main__":
    unittest.main()
