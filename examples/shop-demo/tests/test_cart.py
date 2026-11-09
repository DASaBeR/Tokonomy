import unittest

from shop.cart import Cart


class CartTests(unittest.TestCase):
    def test_subtotal_and_total(self):
        cart = Cart()
        cart.add("pen", 2.50, 4)
        cart.add("book", 12.00)
        self.assertEqual(cart.subtotal(), 22.0)
        self.assertEqual(cart.total(), 22.0)

    def test_sale_cart(self):
        cart = Cart(on_sale=True)
        cart.add("book", 10.00, 2)
        self.assertEqual(cart.subtotal(), 16.0)

    def test_percent_coupon(self):
        cart = Cart()
        cart.add("book", 20.00)
        self.assertEqual(cart.total({"type": "percent", "value": 25}), 15.0)

    def test_fixed_coupon(self):
        cart = Cart()
        cart.add("book", 20.00)
        self.assertEqual(cart.total({"type": "fixed", "value": 5}), 15.0)


if __name__ == "__main__":
    unittest.main()
