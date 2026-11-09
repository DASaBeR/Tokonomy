"""Money helpers. Amounts are floats in dollars."""


def round2(amount: float) -> float:
    """Round to cents (half up, tolerant of float error)."""
    return round(amount + 1e-9, 2)


def fmt(amount: float) -> str:
    return f"${amount:,.2f}"
