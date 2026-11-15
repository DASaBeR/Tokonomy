import unittest
from datetime import date

from roombook.errors import ConflictError, NotFoundError, ValidationError
from roombook.models import BookingStatus

from tests.helpers import local, make_service


class BookingTests(unittest.TestCase):
    def setUp(self):
        self.service = make_service()

    def book(self, user, room, start, end, **kwargs):
        return self.service.book(user, room, local(self.service, room, start), local(self.service, room, end), **kwargs)

    def test_booking_is_stored_and_confirmed(self):
        booking = self.book("bob", "atlas", "2026-12-02 09:00", "2026-12-02 10:00", title="  Planning ")
        self.assertEqual(self.service.bookings.get(booking.id).title, "Planning")
        [note] = self.service.outbox.for_user("bob")
        self.assertEqual(note.kind, "confirmed")
        self.assertEqual(note.message, "Booked Atlas 2026-12-02 09:00-10:00 (Planning)")

    def test_overlapping_booking_is_refused(self):
        first = self.book("bob", "atlas", "2026-12-02 09:00", "2026-12-02 10:00")
        with self.assertRaises(ConflictError) as ctx:
            self.book("carol", "atlas", "2026-12-02 09:30", "2026-12-02 10:30")
        self.assertEqual([b.id for b in ctx.exception.conflicts], [first.id])

    def test_other_rooms_do_not_conflict(self):
        self.book("bob", "atlas", "2026-12-02 09:00", "2026-12-02 10:00")
        self.book("carol", "borealis", "2026-12-02 09:00", "2026-12-02 10:00")

    def test_capacity(self):
        with self.assertRaises(ValidationError):
            self.book("bob", "borealis", "2026-12-02 09:00", "2026-12-02 10:00", attendees=5)

    def test_unknown_room_and_user(self):
        with self.assertRaises(NotFoundError):
            self.book("bob", "nowhere", "2026-12-02 09:00", "2026-12-02 10:00")
        with self.assertRaises(NotFoundError):
            self.service.book("zed", "atlas", local(self.service, "atlas", "2026-12-02 09:00"), local(self.service, "atlas", "2026-12-02 10:00"))

    def test_end_before_start(self):
        with self.assertRaises(ValidationError):
            self.book("bob", "atlas", "2026-12-02 10:00", "2026-12-02 09:00")


class CancelTests(unittest.TestCase):
    def setUp(self):
        self.service = make_service()
        self.booking = self.service.book(
            "bob", "atlas", local(self.service, "atlas", "2026-12-02 09:00"), local(self.service, "atlas", "2026-12-02 10:00")
        )

    def test_cancel_keeps_the_booking_as_history_and_frees_the_room(self):
        self.service.cancel(self.booking.id)
        self.assertEqual(self.service.bookings.get(self.booking.id).status, BookingStatus.CANCELLED)
        again = self.service.book(
            "carol", "atlas", local(self.service, "atlas", "2026-12-02 09:00"), local(self.service, "atlas", "2026-12-02 10:00")
        )
        self.assertTrue(again.is_active)

    def test_cancel_notifies_the_owner(self):
        self.service.cancel(self.booking.id)
        self.assertEqual(self.service.outbox.for_user("bob")[-1].message, "Your booking of Atlas on 2026-12-02 09:00 was cancelled")

    def test_cancelling_twice_is_refused(self):
        self.service.cancel(self.booking.id)
        with self.assertRaises(ValidationError):
            self.service.cancel(self.booking.id)


class QueryTests(unittest.TestCase):
    def setUp(self):
        self.service = make_service()

    def test_bookings_on_uses_the_room_local_day(self):
        # Cedar is UTC-05:00: 19:00-20:00 local on 2 December is 00:00-01:00 UTC on 3 December.
        late = self.service.book("bob", "cedar", local(self.service, "cedar", "2026-12-02 19:00"), local(self.service, "cedar", "2026-12-02 20:00"))
        self.assertEqual([b.id for b in self.service.bookings_on("cedar", date(2026, 12, 2))], [late.id])
        self.assertEqual(self.service.bookings_on("cedar", date(2026, 12, 3)), [])

    def test_free_slots_skip_booked_time(self):
        # Admins may book beyond the member maximum.
        self.service.book("alice", "atlas", local(self.service, "atlas", "2026-12-02 08:00"), local(self.service, "atlas", "2026-12-02 20:00"))
        slots = self.service.find_free_slots("atlas", date(2026, 12, 2), 30)
        starts = [s.strftime("%H:%M") for s, _ in slots]
        self.assertIn("07:00", starts)
        self.assertNotIn("09:00", starts)
        self.assertTrue(all(s.hour < 8 or s.hour >= 20 for s, _ in slots))

    def test_free_slots_of_an_empty_day(self):
        slots = self.service.find_free_slots("borealis", date(2026, 12, 2), 60)
        self.assertEqual(len(slots), (21 - 7 - 1) * 4 + 1)

    def test_upcoming_for_user_ignores_cancelled_and_past(self):
        a = self.service.book("dave", "atlas", local(self.service, "atlas", "2026-12-02 09:00"), local(self.service, "atlas", "2026-12-02 10:00"))
        b = self.service.book("dave", "atlas", local(self.service, "atlas", "2026-12-03 09:00"), local(self.service, "atlas", "2026-12-03 10:00"))
        self.service.cancel(a.id)
        self.assertEqual([x.id for x in self.service.upcoming_for_user("dave")], [b.id])


if __name__ == "__main__":
    unittest.main()
