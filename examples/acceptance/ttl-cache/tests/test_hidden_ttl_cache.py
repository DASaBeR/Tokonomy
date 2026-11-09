"""Hidden acceptance test: copied in only after the run, never shown to workers."""

import random
import sys
import threading
import unittest

from shop.cache import TtlCache


class Clock:
    def __init__(self):
        self.now = 100.0

    def __call__(self):
        return self.now


class TtlCacheAcceptance(unittest.TestCase):
    def setUp(self):
        self.clock = Clock()

    def cache(self, ttl=10, max_size=None):
        return TtlCache(ttl_seconds=ttl, max_size=max_size, clock=self.clock)

    def test_expires_exactly_at_ttl(self):
        cache = self.cache(ttl=10)
        cache.set("a", 1)
        self.clock.now += 9.99
        self.assertEqual(cache.get("a"), 1)
        self.clock.now += 0.01
        self.assertIsNone(cache.get("a"))
        self.assertEqual(cache.get("a", "dflt"), "dflt")
        self.assertNotIn("a", cache)

    def test_set_refreshes_ttl_and_value_but_get_does_not(self):
        cache = self.cache(ttl=10)
        cache.set("a", 1)
        self.clock.now += 6
        cache.get("a")  # reading does not extend the life
        self.clock.now += 5
        self.assertIsNone(cache.get("a"))
        cache.set("b", 1)
        self.clock.now += 6
        cache.set("b", 2)  # writing does
        self.clock.now += 6
        self.assertEqual(cache.get("b"), 2)

    def test_len_counts_only_live_entries(self):
        cache = self.cache(ttl=10)
        cache.set("a", 1)
        self.clock.now += 5
        cache.set("b", 2)
        self.assertEqual(len(cache), 2)
        self.clock.now += 6
        self.assertEqual(len(cache), 1)

    def test_lru_eviction_uses_get_and_set_as_use(self):
        cache = self.cache(max_size=2)
        cache.set("a", 1)
        cache.set("b", 2)
        cache.get("a")  # a is now more recent than b
        cache.set("c", 3)
        self.assertIn("a", cache)
        self.assertNotIn("b", cache)
        self.assertIn("c", cache)

        cache.set("a", 10)  # re-setting counts as use
        cache.set("d", 4)  # evicts c
        self.assertEqual(sorted(k for k in "abcd" if k in cache), ["a", "d"])

    def test_expired_entries_are_purged_before_evicting_live_ones(self):
        cache = self.cache(ttl=10, max_size=2)
        cache.set("a", 1)
        self.clock.now += 5
        cache.set("b", 2)
        cache.get("a")  # a is now the most recently used, so b is the LRU entry
        self.clock.now += 6  # a is expired (age 11), b is alive (age 6)
        cache.set("c", 3)
        # Dropping expired a leaves room: b must NOT be evicted just because it is the LRU entry.
        self.assertIn("b", cache)
        self.assertIn("c", cache)
        self.assertEqual(len(cache), 2)

    def test_updating_existing_key_at_capacity_does_not_evict(self):
        cache = self.cache(max_size=2)
        cache.set("a", 1)
        cache.set("b", 2)
        cache.set("a", 9)
        self.assertEqual((cache.get("a"), cache.get("b")), (9, 2))

    def test_delete(self):
        cache = self.cache(ttl=10)
        cache.set("a", 1)
        self.assertTrue(cache.delete("a"))
        self.assertFalse(cache.delete("a"))
        cache.set("b", 1)
        self.clock.now += 11
        self.assertFalse(cache.delete("b"))  # already expired

    def test_instances_are_isolated(self):
        one, two = self.cache(), self.cache()
        one.set("k", 1)
        self.assertIsNone(two.get("k"))
        self.assertEqual(len(two), 0)

    def test_none_values_are_stored(self):
        cache = self.cache()
        cache.set("a", None)
        self.assertIn("a", cache)
        self.assertEqual(cache.get("a", "dflt"), None)

    def test_thread_safety_under_contention(self):
        cache = TtlCache(ttl_seconds=0.001, max_size=50)  # real clock, entries expire constantly
        errors = []

        def worker(seed):
            rng = random.Random(seed)
            try:
                for _ in range(1500):
                    key = rng.randrange(120)
                    op = rng.random()
                    if op < 0.5:
                        cache.set(key, key)
                    elif op < 0.9:
                        cache.get(key)
                    else:
                        cache.delete(key)
                    self.assertLessEqual(len(cache), 50)
            except Exception as exc:  # noqa: BLE001
                errors.append(repr(exc))

        # Force very frequent thread switches so unsynchronised access shows up reliably.
        previous = sys.getswitchinterval()
        sys.setswitchinterval(1e-6)
        try:
            threads = [threading.Thread(target=worker, args=(i,)) for i in range(12)]
            for t in threads:
                t.start()
            for t in threads:
                t.join()
        finally:
            sys.setswitchinterval(previous)
        self.assertEqual(errors, [])


if __name__ == "__main__":
    unittest.main()
