import copy
import unittest
import images
import smoke

class UtilitiesImageContracts(unittest.TestCase):
    def test_real_identity_and_version_drive_generic_smoke(self):
        entry = images.catalogue("utilities")
        self.assertEqual("Agentstration.Extensions.Utilities", entry["extensionId"])
        self.assertEqual("0.1.0", entry["version"])
        delivery = {"extension": entry}
        manifest = {
            "protocolVersion": entry["aepVersion"],
            "extension": {"id": entry["extensionId"], "version": entry["version"]},
            "contributions": {
                "tools": [{"id": "hash.compute"}, {"id": "json.compact"}, {"id": "text.upper"}],
                "schemaFormats": [{"id": "json-schema.validation"}],
            },
        }
        smoke.check_manifest(manifest, delivery)
        for kind, contributions in manifest["contributions"].items():
            for contribution in contributions:
                with self.subTest(kind=kind, missing=contribution["id"]):
                    invalid = copy.deepcopy(manifest)
                    invalid["contributions"][kind].remove(contribution)
                    with self.assertRaisesRegex(ValueError, "Expected extension contribution is missing"):
                        smoke.check_manifest(invalid, delivery)
        for key in ("id", "version"):
            invalid = copy.deepcopy(manifest)
            invalid["extension"][key] = "invalid"
            with self.assertRaises(ValueError): smoke.check_manifest(invalid, delivery)
