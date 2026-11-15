"""Hidden acceptance tests for task csv-import. Never shown to the worker.

The files are written byte for byte the way Excel exports "CSV UTF-8": a byte order mark, CRLF line endings, UTF-8 text.
"""

import io
import os
import tempfile
import unittest
from datetime import datetime
from pathlib import Path

from roombook.cli import main
from roombook.importer import import_bookings
from roombook.timeutil import UTC

from tests.helpers import local, make_service

BOM = "﻿"
CRLF = "\r\n"
HEADER = "room,user,start,end,title"


def excel_csv(*rows: str) -> str:
    """Writes an Excel-style CSV to a temp file and returns its path."""
    handle, path = tempfile.mkstemp(suffix=".csv")
    with os.fdopen(handle, "wb") as f:
        f.write((BOM + CRLF.join((HEADER,) + rows) + CRLF).encode("utf-8"))
    return path


CLEAN = (
    "atlas,bob,2026-12-07 09:00,2026-12-07 10:00,Sprint planning",
    "dune,carol,2026-12-07 09:00,2026-12-07 10:30,Café supplier review",
    "cedar,dave,2026-12-07 19:00,2026-12-07 20:00,Late sync with 東京",
    'borealis,bob,2026-12-08 08:00,2026-12-08 09:00,"Review, part 2"',
)

# line 1 is the header
MIXED = (
    "atlas,bob,2026-12-07 09:00,2026-12-07 10:00,ok one",            # line 2: valid
    "nowhere,bob,2026-12-07 09:00,2026-12-07 10:00,unknown room",    # line 3: fails
    "borealis,carol,2026-12-07 09:00,2026-12-07 10:00,ok two",       # line 4: valid
    "atlas,dave,2026-12-07 09:30,2026-12-07 10:30,clashes line 2",   # line 5: fails (conflict with an earlier row)
    "dune,dave,2026-12-07 05:00,2026-12-07 06:00,before opening",    # line 6: fails (opening hours)
)


class HiddenCsvImportTests(unittest.TestCase):
    def setUp(self):
        self.s = make_service()

    def active(self):
        return [b for room in ("atlas", "borealis", "cedar", "dune") for b in self.s.bookings.for_room(room) if b.is_active]

    def test_excel_export_with_byte_order_mark_imports_cleanly(self):
        result = import_bookings(self.s, excel_csv(*CLEAN))
        self.assertEqual(result.errors, [])
        self.assertEqual(len(result.imported), 4)
        by_room = {b.room_id: b for b in result.imported}
        self.assertEqual(by_room["atlas"].title, "Sprint planning")
        self.assertEqual(by_room["dune"].title, "Café supplier review")
        self.assertEqual(by_room["cedar"].title, "Late sync with 東京")
        self.assertEqual(by_room["borealis"].title, "Review, part 2")
        self.assertEqual(len(self.active()), 4)

    def test_times_are_room_local_wall_clock_times(self):
        result = import_bookings(self.s, excel_csv(*CLEAN))
        by_room = {b.room_id: b for b in result.imported}
        self.assertEqual(by_room["dune"].start, datetime(2026, 12, 7, 5, 30, tzinfo=UTC))   # UTC+03:30
        self.assertEqual(by_room["cedar"].start, datetime(2026, 12, 8, 0, 0, tzinfo=UTC))   # UTC-05:00
        self.assertEqual(by_room["borealis"].end, datetime(2026, 12, 8, 8, 0, tzinfo=UTC))  # UTC+01:00

    def test_each_imported_booking_is_confirmed_like_a_normal_one(self):
        import_bookings(self.s, excel_csv(*CLEAN))
        self.assertEqual([n.kind for n in self.s.outbox.for_user("bob")], ["confirmed", "confirmed"])
        self.assertEqual(len(self.s.outbox.sent), 4)

    def test_the_shipped_sample_imports_cleanly(self):
        sample = Path(__file__).resolve().parents[1] / "samples" / "bookings_export.csv"
        result = import_bookings(self.s, sample)
        self.assertEqual(result.errors, [])
        self.assertEqual(len(result.imported), 4)

    def test_all_or_nothing_by_default(self):
        result = import_bookings(self.s, excel_csv(*MIXED))
        self.assertEqual(result.imported, [])
        self.assertEqual([e.line for e in result.errors], [3, 5, 6])
        self.assertTrue(all(e.message.strip() for e in result.errors))
        self.assertEqual(self.active(), [])
        self.assertEqual(self.s.outbox.sent, [])

    def test_partial_books_the_valid_rows(self):
        result = import_bookings(self.s, excel_csv(*MIXED), partial=True)
        self.assertEqual(sorted(b.title for b in result.imported), ["ok one", "ok two"])
        self.assertEqual([e.line for e in result.errors], [3, 5, 6])
        self.assertEqual(len(self.active()), 2)
        self.assertEqual(len(self.s.outbox.sent), 2)

    def test_conflict_with_an_existing_booking_is_a_row_error(self):
        self.s.book("carol", "atlas", local(self.s, "atlas", "2026-12-07 09:00"), local(self.s, "atlas", "2026-12-07 10:00"))
        result = import_bookings(self.s, excel_csv("atlas,bob,2026-12-07 09:30,2026-12-07 10:30,clash"))
        self.assertEqual([e.line for e in result.errors], [2])
        self.assertEqual(result.imported, [])

    def test_malformed_rows_are_reported_with_their_line(self):
        result = import_bookings(self.s, excel_csv(
            "atlas,bob,2026-12-07 09:00,2026-12-07 10:00,fine",
            "atlas,bob,7 Dec 9am,2026-12-07 11:00,bad time",
            "atlas,bob,2026-12-07 12:00",
        ), partial=True)
        self.assertEqual([e.line for e in result.errors], [3, 4])
        self.assertEqual([b.title for b in result.imported], ["fine"])

    def test_rules_of_a_normal_booking_apply(self):
        result = import_bookings(self.s, excel_csv(
            "borealis,bob,2026-12-07 09:00,2026-12-07 14:00,too long for a member",
            "atlas,zed,2026-12-07 09:00,2026-12-07 10:00,unknown user",
        ), partial=True)
        self.assertEqual([e.line for e in result.errors], [2, 3])


class HiddenCsvImportCliTests(unittest.TestCase):
    def setUp(self):
        self.s = make_service()

    def run_cli(self, *argv):
        out = io.StringIO()
        return main(list(argv), service=self.s, out=out), out.getvalue().splitlines()

    def test_clean_import(self):
        code, lines = self.run_cli("import-csv", excel_csv(*CLEAN))
        self.assertEqual(code, 0)
        self.assertEqual(lines[-1], "imported 4 booking(s)")

    def test_failed_all_or_nothing_import(self):
        code, lines = self.run_cli("import-csv", excel_csv(*MIXED))
        self.assertEqual(code, 1)
        self.assertEqual([l.split(":")[0] for l in lines if l.startswith("line ")], ["line 3", "line 5", "line 6"])
        self.assertEqual(lines[-1], "imported 0 booking(s)")

    def test_partial_import(self):
        code, lines = self.run_cli("import-csv", excel_csv(*MIXED), "--partial")
        self.assertEqual(code, 1)
        self.assertEqual(lines[-1], "imported 2 booking(s)")


if __name__ == "__main__":
    unittest.main()
