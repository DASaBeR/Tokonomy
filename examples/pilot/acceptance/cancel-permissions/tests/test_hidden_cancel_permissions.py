"""Hidden acceptance tests for task cancel-permissions. Never shown to the worker."""

import io
import unittest

from roombook.cli import main
from roombook.errors import NotFoundError, PermissionDeniedError
from roombook.models import BookingStatus

from tests.helpers import local, make_service


class HiddenCancelPermissionTests(unittest.TestCase):
    def setUp(self):
        self.s = make_service()
        self.booking = self.s.book("bob", "atlas", local(self.s, "atlas", "2026-12-02 09:00"), local(self.s, "atlas", "2026-12-02 10:00"))
        self.s.outbox.sent.clear()

    def status(self):
        return self.s.bookings.get(self.booking.id).status

    def test_owner_may_cancel(self):
        self.s.cancel(self.booking.id, "bob")
        self.assertEqual(self.status(), BookingStatus.CANCELLED)
        [note] = self.s.outbox.sent
        self.assertEqual((note.user_id, note.kind), ("bob", "cancelled"))

    def test_admin_may_cancel_and_the_owner_is_told_who_did(self):
        self.s.cancel(self.booking.id, "alice")
        self.assertEqual(self.status(), BookingStatus.CANCELLED)
        [note] = self.s.outbox.sent
        self.assertEqual(note.user_id, "bob")
        self.assertIn("cancelled by Alice Moreau", note.message)
        self.assertEqual(self.s.outbox.for_user("alice"), [])

    def test_another_member_is_refused_and_nothing_changes(self):
        with self.assertRaises(PermissionDeniedError):
            self.s.cancel(self.booking.id, "carol")
        self.assertEqual(self.status(), BookingStatus.ACTIVE)
        self.assertEqual(self.s.outbox.sent, [])

    def test_unknown_actor_is_not_found_and_nothing_changes(self):
        with self.assertRaises(NotFoundError):
            self.s.cancel(self.booking.id, "zed")
        self.assertEqual(self.status(), BookingStatus.ACTIVE)

    def test_a_refused_member_cannot_learn_more_by_retrying(self):
        for _ in range(3):
            with self.assertRaises(PermissionDeniedError):
                self.s.cancel(self.booking.id, "dave")
        self.assertEqual(self.status(), BookingStatus.ACTIVE)


class HiddenCancelCliTests(unittest.TestCase):
    def setUp(self):
        self.s = make_service()
        self.s.book("bob", "atlas", local(self.s, "atlas", "2026-12-02 09:00"), local(self.s, "atlas", "2026-12-02 10:00"))

    def run_cli(self, *argv):
        out = io.StringIO()
        return main(list(argv), service=self.s, out=out), out.getvalue().strip()

    def test_cancel_as_the_owner(self):
        self.assertEqual(self.run_cli("cancel", "1", "--as", "bob"), (0, "cancelled #1"))

    def test_cancel_as_someone_else_is_refused_with_exit_code_1(self):
        code, text = self.run_cli("cancel", "1", "--as", "carol")
        self.assertEqual(code, 1)
        self.assertTrue(text.startswith("error: "))
        self.assertTrue(self.s.bookings.get(1).is_active)

    def test_the_actor_is_required(self):
        code, _ = self.run_cli("cancel", "1")
        self.assertEqual(code, 2)
        self.assertTrue(self.s.bookings.get(1).is_active)


if __name__ == "__main__":
    unittest.main()
