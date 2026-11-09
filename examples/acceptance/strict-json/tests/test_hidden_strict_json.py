"""Hidden acceptance test: copied in only after the run, never shown to workers."""

import json
import math
import re
import unittest

from shop import strictjson
from shop.strictjson import StrictJSONError, loads


class StrictJsonAcceptance(unittest.TestCase):
    def assertError(self, text, code, line, column):
        with self.assertRaises(StrictJSONError, msg=repr(text)) as cm:
            loads(text)
        e = cm.exception
        self.assertEqual((e.code, e.line, e.column), (code, line, column), repr(text))

    def check_table(self, rows):
        for text, code, line, column in rows:
            with self.subTest(text=text[:40]):
                self.assertError(text, code, line, column)

    # ---------------------------------------------------------------- valid
    def test_valid_documents_match_stdlib(self):
        docs = [
            "null", "true", "false", "0", "-0", "-0.0", "0.5", "-12.5E2", "1e5", "1E+5", "1e-2", "1.5e300",
            "123456789012345678901234567890", "-98765432109876543210", '""', '"abc"', "[]", "{}",
            ' \t\r\n[ 1 ,\t2\n]\r\n ', '{"a":{"b":[1,{"c":null}]},"d":[]}',
            '"\\u00e9\\u00E9"', '"\\ud83d\\ude00"', '"\\uD83D\\uDE00"', '"\U0001F600"',
            '"\\"\\\\\\/\\b\\f\\n\\r\\t"', '"\\u0000"', '"  \x7f﻿"',
            '"\\ud7ff\\ue000"', '"\\udbff\\udc00\\udbff\\udfff"', '{"\\u0061":1}',
            '{"a":1,"b":{"a":2}}', '[{"a":1},{"a":2}]', '{"a":{"a":1}}', '{"a":1,"A":2}', '{"":1," ":2}',
            '[1,[2,[3,[4]]],{"k":[true,false,null]}]', '\n\n{\n  "x" : [ 1 , 2 ]\n}\n',
        ]
        for text in docs:
            with self.subTest(text=text[:40]):
                got = loads(text)
                want = json.loads(text)
                self.assertEqual(got, want)
                self.assertEqual(type(got), type(want))
                if isinstance(want, float):
                    self.assertEqual(math.copysign(1, got), math.copysign(1, want))

    def test_numbers_table(self):
        rows = [
            ("0", 0, int), ("-0", 0, int), ("7", 7, int), ("-7", -7, int), ("10", 10, int),
            ("0.5", 0.5, float), ("-0.25", -0.25, float), ("1e5", 100000.0, float), ("1E+5", 100000.0, float),
            ("1e-2", 0.01, float), ("0e0", 0.0, float), ("2.50", 2.5, float), ("1.0", 1.0, float),
            ("1e2", 100.0, float), ("12E0", 12.0, float),
        ]
        for text, value, typ in rows:
            with self.subTest(text=text):
                got = loads(text)
                self.assertEqual(got, value)
                self.assertIs(type(got), typ)

    def test_negative_zero_forms(self):
        self.assertIs(type(loads("-0")), int)
        self.assertEqual(loads("-0"), 0)
        z = loads("-0.0")
        self.assertIs(type(z), float)
        self.assertEqual(math.copysign(1.0, z), -1.0)
        self.assertEqual(math.copysign(1.0, loads("-0e5")), -1.0)
        self.assertEqual(math.copysign(1.0, loads("0.0")), 1.0)

    def test_big_integers_are_exact(self):
        big = "9" * 60
        self.assertEqual(loads(big), 10 ** 60 - 1)
        self.assertEqual(loads("-" + big), -(10 ** 60 - 1))
        self.assertEqual(loads("[9007199254740993]"), [9007199254740993])
        self.assertIs(type(loads(big)), int)

    def test_literals_are_real_singletons(self):
        self.assertIs(loads("true"), True)
        self.assertIs(loads("false"), False)
        self.assertIsNone(loads("null"))
        self.assertEqual(loads("[true,false,null]"), [True, False, None])

    def test_strings_and_escapes(self):
        rows = [
            ('"\\u00e9"', "é"), ('"\\u00E9"', "é"), ('"\\uD83D\\uDE00"', "\U0001F600"),
            ('"\\ud83d\\ude00x"', "\U0001F600x"), ('"a\\ud83d\\ude01\\ud83d\\ude00"', "a\U0001F601\U0001F600"),
            ('"\\udbff\\udfff"', "\U0010FFFF"), ('"\\udbff\\udc00"', "\U0010FC00"),
            ('"\\ud7ff\\ue000"', "퟿"),
            ('"\\"\\\\\\/\\b\\f\\n\\r\\t"', '"\\/\b\f\n\r\t'), ('"\\u0000"', "\x00"), ('"\\u001f"', "\x1f"),
            ('"  \x7f"', "  \x7f"), ('"\U0001F600"', "\U0001F600"),
            ('"\\u0041\\u0042"', "AB"), ('""', ""),
        ]
        for text, value in rows:
            with self.subTest(text=text):
                self.assertEqual(loads(text), value)
        self.assertEqual(len(loads('"\\ud83d\\ude00"')), 1)

    def test_nested_structures_and_key_order(self):
        doc = '{"z":[1,{"y":[]},"s"],"a":{"b":{"c":[[],{}]}},"m":null}'
        self.assertEqual(loads(doc), {"z": [1, {"y": []}, "s"], "a": {"b": {"c": [[], {}]}}, "m": None})
        self.assertEqual(list(loads(doc).keys()), ["z", "a", "m"])
        self.assertEqual(loads('{"\\u0061\\u0062":1}'), {"ab": 1})

    def test_whitespace_rules(self):
        self.assertEqual(loads(" \t\r\n1 \t\r\n"), 1)
        self.assertEqual(loads("[\r\n1\r\n,\r\n2\r\n]"), [1, 2])
        self.check_table([
            ("\x0c1", "unexpected_char", 1, 1),
            (" 1", "unexpected_char", 1, 1),
            ("1\x0c", "trailing_data", 1, 2),
            ("[1,\x0c2]", "unexpected_char", 1, 4),
            ("\x0b1", "unexpected_char", 1, 1),
            ("1 ", "trailing_data", 1, 2),
            (" ﻿1", "unexpected_char", 1, 2),
        ])

    def test_type_error_for_non_str(self):
        for bad in (b"1", None, 1, ["1"]):
            with self.subTest(bad=bad):
                with self.assertRaises(TypeError):
                    loads(bad)

    def test_error_is_value_error_with_attrs(self):
        with self.assertRaises(ValueError) as cm:
            loads("[1,]")
        self.assertIsInstance(cm.exception, StrictJSONError)
        self.assertEqual(
            (cm.exception.code, cm.exception.line, cm.exception.column), ("unexpected_char", 1, 4)
        )

    # --------------------------------------------------------------- errors
    def test_empty_and_whitespace_only(self):
        self.check_table([
            ("", "empty", 1, 1),
            ("   ", "empty", 1, 4),
            (" \n\t\r\n ", "empty", 3, 2),
            ("\n", "empty", 2, 1),
            ("\r", "empty", 1, 2),
        ])

    def test_bom(self):
        self.check_table([
            ("﻿", "bom", 1, 1),
            ("﻿1", "bom", 1, 1),
            ("﻿[1]", "bom", 1, 1),
            ("﻿\n\n[", "bom", 1, 1),
        ])

    def test_trailing_data(self):
        self.check_table([
            ("1 2", "trailing_data", 1, 3),
            ("[1]x", "trailing_data", 1, 4),
            ("{} {}", "trailing_data", 1, 4),
            ("1\n\n  x", "trailing_data", 3, 3),
            ("null,", "trailing_data", 1, 5),
            ("1]", "trailing_data", 1, 2),
            ("truex", "trailing_data", 1, 5),
            ("01", "trailing_data", 1, 2),
            ("-01", "trailing_data", 1, 3),
            ("1 // c", "trailing_data", 1, 3),
            ('"a""b"', "trailing_data", 1, 4),
            ("1\r\nx", "trailing_data", 2, 1),
            ("[]\n}", "trailing_data", 2, 1),
            ("0.5.5", "trailing_data", 1, 4),
        ])

    def test_unexpected_end(self):
        self.check_table([
            ("[", "unexpected_end", 1, 2),
            ("[1", "unexpected_end", 1, 3),
            ("[1,", "unexpected_end", 1, 4),
            ("{", "unexpected_end", 1, 2),
            ('{"a"', "unexpected_end", 1, 5),
            ('{"a":', "unexpected_end", 1, 6),
            ('{"a":1', "unexpected_end", 1, 7),
            ('{"a":1,', "unexpected_end", 1, 8),
            ('"abc', "unexpected_end", 1, 5),
            ('"abc\\', "unexpected_end", 1, 6),
            ('"\\u12', "unexpected_end", 1, 6),
            ("tru", "unexpected_end", 1, 4),
            ("n", "unexpected_end", 1, 2),
            ("-", "unexpected_end", 1, 2),
            ("1.", "unexpected_end", 1, 3),
            ("1e", "unexpected_end", 1, 3),
            ("1e+", "unexpected_end", 1, 4),
            ("[\n", "unexpected_end", 2, 1),
            ('{"a":\n  ', "unexpected_end", 2, 3),
            ('{"a":1,"a', "unexpected_end", 1, 10),
            ('[1,\r\n2,\r\n', "unexpected_end", 3, 1),
        ])

    def test_unexpected_char(self):
        self.check_table([
            ("@", "unexpected_char", 1, 1),
            ("+1", "unexpected_char", 1, 1),
            (".5", "unexpected_char", 1, 1),
            ("1.x", "unexpected_char", 1, 3),
            ("[1.]", "unexpected_char", 1, 4),
            ("-x", "unexpected_char", 1, 2),
            ("-Infinity", "unexpected_char", 1, 2),
            ("NaN", "unexpected_char", 1, 1),
            ("Infinity", "unexpected_char", 1, 1),
            ("'a'", "unexpected_char", 1, 1),
            ("[1,]", "unexpected_char", 1, 4),
            ("[,1]", "unexpected_char", 1, 2),
            ("[1 2]", "unexpected_char", 1, 4),
            ('{"a":1,}', "unexpected_char", 1, 8),
            ("{1:2}", "unexpected_char", 1, 2),
            ("{'a':1}", "unexpected_char", 1, 2),
            ('{"a" 1}', "unexpected_char", 1, 6),
            ('{"a":}', "unexpected_char", 1, 6),
            ("{,}", "unexpected_char", 1, 2),
            ("[01]", "unexpected_char", 1, 3),
            ("[1 // c\n]", "unexpected_char", 1, 4),
            ("[/* c */]", "unexpected_char", 1, 2),
            ("[1e]", "unexpected_char", 1, 4),
            ("1e+x", "unexpected_char", 1, 4),
            ("trux", "unexpected_char", 1, 4),
            ("nul1", "unexpected_char", 1, 4),
            ("True", "unexpected_char", 1, 1),
            ("[tru]", "unexpected_char", 1, 5),
            ('"\\x"', "unexpected_char", 1, 3),
            ('"\\a"', "unexpected_char", 1, 3),
            ('"\\u12G4"', "unexpected_char", 1, 6),
            ('"\\u 123"', "unexpected_char", 1, 4),
            ('"\\\n"', "unexpected_char", 1, 3),
            ("[\n  1,\n  ]", "unexpected_char", 3, 3),
            ('{\n"a": 1,\n"b" 2}', "unexpected_char", 3, 5),
            ("[1,\r\n2,\r\n]", "unexpected_char", 3, 1),
            ("[\r\n\r\n x", "unexpected_char", 3, 2),
            ("[1,\r]", "unexpected_char", 1, 5),
            ("[}", "unexpected_char", 1, 2),
            ('{"a":1]', "unexpected_char", 1, 7),
            ("[1,2}", "unexpected_char", 1, 5),
            ("{]", "unexpected_char", 1, 2),
        ])

    def test_unexpected_char_columns_count_characters(self):
        self.check_table([
            ('["\U0001F600", x]', "unexpected_char", 1, 7),
            ('["éé",\n"\U0001F600\U0001F600" x]', "unexpected_char", 2, 6),
        ])

    def test_control_characters_in_strings(self):
        self.check_table([
            ('"\n"', "control_char", 1, 2),
            ('"a\tb"', "control_char", 1, 3),
            ('"\x00"', "control_char", 1, 2),
            ('"\x1f"', "control_char", 1, 2),
            ('"ab\r\n"', "control_char", 1, 4),
            ('["x",\n"a\nb"]', "control_char", 2, 3),
            ('{"a\x01":1}', "control_char", 1, 4),
            ('"\U0001F600\x08"', "control_char", 1, 3),
        ])
        self.assertEqual(loads('"\x7f"'), "\x7f")
        self.assertEqual(loads('"\\b"'), "\b")

    def test_bad_surrogates(self):
        self.check_table([
            ('"\\ud800"', "bad_surrogate", 1, 2),
            ('"\\udc00"', "bad_surrogate", 1, 2),
            ('"x\\ude00"', "bad_surrogate", 1, 3),
            ('"\\ud83dx"', "bad_surrogate", 1, 2),
            ('"\\ud83d\\u0041"', "bad_surrogate", 1, 2),
            ('"\\ud83d\\ud83d"', "bad_surrogate", 1, 2),
            ('"\\ude00\\ud83d"', "bad_surrogate", 1, 2),
            ('"ab\\ud83d\\n"', "bad_surrogate", 1, 4),
            ('["\\ud83d\\ude00", "\\ud83d"]', "bad_surrogate", 1, 19),
            ('"\\udfff\\ude00"', "bad_surrogate", 1, 2),
            ('"\\ud83d\\u00zz"', "unexpected_char", 1, 12),
            ('"\\ud83d\\ude00\\ude00"', "bad_surrogate", 1, 14),
            ('{"\\ud83d":1}', "bad_surrogate", 1, 3),
        ])

    def test_duplicate_keys(self):
        self.check_table([
            ('{"a":1,"a":2}', "duplicate_key", 1, 8),
            ('{"a":1,"b":2,"a":3}', "duplicate_key", 1, 14),
            ('{"a":1,"\\u0061":2}', "duplicate_key", 1, 8),
            ('{"a":{"b":1,"b":2}}', "duplicate_key", 1, 13),
            ('{\n  "k": 1,\n  "k": 2\n}', "duplicate_key", 3, 3),
            ('{"a":1,"a":}', "duplicate_key", 1, 8),
            ('{"a":1,"a":tru}', "duplicate_key", 1, 8),
            ('{"a":{"x":1},"a":{"x":1}}', "duplicate_key", 1, 14),
            ('[{"a":1,"a":2}]', "duplicate_key", 1, 9),
            ('{"":1,"":2}', "duplicate_key", 1, 7),
            ('{"a":1,"a"', "duplicate_key", 1, 8),
            ('{"a":1,\r\n"a":2}', "duplicate_key", 2, 1),
        ])

    def test_duplicate_keys_only_within_one_object(self):
        self.assertEqual(loads('[{"a":1},{"a":2}]'), [{"a": 1}, {"a": 2}])
        self.assertEqual(loads('{"a":{"a":{"a":1}}}'), {"a": {"a": {"a": 1}}})
        self.assertEqual(loads('{"a":{"b":1},"c":{"b":2}}'), {"a": {"b": 1}, "c": {"b": 2}})
        self.assertEqual(loads('{"a":1,"A":2,"a ":3}'), {"a": 1, "A": 2, "a ": 3})

    def test_depth_limit_arrays(self):
        self.assertEqual(loads("[" * 64 + "]" * 64), json.loads("[" * 64 + "]" * 64))
        self.check_table([
            ("[" * 65 + "]" * 65, "too_deep", 1, 65),
            ("[" * 65, "too_deep", 1, 65),
            ("[" * 70 + "]" * 70, "too_deep", 1, 65),
            ("\n\n" + "[" * 65, "too_deep", 3, 65),
            ("[" * 64 + "x", "unexpected_char", 1, 65),
            ("[" * 64, "unexpected_end", 1, 65),
        ])

    def test_depth_limit_objects(self):
        ok = '{"a":' * 64 + "1" + "}" * 64
        self.assertEqual(loads(ok), json.loads(ok))
        self.check_table([
            ('{"a":' * 65 + "1" + "}" * 65, "too_deep", 1, 321),
            ('{"a":' * 65, "too_deep", 1, 321),
            ('{"a":' * 64 + "x", "unexpected_char", 1, 321),
        ])

    def test_depth_limit_mixed_and_siblings(self):
        ok = "[" * 63 + "{}" + "]" * 63
        self.assertEqual(loads(ok), json.loads(ok))
        ok2 = '[{"a":' * 32 + "1" + "}]" * 32
        self.assertEqual(loads(ok2), json.loads(ok2))
        sib = "[" + ",".join(["[]"] * 100) + "]"
        self.assertEqual(loads(sib), json.loads(sib))
        sib2 = "[" + ("[" * 63 + "]" * 63 + ",") * 5 + "[]]"
        self.assertEqual(loads(sib2), json.loads(sib2))
        self.check_table([
            ("[" * 64 + "{}" + "]" * 64, "too_deep", 1, 65),
            ("[" * 64 + '{"a":1}' + "]" * 64, "too_deep", 1, 65),
            ("[" + ("[" * 64 + "]" * 64 + ",") * 2 + "[]]", "too_deep", 1, 65),
            ('{"k":' + "[" * 64 + "]}", "too_deep", 1, 69),
        ])

    def test_first_problem_wins(self):
        self.check_table([
            ("﻿", "bom", 1, 1),
            ('[1, "a\nb" x', "control_char", 1, 7),
            ('[01, "\\q"]', "unexpected_char", 1, 3),
            ('{"a":1,"a":2,]', "duplicate_key", 1, 8),
            ('["\\ud83d", 01 x', "bad_surrogate", 1, 3),
            ("1 x \x00", "trailing_data", 1, 3),
        ])

    def test_module_does_not_import_json(self):
        with open(strictjson.__file__, encoding="utf-8") as fh:
            source = fh.read()
        self.assertIsNone(re.search(r"\bimport\s+json\b", source))
        self.assertIsNone(re.search(r"\bfrom\s+json\b", source))
        self.assertNotIn("__import__", source)


if __name__ == "__main__":
    unittest.main()
