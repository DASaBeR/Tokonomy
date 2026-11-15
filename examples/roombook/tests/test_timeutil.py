import unittest
from datetime import date, datetime, timedelta, timezone

from roombook.errors import ValidationError
from roombook.models import Room
from roombook.timeutil import UTC, format_local, from_room_local, local_day_bounds, overlaps, parse_instant, to_room_local

NEW_YORK = Room("cedar", "Cedar", 12, -300)
TEHRAN = Room("dune", "Dune", 6, 210)


class ParseInstantTests(unittest.TestCase):
    def test_offset_is_normalised_to_utc(self):
        self.assertEqual(parse_instant("2026-12-01T09:00:00+01:00"), datetime(2026, 12, 1, 8, 0, tzinfo=UTC))

    def test_z_suffix_is_utc(self):
        self.assertEqual(parse_instant("2026-12-01T09:00:00Z"), datetime(2026, 12, 1, 9, 0, tzinfo=UTC))

    def test_naive_time_is_refused(self):
        with self.assertRaises(ValidationError):
            parse_instant("2026-12-01T09:00:00")

    def test_garbage_is_refused(self):
        with self.assertRaises(ValidationError):
            parse_instant("tomorrow at nine")


class RoomLocalTimeTests(unittest.TestCase):
    def test_round_trip_with_a_half_hour_offset(self):
        instant = from_room_local(datetime(2026, 12, 2, 9, 0), TEHRAN)
        self.assertEqual(instant, datetime(2026, 12, 2, 5, 30, tzinfo=UTC))
        self.assertEqual(to_room_local(instant, TEHRAN).hour, 9)
        self.assertEqual(format_local(instant, TEHRAN), "2026-12-02 09:00")

    def test_local_day_of_a_negative_offset_room_crosses_utc_midnight(self):
        start, end = local_day_bounds(date(2026, 12, 2), NEW_YORK)
        self.assertEqual(start, datetime(2026, 12, 2, 5, 0, tzinfo=UTC))
        self.assertEqual(end - start, timedelta(days=1))

    def test_from_room_local_refuses_aware_input(self):
        with self.assertRaises(ValidationError):
            from_room_local(datetime(2026, 12, 2, 9, 0, tzinfo=timezone.utc), NEW_YORK)


class OverlapTests(unittest.TestCase):
    def setUp(self):
        self.t = lambda h, m=0: datetime(2026, 12, 2, h, m, tzinfo=UTC)

    def test_partial_overlap(self):
        self.assertTrue(overlaps(self.t(9), self.t(10), self.t(9, 30), self.t(11)))

    def test_containment(self):
        self.assertTrue(overlaps(self.t(9), self.t(12), self.t(10), self.t(11)))

    def test_disjoint(self):
        self.assertFalse(overlaps(self.t(9), self.t(10), self.t(11), self.t(12)))


if __name__ == "__main__":
    unittest.main()
