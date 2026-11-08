import unittest

from app.auth import hash_password, login


class LoginTests(unittest.TestCase):
    def setUp(self):
        self.users = {"alice": hash_password("correct horse")}

    def test_valid_password(self):
        self.assertEqual(login("alice", "correct horse", self.users), "ok")

    def test_wrong_password(self):
        self.assertEqual(login("alice", "nope", self.users), "denied")

    def test_unknown_user(self):
        self.assertEqual(login("bob", "x", self.users), "denied")


if __name__ == "__main__":
    unittest.main()
