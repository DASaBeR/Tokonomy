"""Money helpers. Amounts are floats in dollars; exact arithmetic uses integer cents."""

import math


def round2(amount: float) -> float:
    """Round to cents (half up, tolerant of float error)."""
    return round(amount + 1e-9, 2)


def to_cents(amount: float) -> int:
    """Dollars to integer cents, rounding half up."""
    return math.floor(amount * 100 + 0.5 + 1e-9)


def from_cents(cents: int) -> float:
    return cents / 100


def fmt(amount: float) -> str:
    return f"${amount:,.2f}"
