"""Hidden acceptance test: copied in only after the run, never shown to workers."""

import os
import tempfile
import unittest

from shop.report import orders_to_csv, write_csv


class CsvExportAcceptance(unittest.TestCase):
    def test_header_and_plain_rows_use_crlf(self):
        text = orders_to_csv([{"id": "o1", "customer": "Ann", "total": 12.5}])
        self.assertEqual(text, "id,customer,total\r\no1,Ann,12.50\r\n")

    def test_empty_list_is_header_only(self):
        self.assertEqual(orders_to_csv([]), "id,customer,total\r\n")

    def test_quotes_commas_and_newlines(self):
        orders = [
            {"id": "o2", "customer": 'Smith, "Bob"', "total": 1.0},
            {"id": "o3", "customer": "line1\nline2", "total": 2.0},
            {"id": "o4", "customer": "cr\rhere", "total": 3.0},
        ]
        self.assertEqual(
            orders_to_csv(orders),
            'id,customer,total\r\n'
            'o2,"Smith, ""Bob""",1.00\r\n'
            'o3,"line1\nline2",2.00\r\n'
            'o4,"cr\rhere",3.00\r\n',
        )

    def test_none_is_empty_and_unicode_preserved(self):
        orders = [{"id": "o5", "customer": None, "total": 0}, {"id": "o6", "customer": "علی ☕", "total": 7.25}]
        text = orders_to_csv(orders)
        self.assertIn("o5,,0.00\r\n", text)
        self.assertIn("o6,علی ☕,7.25\r\n", text)

    def test_id_with_comma_is_quoted_too(self):
        self.assertIn('"a,b",x,1.00', orders_to_csv([{"id": "a,b", "customer": "x", "total": 1}]))

    def test_write_csv_bytes_are_exact_and_returns_count(self):
        orders = [{"id": "o1", "customer": "علی", "total": 1.5}, {"id": "o2", "customer": "B", "total": 2}]
        with tempfile.TemporaryDirectory() as tmp:
            path = os.path.join(tmp, "orders.csv")
            self.assertEqual(write_csv(orders, path), 2)
            with open(path, "rb") as handle:
                data = handle.read()
        # utf-8, CRLF exactly once per line (no newline translation)
        self.assertEqual(data, orders_to_csv(orders).encode("utf-8"))
        self.assertNotIn(b"\r\r", data)


if __name__ == "__main__":
    unittest.main()
