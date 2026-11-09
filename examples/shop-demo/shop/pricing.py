"""Pricing rules: sale prices and coupons."""

from shop.money import round2

SALE_PERCENT = 20


def sale_price(unit_price: float) -> float:
    """Price of one unit while the sale is on."""
    return round2(unit_price * (100 - SALE_PERCENT) / 100)


def apply_coupon(amount: float, coupon):
    """Apply one coupon to an amount.

    A coupon is None, {"type": "percent", "value": 15} or {"type": "fixed", "value": 5}.
    """
    if coupon is None:
        return amount
    if coupon["type"] == "percent":
        return round2(amount * (100 - coupon["value"]) / 100)
    if coupon["type"] == "fixed":
        return round2(amount - coupon["value"])
    raise ValueError(f"unknown coupon type: {coupon['type']}")
