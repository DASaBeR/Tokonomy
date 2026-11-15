"""Hidden acceptance tests for task maintenance. Never shown to the worker.

What happens to EXISTING bookings inside a new window is deliberately left open by the task (the ambiguity under test)
and is NOT checked here: every window below is scheduled on a room with no bookings in it. No interval touches another
end-to-start either (the base code has an unrelated back-to-back defect that another task fixes).
"""

import os
import tempfile
import unittest
from datetime import date

from roombook.errors import BookingError, MaintenanceError, NotFoundError, PermissionDeniedError, ValidationError
from roombook.clock import FixedClock
from roombook.seed import load_seed
from roombook.service import BookingService
from roombook.storage import Database
from roombook.timeutil import format_local

from tests.helpers import NOW, local, make_service


class HiddenMaintenanceTests(unittest.TestCase):
    def setUp(self):
        self.s = make_service()

    def at(self, room, text):
        return local(self.s, room, "2026-12-02 " + text)

    def window(self, room, start, end, reason="painting", actor="alice"):
        return self.s.schedule_maintenance(room, self.at(room, start), self.at(room, end), reason, actor)

    def test_admin_schedules_and_lists_windows_in_start_order(self):
        later = self.window("atlas", "15:00", "17:00", "carpet")
        earlier = self.window("atlas", "08:00", "09:00", "projector")
        self.window("borealis", "08:00", "09:00", "other room")
        windows = self.s.maintenance_for_room("atlas")
        self.assertEqual([w.reason for w in windows], ["projector", "carpet"])
        self.assertEqual(windows[0].room_id, "atlas")
        self.assertEqual(windows[0].start, earlier.start)
        self.assertEqual(windows[1].end, later.end)

    def test_members_cannot_schedule_maintenance(self):
        with self.assertRaises(PermissionDeniedError):
            self.window("atlas", "08:00", "09:00", actor="bob")
        self.assertEqual(self.s.maintenance_for_room("atlas"), [])

    def test_unknown_actor_and_room(self):
        with self.assertRaises(NotFoundError):
            self.window("atlas", "08:00", "09:00", actor="zed")
        with self.assertRaises(NotFoundError):
            self.s.schedule_maintenance("nowhere", self.at("atlas", "08:00"), self.at("atlas", "09:00"), "x", "alice")

    def test_window_must_end_after_it_starts(self):
        with self.assertRaises(ValidationError):
            self.window("atlas", "10:00", "09:00")

    def test_new_bookings_inside_a_window_are_refused(self):
        self.window("atlas", "10:00", "12:00")
        for start, end in (("10:00", "11:00"), ("09:30", "10:30"), ("11:30", "12:30"), ("09:00", "13:00")):
            with self.assertRaises(MaintenanceError):
                self.s.book("bob", "atlas", self.at("atlas", start), self.at("atlas", end))
        self.assertTrue(issubclass(MaintenanceError, BookingError))
        self.assertEqual([b for b in self.s.bookings.for_room("atlas") if b.is_active], [])

    def test_bookings_outside_a_window_and_in_other_rooms_still_work(self):
        self.window("atlas", "10:00", "12:00")
        self.s.book("bob", "atlas", self.at("atlas", "08:00"), self.at("atlas", "09:30"))
        self.s.book("carol", "atlas", self.at("atlas", "12:30"), self.at("atlas", "13:30"))
        self.s.book("dave", "borealis", self.at("borealis", "10:00"), self.at("borealis", "11:00"))

    def test_free_slots_skip_maintenance(self):
        self.window("dune", "10:00", "12:00")
        room = self.s.rooms.get("dune")
        starts = [format_local(s, room)[11:] for s, _ in self.s.find_free_slots("dune", date(2026, 12, 2), 60)]
        self.assertIn("08:00", starts)
        self.assertIn("12:15", starts)
        for blocked in ("09:30", "10:00", "10:30", "11:00", "11:45"):
            self.assertNotIn(blocked, starts)

    def test_windows_are_stored_in_the_database(self):
        path = os.path.join(tempfile.mkdtemp(), "rb.db")
        db = Database(path)
        load_seed(db)
        first = BookingService(db, clock=FixedClock(NOW))
        first.schedule_maintenance("atlas", self.at("atlas", "10:00"), self.at("atlas", "12:00"), "boiler", "alice")
        db.close()

        reopened = BookingService(Database(path), clock=FixedClock(NOW))
        self.assertEqual([w.reason for w in reopened.maintenance_for_room("atlas")], ["boiler"])
        with self.assertRaises(MaintenanceError):
            reopened.book("bob", "atlas", self.at("atlas", "10:30"), self.at("atlas", "11:00"))
        reopened.db.close()


if __name__ == "__main__":
    unittest.main()
