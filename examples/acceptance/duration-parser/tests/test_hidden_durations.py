"""Hidden acceptance test: copied in only after the run, never shown to workers."""

import unittest

from shop.durations import parse_duration


class DurationParserAcceptance(unittest.TestCase):
    def test_valid_values(self):
        cases = {
            "1h30m": 5400,
            "2d": 172800,
            "90s": 90,
            "1d1h1m1s": 90061,
            "0s": 0,
            "00m": 0,
            "5m": 300,
            "1000d": 86_400_000,
            "2h5s": 7205,
        }
        for text, seconds in cases.items():
            with self.subTest(text=text):
                self.assertEqual(parse_duration(text), seconds)

    def test_surrounding_whitespace_is_stripped(self):
        self.assertEqual(parse_duration("  1h \n"), 3600)

    def test_invalid_values_raise_value_error(self):
        bad = [
            "",
            "   ",
            "5",  # no unit
            "h",  # no number
            "30m1h",  # wrong order
            "1h1h",  # repeated unit
            "1.5h",  # not an integer
            "-5s",  # sign
            "+5s",
            "1h 30m",  # inner whitespace
            "1H",  # uppercase unit
            "1w",  # unknown unit
            "1h30",  # trailing number
            "٣s",  # non-ASCII digit
            "1 h",
            "1h,30m",
            "m30",
        ]
        for text in bad:
            with self.subTest(text=text):
                with self.assertRaises(ValueError):
                    parse_duration(text)

    def test_non_string_rejected(self):
        for value in (None, 5, 1.5):
            with self.subTest(value=value):
                with self.assertRaises((TypeError, ValueError)):
                    parse_duration(value)


if __name__ == "__main__":
    unittest.main()
