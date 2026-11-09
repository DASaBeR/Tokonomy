"""A shopping cart. Prices are stored as integer cents."""

from shop.money import from_cents, round2, to_cents
from shop.pricing import SALE_PERCENT, apply_coupon


class Cart:
    def __init__(self, on_sale: bool = False):
        self.on_sale = on_sale
        self.items = []

    def add(self, sku: str, unit_price: float, qty: int = 1) -> None:
        self.items.append({"sku": sku, "unit_price_cents": to_cents(unit_price), "qty": qty})

    def _line_cents(self, item: dict) -> int:
        cents = item["unit_price_cents"] * item["qty"]
        if self.on_sale:
            return (cents * (100 - SALE_PERCENT) + 50) // 100
        return cents

    def line_total(self, item: dict) -> float:
        return from_cents(self._line_cents(item))

    def subtotal_cents(self) -> int:
        return sum(self._line_cents(item) for item in self.items)

    def subtotal(self) -> float:
        return from_cents(self.subtotal_cents())

    def total(self, coupon=None) -> float:
        return apply_coupon(self.subtotal(), coupon)


__all__ = ["Cart", "round2"]
