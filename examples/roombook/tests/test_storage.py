import os
import tempfile
import unittest
from datetime import datetime

from roombook.models import Booking, BookingStatus
from roombook.seed import load_seed
from roombook.storage import MIGRATIONS, BookingRepository, Database, RoomRepository
from roombook.timeutil import UTC


class StorageTests(unittest.TestCase):
    def test_new_database_is_fully_migrated(self):
        db = Database(":memory:")
        self.assertEqual(db.schema_version, len(MIGRATIONS))

    def test_migrations_are_not_reapplied(self):
        path = os.path.join(tempfile.mkdtemp(), "rb.db")
        Database(path).close()
        db = Database(path)  # would fail with "table rooms already exists" if re-run
        self.assertEqual(db.schema_version, len(MIGRATIONS))
        db.close()

    def test_instants_round_trip_in_utc(self):
        db = Database(":memory:")
        load_seed(db)
        repo = BookingRepository(db)
        start = datetime(2026, 12, 2, 9, 0, tzinfo=UTC)
        saved = repo.add(Booking(None, "atlas", "bob", start, datetime(2026, 12, 2, 10, 0, tzinfo=UTC)))
        self.assertEqual(repo.get(saved.id).start, start)
        self.assertEqual(repo.get(saved.id).status, BookingStatus.ACTIVE)

    def test_transaction_rolls_back_on_error(self):
        db = Database(":memory:")
        load_seed(db)
        repo = BookingRepository(db)
        with self.assertRaises(RuntimeError):
            with db.transaction():
                repo.add(Booking(None, "atlas", "bob", datetime(2026, 12, 2, 9, 0, tzinfo=UTC), datetime(2026, 12, 2, 10, 0, tzinfo=UTC)))
                raise RuntimeError("boom")
        self.assertEqual(repo.for_room("atlas"), [])

    def test_seed_loads_once(self):
        db = Database(":memory:")
        load_seed(db)
        load_seed(db)
        self.assertEqual([r.id for r in RoomRepository(db).all()], ["atlas", "borealis", "cedar", "dune"])


if __name__ == "__main__":
    unittest.main()
