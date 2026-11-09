"""Shipping estimates."""

from datetime import date, datetime, timedelta

CUTOFF_HOUR = 14


def _is_business_day(day: date, holidays) -> bool:
    return day.weekday() < 5 and day not in holidays


def _next_business_day(day: date, holidays) -> date:
    while not _is_business_day(day, holidays):
        day += timedelta(days=1)
    return day


def delivery_date(placed: datetime, business_days: int, holidays=frozenset()) -> date:
    if business_days < 0:
        raise ValueError("business_days must be >= 0")
    day = placed.date()
    if _is_business_day(day, holidays):
        if placed.hour >= CUTOFF_HOUR:
            day = _next_business_day(day + timedelta(days=1), holidays)
    else:
        day = _next_business_day(day, holidays)
    remaining = business_days
    while remaining > 0:
        day = _next_business_day(day + timedelta(days=1), holidays)
        remaining -= 1
    return day
