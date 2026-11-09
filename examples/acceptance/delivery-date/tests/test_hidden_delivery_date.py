"""Hidden acceptance test: copied in only after the run, never shown to workers."""

import unittest
from datetime import date, datetime

from shop.shipping import delivery_date

# 2026-11-02 is a Monday.
MON, TUE, WED, THU, FRI, SAT, SUN = (date(2026, 11, d) for d in (2, 3, 4, 5, 6, 7, 8))
NEXT_MON, NEXT_TUE = date(2026, 11, 9), date(2026, 11, 10)


def at(day, hour, minute=0):
    return datetime(day.year, day.month, day.day, hour, minute)


class DeliveryDateAcceptance(unittest.TestCase):
    def test_simple_business_days(self):
        self.assertEqual(delivery_date(at(MON, 10), 3), THU)

    def test_cutoff_is_inclusive_at_14_00(self):
        self.assertEqual(delivery_date(at(MON, 13, 59), 1), TUE)
        self.assertEqual(delivery_date(at(MON, 14, 0), 1), WED)

    def test_friday_after_cutoff_rolls_to_monday(self):
        self.assertEqual(delivery_date(at(FRI, 15), 1), NEXT_TUE)

    def test_weekend_order_counts_from_next_business_day_start(self):
        self.assertEqual(delivery_date(at(SAT, 10), 0), NEXT_MON)
        self.assertEqual(delivery_date(at(SAT, 20), 0), NEXT_MON)  # cutoff does not apply on non-business days
        self.assertEqual(delivery_date(at(SUN, 9), 2), NEXT_TUE + (NEXT_TUE - NEXT_MON))

    def test_zero_days_on_business_day_before_cutoff_is_same_day(self):
        self.assertEqual(delivery_date(at(WED, 9), 0), WED)

    def test_zero_days_after_cutoff_is_next_business_day(self):
        self.assertEqual(delivery_date(at(WED, 17), 0), THU)

    def test_holidays_are_skipped(self):
        self.assertEqual(delivery_date(at(MON, 10), 3, holidays={WED}), FRI)

    def test_holiday_on_the_day_after_a_weekend(self):
        self.assertEqual(delivery_date(at(FRI, 10), 1, holidays={NEXT_MON}), NEXT_TUE)

    def test_order_placed_on_a_holiday(self):
        self.assertEqual(delivery_date(at(NEXT_MON, 10), 0, holidays={NEXT_MON}), NEXT_TUE)

    def test_long_range_over_two_weekends(self):
        self.assertEqual(delivery_date(at(MON, 9), 10), date(2026, 11, 16))

    def test_negative_days_rejected(self):
        with self.assertRaises(ValueError):
            delivery_date(at(MON, 10), -1)

    def test_frozenset_default_is_not_mutated_between_calls(self):
        delivery_date(at(MON, 10), 1)
        self.assertEqual(delivery_date(at(MON, 10), 1), TUE)


if __name__ == "__main__":
    unittest.main()
