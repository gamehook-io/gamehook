import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location("release_version", Path(__file__).with_name("release-version.py"))
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class ReleaseVersionTests(unittest.TestCase):
    def test_tag_and_package_version_match(self):
        self.assertEqual(module.resolve("2026.9.1"), ("2026.9.1", "2026.9.1"))
        self.assertEqual(module.resolve("2024.2.2"), ("2024.2.2", "2024.2.2"))
        self.assertEqual(module.resolve("2069.1.10"), ("2069.1.10", "2069.1.10"))

    def test_next_monthly_number(self):
        self.assertEqual(module.next_version("2026.9", []), "2026.9.1")
        self.assertEqual(module.next_version("2026.9", ["2026.9.2", "2026.9.10", "2026.8.99"]), "2026.9.11")

    def test_month_and_year_rollover_reset_number(self):
        self.assertEqual(module.next_version("2026.10", ["2026.9.99"]), "2026.10.1")
        self.assertEqual(module.next_version("2027.1", ["2026.12.99", "2026.1.9"]), "2027.1.1")

    def test_invalid_dates_and_injection_are_rejected(self):
        for value in ("2026.9.06.1", "2026.13.1", "2026.0.1", "2026.09.1", "1.0.0", "v2026.9.1", "2026.9\ntag=bad", "$(id)", "2026.9.0", "2026.9.01", "2026.9.65535", "2026-09-06.1", "26.13.1", "26.9.1"):
            with self.subTest(value=value), self.assertRaises(ValueError):
                module.resolve(value)


if __name__ == "__main__":
    unittest.main()
