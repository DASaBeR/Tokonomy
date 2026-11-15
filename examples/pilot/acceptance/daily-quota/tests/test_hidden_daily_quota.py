"""Hidden acceptance tests for task daily-quota. Never shown to the worker.

No two bookings in these tests touch end-to-start: the base code has an unrelated back-to-back defect (another task).
"""

import unittest

from roombook.errors import PolicyError
from roombook.policy import BookingPolicy

from tests.helpers import local, make_service


class HiddenDailyQuotaTests(unittest.TestCase):
    def setUp(self):
        self.s = make_service()

    def book(self, user, room, day, start, end):
        return self.s.book(user, room, local(self.s, room, f"{day} {start}"), local(self.s, room, f"{day} {end}"))

    def assertQuota(self, user, room, day, start, end):
        before = len(self.s.outbox.sent)
        with self.assertRaises(PolicyError) as ctx:
            self.book(user, room, day, start, end)
        self.assertEqual(ctx.exception.code, "daily_quota")
        self.assertEqual(len(self.s.outbox.sent), before)

    def test_exactly_four_hours_is_allowed_and_more_is_refused(self):
        self.book("bob", "atlas", "2026-12-02", "08:00", "10:00")
        self.book("bob", "atlas", "2026-12-02", "11:00", "13:00")
        self.assertQuota("bob", "atlas", "2026-12-02", "14:00", "14:15")
        self.assertEqual(len([b for b in self.s.bookings.for_user("bob") if b.is_active]), 2)

    def test_other_days_and_other_users_are_separate(self):
        self.book("bob", "atlas", "2026-12-02", "08:00", "12:00")
        self.book("bob", "atlas", "2026-12-03", "08:00", "12:00")
        self.book("carol", "atlas", "2026-12-02", "13:00", "17:00")

    def test_cancelled_bookings_do_not_count(self):
        self.book("bob", "atlas", "2026-12-02", "08:00", "10:00")
        second = self.book("bob", "atlas", "2026-12-02", "11:00", "13:00")
        self.s.cancel(second.id)
        self.book("bob", "atlas", "2026-12-02", "14:00", "16:00")

    def test_the_quota_spans_rooms_and_uses_the_local_day_of_the_room_being_booked(self):
        self.book("bob", "atlas", "2026-12-02", "09:00", "12:00")           # 09:00-12:00 UTC
        # Borealis is UTC+01:00: the Atlas booking falls on the same local day there.
        self.assertQuota("bob", "borealis", "2026-12-02", "14:00", "15:15")
        self.book("bob", "borealis", "2026-12-02", "14:00", "15:00")

    def test_a_local_day_that_crosses_utc_midnight(self):
        # Cedar is UTC-05:00. 17:00-19:00 local = 22:00-00:00 UTC; 19:15-21:00 local = 00:15-02:00 UTC next day.
        self.book("dave", "cedar", "2026-12-02", "17:00", "19:00")
        self.book("dave", "cedar", "2026-12-02", "19:15", "21:00")         # 3h45 on local 2 December
        self.assertQuota("dave", "cedar", "2026-12-02", "07:00", "07:30")  # would be 4h15 on local 2 December

    def test_the_next_local_day_is_not_charged_with_the_late_bookings(self):
        self.book("dave", "cedar", "2026-12-02", "17:00", "19:00")
        self.book("dave", "cedar", "2026-12-02", "19:15", "21:00")
        self.book("dave", "cedar", "2026-12-03", "07:00", "09:30")         # nothing held on local 3 December yet

    def test_admins_are_exempt(self):
        self.book("alice", "atlas", "2026-12-02", "08:00", "12:00")
        self.book("alice", "atlas", "2026-12-02", "13:00", "15:00")

    def test_none_disables_the_quota(self):
        self.s = make_service(policy=BookingPolicy(daily_quota_minutes=None))
        self.book("bob", "atlas", "2026-12-02", "08:00", "12:00")
        self.book("bob", "atlas", "2026-12-02", "13:00", "15:00")

    def test_a_smaller_configured_quota(self):
        self.s = make_service(policy=BookingPolicy(daily_quota_minutes=60))
        self.book("bob", "atlas", "2026-12-02", "08:00", "09:00")
        self.assertQuota("bob", "atlas", "2026-12-02", "10:00", "10:15")


if __name__ == "__main__":
    unittest.main()
