"""A shopping cart."""

from shop.money import round2
from shop.pricing import SALE_PERCENT, apply_coupon


class Cart:
    def __init__(self, on_sale: bool = False):
        self.on_sale = on_sale
        self.items = []

    def add(self, sku: str, unit_price: float, qty: int = 1) -> None:
        self.items.append({"sku": sku, "unit_price": unit_price, "qty": qty})

    def line_total(self, item: dict) -> float:
        if self.on_sale:
            return round2(item["unit_price"] * (100 - SALE_PERCENT) / 100 * item["qty"])
        return round2(item["unit_price"] * item["qty"])

    def subtotal(self) -> float:
        return round2(sum(self.line_total(item) for item in self.items))

    def total(self, coupon=None) -> float:
        return apply_coupon(self.subtotal(), coupon)
