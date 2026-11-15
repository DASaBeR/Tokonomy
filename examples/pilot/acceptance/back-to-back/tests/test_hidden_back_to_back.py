"""Hidden acceptance tests for task back-to-back. Never shown to the worker."""

import unittest
from datetime import date

from roombook.errors import ConflictError
from roombook.timeutil import format_local

from tests.helpers import local, make_service


class HiddenBackToBackTests(unittest.TestCase):
    def setUp(self):
        self.s = make_service()

    def book(self, user, room, start, end):
        return self.s.book(user, room, local(self.s, room, "2026-12-02 " + start), local(self.s, room, "2026-12-02 " + end))

    def test_booking_that_starts_when_another_ends_is_allowed(self):
        self.book("bob", "atlas", "10:00", "11:00")
        after = self.book("carol", "atlas", "11:00", "12:00")
        self.assertTrue(after.is_active)

    def test_booking_that_ends_when_another_starts_is_allowed(self):
        self.book("bob", "atlas", "10:00", "11:00")
        before = self.book("carol", "atlas", "09:00", "10:00")
        self.assertTrue(before.is_active)

    def test_back_to_back_in_a_half_hour_offset_room(self):
        self.book("bob", "dune", "10:00", "11:30")
        self.book("carol", "dune", "11:30", "12:00")
        self.book("dave", "dune", "09:15", "10:00")

    def test_genuine_overlaps_are_still_refused(self):
        a = self.book("bob", "atlas", "10:00", "11:00")
        b = self.book("carol", "atlas", "11:00", "12:00")
        with self.assertRaises(ConflictError) as ctx:
            self.book("dave", "atlas", "10:30", "11:30")
        self.assertEqual(sorted(x.id for x in ctx.exception.conflicts), sorted([a.id, b.id]))
        with self.assertRaises(ConflictError):
            self.book("dave", "atlas", "09:45", "10:15")
        with self.assertRaises(ConflictError):
            self.book("dave", "atlas", "10:15", "10:45")
        with self.assertRaises(ConflictError):
            self.book("dave", "atlas", "09:00", "13:00")

    def test_free_slots_touching_a_booking_are_offered(self):
        self.book("bob", "atlas", "10:00", "11:00")
        starts = [s.strftime("%H:%M") for s, _ in self.s.find_free_slots("atlas", date(2026, 12, 2), 60)]
        self.assertIn("09:00", starts)   # ends exactly when the booking starts
        self.assertIn("11:00", starts)   # starts exactly when the booking ends
        for taken in ("09:15", "09:30", "09:45", "10:00", "10:15", "10:30", "10:45"):
            self.assertNotIn(taken, starts)

    def test_free_slots_between_two_bookings(self):
        self.book("bob", "borealis", "09:00", "10:00")
        self.book("carol", "borealis", "11:00", "12:00")
        room = self.s.rooms.get("borealis")
        slots = self.s.find_free_slots("borealis", date(2026, 12, 2), 60)
        local_starts = [format_local(s, room)[11:] for s, _ in slots]
        self.assertIn("10:00", local_starts)  # 10:00-11:00 local fits exactly between them


if __name__ == "__main__":
    unittest.main()
