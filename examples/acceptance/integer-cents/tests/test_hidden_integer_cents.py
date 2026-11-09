"""Hidden acceptance test: copied in only after the run, never shown to workers."""

import unittest

from shop.cart import Cart
from shop.money import from_cents, to_cents


class IntegerCentsAcceptance(unittest.TestCase):
    def test_conversions(self):
        self.assertEqual(to_cents(19.99), 1999)
        self.assertEqual(to_cents(0.1 + 0.2), 30)
        self.assertEqual(to_cents(0), 0)
        self.assertIsInstance(to_cents(5.0), int)
        self.assertEqual(from_cents(1999), 19.99)
        self.assertEqual(from_cents(5), 0.05)

    def test_items_store_integer_cents(self):
        cart = Cart()
        cart.add("a", 19.99, 2)
        item = cart.items[0]
        self.assertEqual(item["unit_price_cents"], 1999)
        self.assertIsInstance(item["unit_price_cents"], int)
        self.assertNotIn("unit_price", item)
        self.assertEqual(item["qty"], 2)

    def test_subtotal_cents_is_exact(self):
        cart = Cart()
        for i in range(10):
            cart.add(f"s{i}", 0.10)
        self.assertEqual(cart.subtotal_cents(), 100)
        self.assertIsInstance(cart.subtotal_cents(), int)
        self.assertEqual(cart.subtotal(), 1.0)

    def test_sale_lines_round_half_up_once_per_line(self):
        cart = Cart(on_sale=True)
        cart.add("a", 0.99, 3)  # 99 * 80 * 3 / 100 = 237.6 -> 238
        cart.add("b", 0.33, 5)  # 33 * 80 * 5 / 100 = 132
        cart.add("c", 0.07, 1)  # 5.6 -> 6
        self.assertEqual(cart.subtotal_cents(), 238 + 132 + 6)
        self.assertEqual(cart.subtotal(), 3.76)

    def test_public_float_api_unchanged(self):
        cart = Cart()
        cart.add("a", 2.50, 4)
        cart.add("b", 12.00)
        self.assertEqual(cart.subtotal(), 22.0)
        self.assertEqual(cart.line_total(cart.items[0]), 10.0)
        self.assertEqual(cart.total({"type": "percent", "value": 25}), 16.5)

    def test_no_float_drift_across_many_lines(self):
        cart = Cart()
        for _ in range(1000):
            cart.add("x", 0.07)
        self.assertEqual(cart.subtotal_cents(), 7000)
        self.assertEqual(cart.subtotal(), 70.0)


if __name__ == "__main__":
    unittest.main()
