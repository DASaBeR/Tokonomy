"""Hidden acceptance test: copied in only after the run, never shown to workers."""

import unittest

from app.passwords import validate_password


class PasswordPolicyAcceptance(unittest.TestCase):
    def test_strong_password_has_no_errors(self):
        self.assertEqual(validate_password("Correct-Horse-42"), [])

    def test_reports_each_problem(self):
        errors = validate_password("short")
        self.assertIn("too_short", errors)
        self.assertIn("no_digit", errors)
        self.assertIn("no_uppercase", errors)

    def test_length_boundary_is_twelve(self):
        self.assertNotIn("too_short", validate_password("Abcdefghij12"))
        self.assertIn("too_short", validate_password("Abcdefghi12"))

    def test_error_order_is_stable(self):
        self.assertEqual(validate_password("abc"), ["too_short", "no_digit", "no_uppercase"])


if __name__ == "__main__":
    unittest.main()
