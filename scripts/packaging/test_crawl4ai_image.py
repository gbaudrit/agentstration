import copy
import unittest
import images
import smoke

class Crawl4AIImageContracts(unittest.TestCase):
    def test_real_identity_and_version_drive_generic_smoke(self):
        entry = images.catalogue("crawl4ai")
        self.assertEqual("Agentstration.Extensions.Crawl4AI", entry["extensionId"])
        self.assertEqual("0.1.0", entry["version"])
        delivery = {"extension": entry}
        manifest = {"protocolVersion": entry["aepVersion"], "extension": {"id": entry["extensionId"], "version": entry["version"]}, "contributions": {"tools": [{"id": "crawl4ai.web.fetch"}]}}
        for kind, ids in entry.get("contributions", {}).items():
            manifest["contributions"][kind] = [{"id": value} for value in ids]
        smoke.check_manifest(manifest, delivery)
        for key in ("id", "version"):
            invalid = copy.deepcopy(manifest)
            invalid["extension"][key] = "invalid"
            with self.assertRaises(ValueError): smoke.check_manifest(invalid, delivery)
