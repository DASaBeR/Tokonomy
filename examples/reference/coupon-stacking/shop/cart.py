"""A shopping cart."""

from shop.money import round2
from shop.pricing import apply_coupon, sale_price


class Cart:
    def __init__(self, on_sale: bool = False):
        self.on_sale = on_sale
        self.items = []

    def add(self, sku: str, unit_price: float, qty: int = 1) -> None:
        self.items.append({"sku": sku, "unit_price": unit_price, "qty": qty})

    def line_total(self, item: dict) -> float:
        price = sale_price(item["unit_price"]) if self.on_sale else item["unit_price"]
        return round2(price * item["qty"])

    def subtotal(self) -> float:
        return round2(sum(self.line_total(item) for item in self.items))

    def total(self, coupons=None) -> float:
        if not self.items:
            return 0.0
        if coupons is None:
            coupons = []
        elif isinstance(coupons, dict):
            coupons = [coupons]
        for coupon in coupons:
            if coupon["type"] not in ("percent", "fixed"):
                raise ValueError(f"unknown coupon type: {coupon['type']}")
        percents = [c for c in coupons if c["type"] == "percent"]
        amount = self.subtotal()
        if percents:
            amount = apply_coupon(amount, max(percents, key=lambda c: c["value"]))
        for coupon in coupons:
            if coupon["type"] == "fixed":
                amount = apply_coupon(amount, coupon)
        return max(0.0, amount)
