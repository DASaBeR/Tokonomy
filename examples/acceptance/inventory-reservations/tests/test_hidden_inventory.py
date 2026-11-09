"""Hidden acceptance test: copied in only after the run, never shown to workers."""

import random
import unittest

from shop.inventory import Inventory


class Clock:
    def __init__(self):
        self.now = 1000.0

    def __call__(self):
        return self.now


class InventoryAcceptance(unittest.TestCase):
    def setUp(self):
        self.clock = Clock()
        self.inv = Inventory(self.clock)

    def stock(self, **kw):
        for sku, qty in kw.items():
            self.inv.receive(sku, qty)

    def check(self, sku, on_hand, reserved):
        self.assertEqual(self.inv.on_hand(sku), on_hand)
        self.assertEqual(self.inv.reserved(sku), reserved)
        self.assertEqual(self.inv.available(sku), on_hand - reserved)

    # ---- basics / validation
    def test_unknown_sku_and_receive(self):
        self.check("zz", 0, 0)
        self.assertIsNone(self.inv.receive("a", 5))
        self.inv.receive("a", 2)
        self.check("a", 7, 0)

    def test_validation(self):
        for bad in (0, -1, 1.0, "3", None, True):
            with self.assertRaises(ValueError):
                self.inv.receive("a", bad)
        self.check("a", 0, 0)
        self.stock(a=5)
        for bad_lines in ([], [("a", 0)], [("a", True)], [("a", 1.5)], [("a", 1), ("b", -2)]):
            with self.assertRaises(ValueError):
                self.inv.reserve("o1", bad_lines, 10)
        for bad_ttl in (0, -1, 0.0, True):
            with self.assertRaises(ValueError):
                self.inv.reserve("o1", [("a", 1)], bad_ttl)
        self.check("a", 5, 0)
        self.assertEqual(self.inv.confirm("o1").code, "unknown_order")
        self.assertTrue(self.inv.reserve("o1", [("a", 1)], 0.5).ok)

    def test_validation_before_key_replay(self):
        self.stock(a=5)
        self.assertTrue(self.inv.reserve("o1", [("a", 1)], 10, key="k").ok)
        with self.assertRaises(ValueError):
            self.inv.reserve("o1", [("a", 0)], 10, key="k")
        with self.assertRaises(ValueError):
            self.inv.reserve("o1", [("a", 1)], 0, key="k")
        self.check("a", 5, 1)

    def test_success_result_shape(self):
        self.stock(a=5)
        r = self.inv.reserve("o1", [("a", 2)], 10)
        self.assertIs(r.ok, True)
        self.assertIsNone(r.code)
        self.assertEqual(r.shortfalls, {})
        self.check("a", 5, 2)

    # ---- merge + atomicity + shortfalls
    def test_duplicate_lines_merged(self):
        self.stock(a=5)
        r = self.inv.reserve("o1", [("a", 3), ("a", 3)], 10)
        self.assertFalse(r.ok)
        self.assertEqual(r.code, "insufficient")
        self.assertEqual(r.shortfalls, {"a": 1})
        self.check("a", 5, 0)
        r = self.inv.reserve("o1", [("a", 2), ("a", 3)], 10)
        self.assertTrue(r.ok)
        self.check("a", 5, 5)
        self.assertEqual(self.inv.available("a"), 0)

    def test_all_or_nothing(self):
        self.stock(a=5, b=1, c=10)
        r = self.inv.reserve("o1", [("c", 4), ("a", 6), ("b", 1)], 10)
        self.assertFalse(r.ok)
        self.assertEqual(r.code, "insufficient")
        self.assertEqual(r.shortfalls, {"a": 1})
        self.check("a", 5, 0)
        self.check("b", 1, 0)
        self.check("c", 10, 0)
        # failed order leaves no record
        self.assertEqual(self.inv.confirm("o1").code, "unknown_order")
        self.assertEqual(self.inv.cancel("o1").code, "unknown_order")

    def test_shortfalls_sorted_and_complete(self):
        self.stock(b=2, a=1)
        self.inv.reserve("hold", [("b", 1)], 100)
        r = self.inv.reserve("o1", [("z", 4), ("b", 3), ("a", 1), ("a", 1), ("m", 1)], 10)
        self.assertEqual(r.code, "insufficient")
        self.assertEqual(r.shortfalls, {"a": 1, "b": 2, "m": 1, "z": 4})
        self.assertEqual(list(r.shortfalls), ["a", "b", "m", "z"])
        self.assertNotIn("c", r.shortfalls)
        self.check("b", 2, 1)

    def test_shortfall_accounts_for_reserved_not_on_hand(self):
        self.stock(a=10)
        self.inv.reserve("o1", [("a", 7)], 100)
        r = self.inv.reserve("o2", [("a", 5)], 100)
        self.assertEqual(r.shortfalls, {"a": 2})
        r = self.inv.reserve("o2", [("a", 3)], 100)
        self.assertTrue(r.ok)
        self.check("a", 10, 10)

    def test_exact_availability_ok(self):
        self.stock(a=4)
        self.assertTrue(self.inv.reserve("o1", [("a", 4)], 5).ok)
        r = self.inv.reserve("o2", [("a", 1)], 5)
        self.assertEqual(r.shortfalls, {"a": 1})

    def test_receive_does_not_fulfil(self):
        self.stock(a=1)
        r = self.inv.reserve("o1", [("a", 3)], 10)
        self.assertFalse(r.ok)
        self.inv.receive("a", 10)
        self.check("a", 11, 0)
        self.assertEqual(self.inv.confirm("o1").code, "unknown_order")
        self.check("a", 11, 0)

    # ---- expiry
    def test_expiry_boundary(self):
        self.stock(a=5)
        self.inv.reserve("o1", [("a", 5)], 10)
        self.clock.now += 9.999
        self.check("a", 5, 5)
        self.clock.now = 1010.0
        self.check("a", 5, 0)

    def test_expiry_boundary_float_ttl(self):
        self.stock(a=5)
        self.clock.now = 10.5
        self.inv.reserve("o1", [("a", 2)], 0.25)
        self.clock.now = 10.74
        self.assertEqual(self.inv.reserved("a"), 2)
        self.clock.now = 10.75
        self.assertEqual(self.inv.reserved("a"), 0)

    def test_ttl_measured_from_reserve_time(self):
        self.stock(a=5)
        self.clock.now += 50
        self.inv.reserve("o1", [("a", 1)], 10)
        self.clock.now += 9
        self.assertEqual(self.inv.reserved("a"), 1)
        self.clock.now += 1
        self.assertEqual(self.inv.reserved("a"), 0)

    def test_expiry_frees_stock_for_reserve_in_same_call(self):
        self.stock(a=5)
        self.assertTrue(self.inv.reserve("o1", [("a", 5)], 10).ok)
        self.clock.now += 10  # exactly at boundary, no other call made before reserve
        r = self.inv.reserve("o2", [("a", 5)], 10)
        self.assertTrue(r.ok)
        self.check("a", 5, 5)

    def test_not_yet_expired_blocks(self):
        self.stock(a=5)
        self.inv.reserve("o1", [("a", 5)], 10)
        self.clock.now += 9.5
        r = self.inv.reserve("o2", [("a", 1)], 10)
        self.assertEqual(r.shortfalls, {"a": 1})

    def test_expiry_via_every_method(self):
        for name in ("receive", "on_hand", "reserved", "available", "cancel", "confirm", "reserve"):
            clock = Clock()
            inv = Inventory(clock)
            inv.receive("a", 5)
            inv.reserve("o1", [("a", 5)], 5)
            clock.now += 5
            if name == "receive":
                inv.receive("a", 1)
            elif name == "reserve":
                self.assertTrue(inv.reserve("o9", [("a", 5)], 5).ok)
            elif name in ("cancel", "confirm"):
                getattr(inv, name)("o1")
            else:
                getattr(inv, name)("a")
            if name != "reserve":
                self.assertEqual(inv.reserved("a"), 0, name)

    def test_expired_order_can_be_reserved_again(self):
        self.stock(a=5)
        self.inv.reserve("o1", [("a", 2)], 10)
        self.clock.now += 10
        r = self.inv.reserve("o1", [("a", 3)], 10)
        self.assertTrue(r.ok)
        self.check("a", 5, 3)
        self.assertTrue(self.inv.confirm("o1").ok)
        self.check("a", 2, 0)

    def test_expired_reservation_independent_of_other_orders(self):
        self.stock(a=10)
        self.inv.reserve("short", [("a", 3)], 5)
        self.inv.reserve("long", [("a", 4)], 50)
        self.clock.now += 5
        self.check("a", 10, 4)
        self.assertEqual(self.inv.confirm("short").code, "expired")
        self.assertTrue(self.inv.confirm("long").ok)
        self.check("a", 6, 0)

    # ---- duplicate order
    def test_duplicate_order(self):
        self.stock(a=10)
        self.assertTrue(self.inv.reserve("o1", [("a", 2)], 10).ok)
        r = self.inv.reserve("o1", [("a", 1)], 10)
        self.assertFalse(r.ok)
        self.assertEqual(r.code, "duplicate_order")
        self.assertEqual(r.shortfalls, {})
        self.check("a", 10, 2)

    def test_duplicate_order_checked_before_stock(self):
        self.stock(a=2)
        self.inv.reserve("o1", [("a", 2)], 10)
        r = self.inv.reserve("o1", [("a", 5)], 10)
        self.assertEqual(r.code, "duplicate_order")
        self.assertEqual(r.shortfalls, {})

    def test_duplicate_order_after_confirm(self):
        self.stock(a=10)
        self.inv.reserve("o1", [("a", 2)], 10)
        self.inv.confirm("o1")
        self.assertEqual(self.inv.reserve("o1", [("a", 1)], 10).code, "duplicate_order")
        self.clock.now += 100
        self.assertEqual(self.inv.reserve("o1", [("a", 1)], 10).code, "duplicate_order")
        self.check("a", 8, 0)

    def test_reserve_again_after_cancel(self):
        self.stock(a=10)
        self.inv.reserve("o1", [("a", 2)], 10)
        self.inv.cancel("o1")
        self.check("a", 10, 0)
        self.assertTrue(self.inv.reserve("o1", [("a", 4)], 10).ok)
        self.check("a", 10, 4)

    def test_duplicate_order_not_remembered_as_key(self):
        self.stock(a=10)
        self.inv.reserve("o1", [("a", 2)], 10)
        r = self.inv.reserve("o1", [("a", 2)], 10, key="k1")
        self.assertEqual(r.code, "duplicate_order")
        self.inv.cancel("o1")
        # key k1 was never recorded, so this is a fresh call that succeeds
        self.assertTrue(self.inv.reserve("o1", [("a", 3)], 10, key="k1").ok)
        self.check("a", 10, 3)

    # ---- idempotency key
    def test_key_replay_returns_original_without_reserving(self):
        self.stock(a=10)
        first = self.inv.reserve("o1", [("a", 3)], 10, key="k")
        again = self.inv.reserve("o1", [("a", 3)], 10, key="k")
        self.assertTrue(again.ok)
        self.assertEqual(again, first)
        self.check("a", 10, 3)

    def test_key_replay_ignores_line_order_merge_and_ttl(self):
        self.stock(a=10, b=10)
        first = self.inv.reserve("o1", [("a", 1), ("b", 2), ("a", 1)], 10, key="k")
        again = self.inv.reserve("o1", [("b", 2), ("a", 2)], 999, key="k")
        self.assertEqual(again, first)
        self.assertTrue(again.ok)
        self.check("a", 10, 2)
        self.check("b", 10, 2)
        self.clock.now += 10  # original ttl governs
        self.check("a", 10, 0)

    def test_key_replay_after_cancel_does_not_reserve(self):
        self.stock(a=10)
        self.inv.reserve("o1", [("a", 3)], 10, key="k")
        self.inv.cancel("o1")
        r = self.inv.reserve("o1", [("a", 3)], 10, key="k")
        self.assertTrue(r.ok)
        self.check("a", 10, 0)

    def test_key_replay_after_expiry_and_confirm(self):
        self.stock(a=10)
        self.inv.reserve("o1", [("a", 3)], 10, key="k")
        self.clock.now += 10
        r = self.inv.reserve("o1", [("a", 3)], 10, key="k")
        self.assertTrue(r.ok)
        self.check("a", 10, 0)
        self.inv.reserve("o2", [("a", 3)], 10, key="k2")
        self.inv.confirm("o2")
        r = self.inv.reserve("o2", [("a", 3)], 10, key="k2")
        self.assertTrue(r.ok)
        self.check("a", 7, 0)

    def test_key_replay_of_failure_returns_failure(self):
        self.stock(a=1)
        first = self.inv.reserve("o1", [("a", 3), ("b", 1)], 10, key="k")
        self.assertEqual(first.shortfalls, {"a": 2, "b": 1})
        self.inv.receive("a", 100)
        self.inv.receive("b", 100)
        again = self.inv.reserve("o1", [("a", 3), ("b", 1)], 10, key="k")
        self.assertEqual(again, first)
        self.assertFalse(again.ok)
        self.check("a", 101, 0)
        # without the key the same call now works
        self.assertTrue(self.inv.reserve("o1", [("a", 3), ("b", 1)], 10).ok)

    def test_key_conflict(self):
        self.stock(a=10, b=10)
        self.inv.reserve("o1", [("a", 3)], 10, key="k")
        for order, lines in (("o1", [("a", 4)]), ("o1", [("b", 3)]), ("o1", [("a", 3), ("b", 1)]), ("o2", [("a", 3)])):
            r = self.inv.reserve(order, lines, 10, key="k")
            self.assertFalse(r.ok)
            self.assertEqual(r.code, "key_conflict")
            self.assertEqual(r.shortfalls, {})
        self.check("a", 10, 3)
        self.check("b", 10, 0)
        self.assertEqual(self.inv.confirm("o2").code, "unknown_order")

    def test_key_conflict_takes_priority_over_duplicate_order(self):
        self.stock(a=10)
        self.inv.reserve("o1", [("a", 3)], 10, key="k")
        self.assertEqual(self.inv.reserve("o1", [("a", 2)], 10, key="k").code, "key_conflict")

    def test_key_replay_takes_priority_over_duplicate_order(self):
        self.stock(a=10)
        first = self.inv.reserve("o1", [("a", 3)], 10, key="k")
        self.assertEqual(self.inv.reserve("o1", [("a", 3)], 10, key="k"), first)

    def test_key_conflict_not_remembered(self):
        self.stock(a=10)
        self.inv.reserve("o1", [("a", 3)], 10, key="k")
        self.inv.reserve("o9", [("a", 1)], 10, key="k")  # conflict, must not rebind k
        r = self.inv.reserve("o1", [("a", 3)], 10, key="k")
        self.assertTrue(r.ok)
        self.assertEqual(self.inv.reserved("a"), 3)

    def test_distinct_keys_independent(self):
        self.stock(a=10)
        self.assertTrue(self.inv.reserve("o1", [("a", 1)], 10, key="k1").ok)
        self.assertTrue(self.inv.reserve("o2", [("a", 1)], 10, key="k2").ok)
        self.assertTrue(self.inv.reserve("o3", [("a", 1)], 10, key=None).ok)
        self.assertTrue(self.inv.reserve("o4", [("a", 1)], 10, key=None).ok)
        self.check("a", 10, 4)

    # ---- confirm
    def test_confirm_decrements_and_is_idempotent(self):
        self.stock(a=10, b=4)
        self.inv.reserve("o1", [("a", 3), ("b", 4)], 10)
        r = self.inv.confirm("o1")
        self.assertTrue(r.ok)
        self.assertIsNone(r.code)
        self.assertEqual(r.shortfalls, {})
        self.check("a", 7, 0)
        self.check("b", 0, 0)
        r2 = self.inv.confirm("o1")
        self.assertEqual(r2, r)
        self.check("a", 7, 0)
        self.check("b", 0, 0)

    def test_confirm_after_ttl_when_already_confirmed(self):
        self.stock(a=10)
        self.inv.reserve("o1", [("a", 3)], 10)
        self.inv.confirm("o1")
        self.clock.now += 1000
        self.assertTrue(self.inv.confirm("o1").ok)
        self.check("a", 7, 0)

    def test_confirm_at_expiry_boundary_fails(self):
        self.stock(a=10)
        self.inv.reserve("o1", [("a", 3)], 10)
        self.clock.now += 10
        r = self.inv.confirm("o1")
        self.assertFalse(r.ok)
        self.assertEqual(r.code, "expired")
        self.assertEqual(r.shortfalls, {})
        self.check("a", 10, 0)
        r = self.inv.confirm("o1")
        self.assertEqual(r.code, "expired")
        self.check("a", 10, 0)

    def test_confirm_just_before_expiry_succeeds(self):
        self.stock(a=10)
        self.inv.reserve("o1", [("a", 3)], 10)
        self.clock.now += 9.9
        self.assertTrue(self.inv.confirm("o1").ok)
        self.check("a", 7, 0)

    def test_confirm_unknown_and_cancelled(self):
        self.stock(a=10)
        self.assertEqual(self.inv.confirm("nope").code, "unknown_order")
        self.inv.reserve("o1", [("a", 3)], 10)
        self.inv.cancel("o1")
        r = self.inv.confirm("o1")
        self.assertFalse(r.ok)
        self.assertEqual(r.code, "cancelled")
        self.check("a", 10, 0)

    def test_confirmed_stock_not_available_again(self):
        self.stock(a=5)
        self.inv.reserve("o1", [("a", 5)], 10)
        self.inv.confirm("o1")
        r = self.inv.reserve("o2", [("a", 1)], 10)
        self.assertEqual(r.shortfalls, {"a": 1})

    # ---- cancel
    def test_cancel_semantics(self):
        self.stock(a=10)
        self.inv.reserve("o1", [("a", 3)], 10)
        r = self.inv.cancel("o1")
        self.assertTrue(r.ok)
        self.assertIsNone(r.code)
        self.check("a", 10, 0)
        self.assertTrue(self.inv.cancel("o1").ok)
        self.check("a", 10, 0)

    def test_cancel_unknown(self):
        r = self.inv.cancel("ghost")
        self.assertFalse(r.ok)
        self.assertEqual(r.code, "unknown_order")
        self.assertEqual(r.shortfalls, {})

    def test_cancel_confirmed(self):
        self.stock(a=10)
        self.inv.reserve("o1", [("a", 3)], 10)
        self.inv.confirm("o1")
        r = self.inv.cancel("o1")
        self.assertFalse(r.ok)
        self.assertEqual(r.code, "already_confirmed")
        self.check("a", 7, 0)

    def test_cancel_expired_is_ok_and_stays_expired(self):
        self.stock(a=10)
        self.inv.reserve("o1", [("a", 3)], 10)
        self.clock.now += 10
        r = self.inv.cancel("o1")
        self.assertTrue(r.ok)
        self.check("a", 10, 0)
        self.assertEqual(self.inv.confirm("o1").code, "expired")

    def test_cancel_frees_stock_for_others(self):
        self.stock(a=3)
        self.inv.reserve("o1", [("a", 3)], 10)
        self.assertFalse(self.inv.reserve("o2", [("a", 1)], 10).ok)
        self.inv.cancel("o1")
        self.assertTrue(self.inv.reserve("o2", [("a", 3)], 10).ok)

    # ---- randomized against an independent model
    def test_random_sequence_matches_model(self):
        rng = random.Random(1234)
        clock = Clock()
        clock.now = 0.0
        inv = Inventory(clock)
        skus = ["a", "b", "c"]
        orders = ["o%d" % i for i in range(6)]
        keys = [None, None, "k1", "k2", "k3"]

        # model: plain lists, recomputed from scratch each time
        stock = {s: 0 for s in skus}
        resv = []  # dicts: order, lines{sku:qty}, expires, state
        seen_keys = {}  # key -> (payload, (ok, code, shortfalls))

        def m_sweep():
            for r in resv:
                if r["state"] == "held" and clock.now >= r["expires"]:
                    r["state"] = "expired"

        def m_latest(order):
            for r in reversed(resv):
                if r["order"] == order:
                    return r
            return None

        def m_reserved(s):
            return sum(r["lines"].get(s, 0) for r in resv if r["state"] == "held")

        confirmed_ok = 0
        for step in range(200):
            op = rng.choice(["receive", "reserve", "reserve", "reserve", "confirm", "cancel", "tick", "tick"])
            if op == "tick":
                clock.now += rng.choice([0, 1, 1, 2, 3, 5])
                continue
            m_sweep()
            if op == "receive":
                s, q = rng.choice(skus), rng.randint(1, 6)
                inv.receive(s, q)
                stock[s] += q
            elif op == "reserve":
                order = rng.choice(orders)
                lines = [(rng.choice(skus), rng.randint(1, 4)) for _ in range(rng.randint(1, 3))]
                ttl = rng.choice([1, 2, 3, 5, 8])
                key = rng.choice(keys)
                got = inv.reserve(order, lines, ttl, key=key)
                merged = {}
                for s, q in lines:
                    merged[s] = merged.get(s, 0) + q
                payload = (order, sorted(merged.items()))
                if key is not None and key in seen_keys:
                    p, res = seen_keys[key]
                    exp = res if p == payload else (False, "key_conflict", {})
                else:
                    cur = m_latest(order)
                    if cur is not None and cur["state"] in ("held", "confirmed"):
                        exp = (False, "duplicate_order", {})
                    else:
                        short = {s: q - (stock[s] - m_reserved(s)) for s, q in merged.items() if q > stock[s] - m_reserved(s)}
                        if short:
                            exp = (False, "insufficient", short)
                        else:
                            exp = (True, None, {})
                            resv.append({"order": order, "lines": merged, "expires": clock.now + ttl, "state": "held"})
                        if key is not None:
                            seen_keys[key] = (payload, exp)
                self.assertEqual((got.ok, got.code, got.shortfalls), exp, "step %d" % step)
                if exp[1] == "insufficient":
                    self.assertEqual(list(got.shortfalls), sorted(got.shortfalls))
            elif op == "confirm":
                order = rng.choice(orders)
                got = inv.confirm(order)
                cur = m_latest(order)
                if cur is None:
                    exp = (False, "unknown_order")
                elif cur["state"] == "cancelled":
                    exp = (False, "cancelled")
                elif cur["state"] == "expired":
                    exp = (False, "expired")
                else:
                    if cur["state"] == "held":
                        for s, q in cur["lines"].items():
                            stock[s] -= q
                        cur["state"] = "confirmed"
                        confirmed_ok += 1
                    exp = (True, None)
                self.assertEqual((got.ok, got.code), exp, "step %d" % step)
            else:
                order = rng.choice(orders)
                got = inv.cancel(order)
                cur = m_latest(order)
                if cur is None:
                    exp = (False, "unknown_order")
                elif cur["state"] == "confirmed":
                    exp = (False, "already_confirmed")
                else:
                    if cur["state"] == "held":
                        cur["state"] = "cancelled"
                    exp = (True, None)
                self.assertEqual((got.ok, got.code), exp, "step %d" % step)

            m_sweep()
            for s in skus:
                self.assertGreaterEqual(inv.on_hand(s), 0)
                self.assertGreaterEqual(inv.reserved(s), 0)
                self.assertEqual(inv.on_hand(s), stock[s], "step %d %s" % (step, s))
                self.assertEqual(inv.reserved(s), m_reserved(s), "step %d %s" % (step, s))
                self.assertEqual(inv.available(s), inv.on_hand(s) - inv.reserved(s))
        self.assertGreater(confirmed_ok, 0)


if __name__ == "__main__":
    unittest.main()
