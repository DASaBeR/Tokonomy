import unittest

from shop.money import fmt, round2
from shop.pricing import apply_coupon, sale_price


class PricingTests(unittest.TestCase):
    def test_sale_price(self):
        self.assertEqual(sale_price(10.0), 8.0)

    def test_coupons(self):
        self.assertEqual(apply_coupon(100.0, None), 100.0)
        self.assertEqual(apply_coupon(100.0, {"type": "percent", "value": 10}), 90.0)
        self.assertEqual(apply_coupon(100.0, {"type": "fixed", "value": 7}), 93.0)

    def test_unknown_coupon_type(self):
        with self.assertRaises(ValueError):
            apply_coupon(100.0, {"type": "bogus", "value": 1})

    def test_money_helpers(self):
        self.assertEqual(round2(1.005), 1.01)
        self.assertEqual(fmt(1234.5), "$1,234.50")


if __name__ == "__main__":
    unittest.main()
