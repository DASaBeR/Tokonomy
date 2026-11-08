"""Hidden acceptance test: copied in only after the run, never shown to workers."""

import unittest

from app.auth import hash_password, login
from app.ratelimit import RateLimiter


class FakeClock:
    def __init__(self):
        self.now = 1000.0

    def __call__(self):
        return self.now


class RateLimitAcceptance(unittest.TestCase):
    def setUp(self):
        self.clock = FakeClock()
        self.users = {"alice": hash_password("pw")}
        self.limiter = RateLimiter(max_attempts=3, window_seconds=60, clock=self.clock)

    def test_locks_after_max_failed_attempts(self):
        for _ in range(3):
            self.assertEqual(login("alice", "bad", self.users, limiter=self.limiter), "denied")
        self.assertEqual(login("alice", "pw", self.users, limiter=self.limiter), "locked")

    def test_window_expires(self):
        for _ in range(3):
            login("alice", "bad", self.users, limiter=self.limiter)
        self.clock.now += 61
        self.assertEqual(login("alice", "pw", self.users, limiter=self.limiter), "ok")

    def test_limits_are_per_user(self):
        self.users["bob"] = hash_password("pw2")
        for _ in range(3):
            login("alice", "bad", self.users, limiter=self.limiter)
        self.assertEqual(login("bob", "pw2", self.users, limiter=self.limiter), "ok")

    def test_login_still_works_without_limiter(self):
        self.assertEqual(login("alice", "pw", self.users), "ok")

    def test_no_module_level_state(self):
        other = RateLimiter(max_attempts=3, window_seconds=60, clock=self.clock)
        for _ in range(3):
            login("alice", "bad", self.users, limiter=self.limiter)
        self.assertEqual(login("alice", "pw", self.users, limiter=other), "ok")


if __name__ == "__main__":
    unittest.main()
