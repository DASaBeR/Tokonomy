"""Hidden acceptance test: copied in only after the run, never shown to workers."""

import unittest

from app.auth import hash_password, login


class UsernameNormalizationAcceptance(unittest.TestCase):
    def setUp(self):
        self.users = {"alice": hash_password("pw")}

    def test_case_insensitive(self):
        self.assertEqual(login("Alice", "pw", self.users), "ok")
        self.assertEqual(login("ALICE", "pw", self.users), "ok")

    def test_surrounding_whitespace_ignored(self):
        self.assertEqual(login("  alice ", "pw", self.users), "ok")

    def test_password_is_not_normalized(self):
        self.assertEqual(login("alice", " pw", self.users), "denied")


if __name__ == "__main__":
    unittest.main()
