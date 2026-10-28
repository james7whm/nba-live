import importlib.util
from pathlib import Path
import unittest

path = Path(__file__).resolve().parents[1] / "audit_macho.py"
spec = importlib.util.spec_from_file_location("audit_macho", path)
audit = importlib.util.module_from_spec(spec)
spec.loader.exec_module(audit)


class MinimumVersionTests(unittest.TestCase):
    def test_modern_command_uses_minos_not_linker_version(self):
        data = "cmd LC_BUILD_VERSION\n platform MACOS\n minos 12.0\n sdk 15.0\n tool LD\n version 999.0\n"
        self.assertEqual(audit.minimum_version(data), (12, 0))

    def test_legacy_command_and_rejects_unknown_platform(self):
        self.assertEqual(audit.minimum_version("cmd LC_VERSION_MIN_MACOSX\n version 10.13\n sdk 14.0\n"), (10, 13))
        with self.assertRaises(ValueError):
            audit.minimum_version("cmd LC_BUILD_VERSION\n platform IOS\n minos 12.0\n")
        with self.assertRaises(ValueError):
            audit.minimum_version("cmd LC_BUILD_VERSION\n platform MACOS\n sdk 15.0\n")


if __name__ == "__main__":
    unittest.main()
