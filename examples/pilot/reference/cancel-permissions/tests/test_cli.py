import io
import unittest

from roombook.cli import main

from tests.helpers import make_service


class CliTests(unittest.TestCase):
    def setUp(self):
        self.service = make_service()

    def run_cli(self, *argv):
        out = io.StringIO()
        code = main(list(argv), service=self.service, out=out)
        return code, out.getvalue()

    def test_rooms(self):
        code, text = self.run_cli("rooms")
        self.assertEqual(code, 0)
        self.assertIn("dune", text)
        self.assertIn("UTC+03:30", text)
        self.assertIn("UTC-05:00", text)

    def test_book_and_cancel(self):
        code, text = self.run_cli("book", "--user", "bob", "--room", "borealis",
                                  "--start", "2026-12-02T09:00:00+01:00", "--end", "2026-12-02T10:00:00+01:00", "--title", "1:1")
        self.assertEqual((code, text.strip()), (0, "booked #1 Borealis 2026-12-02 09:00-10:00"))
        code, text = self.run_cli("cancel", "1", "--as", "bob")
        self.assertEqual((code, text.strip()), (0, "cancelled #1"))

    def test_refusal_is_exit_code_1(self):
        code, text = self.run_cli("book", "--user", "bob", "--room", "borealis",
                                  "--start", "2026-12-02T03:00:00+01:00", "--end", "2026-12-02T04:00:00+01:00")
        self.assertEqual(code, 1)
        self.assertTrue(text.startswith("error: the room is open"))

    def test_usage_error_is_exit_code_2(self):
        code, _ = self.run_cli("book", "--user", "bob")
        self.assertEqual(code, 2)

    def test_free_slots_in_local_time(self):
        code, text = self.run_cli("free", "--room", "dune", "--day", "2026-12-02", "--minutes", "240")
        self.assertEqual(code, 0)
        self.assertEqual(text.splitlines()[0], "07:00-11:00")
        self.assertEqual(text.splitlines()[-1], "17:00-21:00")

    def test_report(self):
        self.run_cli("book", "--user", "bob", "--room", "atlas",
                     "--start", "2026-12-02T09:00:00Z", "--end", "2026-12-02T11:00:00Z")
        code, text = self.run_cli("report", "--day", "2026-12-02")
        self.assertEqual(code, 0)
        self.assertIn("atlas,Atlas,120,840,14.3", text)


if __name__ == "__main__":
    unittest.main()
