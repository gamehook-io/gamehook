import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location("release_version", Path(__file__).with_name("release-version.py"))
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class ReleaseVersionTests(unittest.TestCase):
    def test_date_and_numeric_package_version(self):
        self.assertEqual(module.resolve("26.09.1"), ("26.09.1", "2026.9.1"))
        self.assertEqual(module.resolve("24.02.2"), ("24.02.2", "2024.2.2"))
        self.assertEqual(module.resolve("69.01.10"), ("69.01.10", "2069.1.10"))

    def test_next_monthly_number(self):
        self.assertEqual(module.next_version("26.09", []), "26.09.1")
        self.assertEqual(module.next_version("26.09", ["26.09.2", "26.09.10", "26.08.99"]), "26.09.11")

    def test_month_and_year_rollover_reset_number(self):
        self.assertEqual(module.next_version("26.10", ["26.09.99"]), "26.10.1")
        self.assertEqual(module.next_version("27.01", ["26.12.99", "26.01.9"]), "27.01.1")

    def test_invalid_dates_and_injection_are_rejected(self):
        for value in ("26.09.06.1", "26.13.1", "26.00.1", "26.9.1", "1.0.0", "v26.09.1", "26.09\ntag=bad", "$(id)", "26.09.0", "26.09.01", "26.09.65535", "26-09-06.1"):
            with self.subTest(value=value), self.assertRaises(ValueError):
                module.resolve(value)


if __name__ == "__main__":
    unittest.main()
