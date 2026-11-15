import unittest

from roombook.errors import PolicyError

from tests.helpers import local, make_service


class PolicyTests(unittest.TestCase):
    def setUp(self):
        self.service = make_service()

    def book(self, user, room, start, end):
        return self.service.book(user, room, local(self.service, room, start), local(self.service, room, end))

    def assertRule(self, code, user, room, start, end):
        with self.assertRaises(PolicyError) as ctx:
            self.book(user, room, start, end)
        self.assertEqual(ctx.exception.code, code)

    def test_minimum_length(self):
        self.assertRule("too_short", "bob", "atlas", "2026-12-02 09:00", "2026-12-02 09:10")

    def test_maximum_length_for_members(self):
        self.assertRule("too_long", "bob", "atlas", "2026-12-02 09:00", "2026-12-02 13:15")

    def test_admins_may_book_longer(self):
        booking = self.book("alice", "atlas", "2026-12-02 09:00", "2026-12-02 14:00")
        self.assertEqual(booking.duration_minutes, 300)

    def test_granularity(self):
        self.assertRule("granularity", "bob", "atlas", "2026-12-02 09:10", "2026-12-02 10:00")

    def test_opening_hours_are_room_local(self):
        # 07:00 in Dune (UTC+03:30) is 03:30 UTC: allowed, because the room is open then.
        self.book("bob", "dune", "2026-12-02 07:00", "2026-12-02 08:00")
        self.assertRule("outside_hours", "bob", "dune", "2026-12-02 06:30", "2026-12-02 07:30")
        self.assertRule("outside_hours", "bob", "cedar", "2026-12-02 20:30", "2026-12-02 21:30")

    def test_no_bookings_in_the_past(self):
        self.service.clock.advance(days=2)
        self.assertRule("in_past", "bob", "atlas", "2026-12-02 09:00", "2026-12-02 10:00")

    def test_advance_window(self):
        self.assertRule("too_far_ahead", "bob", "atlas", "2027-03-01 09:00", "2027-03-01 10:00")


if __name__ == "__main__":
    unittest.main()
