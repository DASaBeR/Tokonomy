"""Hidden acceptance test: copied in only after the run, never shown to workers."""

import unittest

from shop.cart import Cart
from shop.pricing import sale_price


class SaleRoundingAcceptance(unittest.TestCase):
    def test_line_rounded_once_not_per_unit(self):
        cart = Cart(on_sale=True)
        cart.add("a", 0.99, 3)
        self.assertEqual(cart.line_total(cart.items[0]), 2.38)  # per-unit rounding would give 2.37
        self.assertEqual(cart.subtotal(), 2.38)

    def test_other_quantities(self):
        cart = Cart(on_sale=True)
        cart.add("a", 0.33, 5)  # 1.32, per-unit rounding gives 1.30
        self.assertEqual(cart.subtotal(), 1.32)

    def test_single_units_unchanged(self):
        cart = Cart(on_sale=True)
        cart.add("a", 0.99, 1)
        self.assertEqual(cart.subtotal(), 0.79)
        self.assertEqual(sale_price(0.99), 0.79)

    def test_subtotal_sums_lines(self):
        cart = Cart(on_sale=True)
        cart.add("a", 0.99, 3)
        cart.add("b", 0.99, 1)
        self.assertEqual(cart.subtotal(), 3.17)

    def test_not_on_sale_is_plain_multiplication(self):
        cart = Cart()
        cart.add("a", 0.99, 3)
        self.assertEqual(cart.subtotal(), 2.97)

    def test_exact_multiples_still_exact(self):
        cart = Cart(on_sale=True)
        cart.add("a", 1.15, 7)
        self.assertEqual(cart.subtotal(), 6.44)


if __name__ == "__main__":
    unittest.main()
