"""Hidden acceptance test: copied in only after the run, never shown to workers."""

import unittest

from shop.cart import Cart


def cart_of(*prices):
    cart = Cart()
    for i, price in enumerate(prices):
        cart.add(f"sku{i}", price)
    return cart


PCT = lambda v: {"type": "percent", "value": v}  # noqa: E731
FIX = lambda v: {"type": "fixed", "value": v}  # noqa: E731


class CouponStackingAcceptance(unittest.TestCase):
    def test_best_percent_then_all_fixed(self):
        # best percent (20) applies once: 80; then fixed 5 and 3: 72
        self.assertEqual(cart_of(100.0).total([PCT(10), PCT(20), FIX(5), FIX(3)]), 72.0)

    def test_percent_before_fixed_regardless_of_order(self):
        # (100 * 0.9) - 5 = 85, not (100 - 5) * 0.9 = 85.5
        self.assertEqual(cart_of(100.0).total([FIX(5), PCT(10)]), 85.0)

    def test_never_below_zero(self):
        self.assertEqual(cart_of(10.0).total([FIX(15)]), 0.0)
        self.assertEqual(cart_of(10.0).total([PCT(50), FIX(4), FIX(4)]), 0.0)

    def test_empty_cart_is_zero_whatever_the_coupons(self):
        self.assertEqual(Cart().total([FIX(5)]), 0.0)
        self.assertEqual(Cart().total(), 0.0)
        self.assertEqual(Cart().total([{"type": "bogus", "value": 1}]), 0.0)  # not even validated

    def test_single_dict_and_none_still_work(self):
        cart = cart_of(40.0)
        self.assertEqual(cart.total(PCT(25)), 30.0)
        self.assertEqual(cart.total(FIX(5)), 35.0)
        self.assertEqual(cart.total(None), 40.0)
        self.assertEqual(cart.total([]), 40.0)

    def test_unknown_type_raises(self):
        with self.assertRaises(ValueError):
            cart_of(10.0).total([{"type": "bogus", "value": 1}])

    def test_input_list_is_not_modified(self):
        coupons = [FIX(5), PCT(10), PCT(20)]
        snapshot = [dict(c) for c in coupons]
        cart_of(100.0).total(coupons)
        self.assertEqual(coupons, snapshot)


if __name__ == "__main__":
    unittest.main()
