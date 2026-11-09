"""Hidden acceptance test: copied in only after the run, never shown to workers.

Expected values were derived by hand. Calendar anchors: 2024-01-01 is a Monday,
2024-09-01 a Sunday, 2024-10-01 a Tuesday, 2024-02-05 a Monday.
"""

import unittest
from datetime import datetime as D, timezone

from shop.cron import next_run


class CronNextAcceptance(unittest.TestCase):
    def chain(self, expr, start, expected):
        """Each expected value must be the next run after the previous one."""
        cur = start
        for exp in expected:
            cur = next_run(expr, cur)
            self.assertEqual(cur, exp, "%r after chain step" % expr)

    def test_every_minute_and_rollovers(self):
        cases = [
            (D(2024, 1, 1, 0, 0), D(2024, 1, 1, 0, 1)),
            (D(2024, 1, 1, 0, 0, 30, 500), D(2024, 1, 1, 0, 1)),
            (D(2024, 12, 31, 23, 59), D(2025, 1, 1, 0, 0)),
            (D(2024, 2, 28, 23, 59), D(2024, 2, 29, 0, 0)),
            (D(2023, 2, 28, 23, 59), D(2023, 3, 1, 0, 0)),
            (D(2024, 4, 30, 23, 59, 59), D(2024, 5, 1, 0, 0)),
        ]
        for after, exp in cases:
            self.assertEqual(next_run("* * * * *", after), exp)

    def test_result_shape(self):
        r = next_run("30 12 * * *", D(2024, 1, 1, 0, 0, 59, 999999))
        self.assertEqual((r.second, r.microsecond), (0, 0))
        self.assertIsNone(r.tzinfo)
        self.assertIsInstance(r, D)

    def test_strictly_after_and_seconds_ignored(self):
        e = "30 12 * * *"
        self.assertEqual(next_run(e, D(2024, 1, 1, 12, 30)), D(2024, 1, 2, 12, 30))
        self.assertEqual(next_run(e, D(2024, 1, 1, 12, 30, 45)), D(2024, 1, 2, 12, 30))
        self.assertEqual(next_run(e, D(2024, 1, 1, 12, 29, 59, 999999)), D(2024, 1, 1, 12, 30))
        self.assertEqual(next_run(e, D(2024, 1, 1, 12, 31)), D(2024, 1, 2, 12, 30))
        self.assertEqual(next_run("0 0 * * *", D(2024, 1, 1, 0, 0)), D(2024, 1, 2, 0, 0))
        self.assertEqual(next_run("59 23 31 12 *", D(2024, 12, 31, 23, 59)), D(2025, 12, 31, 23, 59))

    def test_minute_steps(self):
        self.chain("5/15 * * * *", D(2024, 1, 1, 10, 0),
                   [D(2024, 1, 1, 10, 5), D(2024, 1, 1, 10, 20), D(2024, 1, 1, 10, 35),
                    D(2024, 1, 1, 10, 50), D(2024, 1, 1, 11, 5)])
        self.chain("*/15 * * * *", D(2024, 1, 1, 10, 46),
                   [D(2024, 1, 1, 11, 0), D(2024, 1, 1, 11, 15)])
        self.chain("10-20/5 * * * *", D(2024, 1, 1, 10, 0),
                   [D(2024, 1, 1, 10, 10), D(2024, 1, 1, 10, 15), D(2024, 1, 1, 10, 20),
                    D(2024, 1, 1, 11, 10)])
        self.chain("0-30/20 * * * *", D(2024, 1, 1, 10, 0),
                   [D(2024, 1, 1, 10, 20), D(2024, 1, 1, 11, 0)])
        self.chain("*/45 * * * *", D(2024, 1, 1, 10, 0),
                   [D(2024, 1, 1, 10, 45), D(2024, 1, 1, 11, 0)])
        self.chain("*/100 * * * *", D(2024, 1, 1, 10, 0), [D(2024, 1, 1, 11, 0)])
        self.assertEqual(next_run("5/60 * * * *", D(2024, 1, 1, 10, 5)), D(2024, 1, 1, 11, 5))
        self.assertEqual(next_run("59/1 * * * *", D(2024, 1, 1, 10, 0)), D(2024, 1, 1, 10, 59))
        self.assertEqual(next_run("50/7 * * * *", D(2024, 1, 1, 10, 50)), D(2024, 1, 1, 10, 57))
        self.assertEqual(next_run("50/7 * * * *", D(2024, 1, 1, 10, 57)), D(2024, 1, 1, 11, 50))

    def test_hour_steps_and_lists(self):
        self.chain("0 */6 * * *", D(2024, 1, 1, 5, 0),
                   [D(2024, 1, 1, 6, 0), D(2024, 1, 1, 12, 0), D(2024, 1, 1, 18, 0),
                    D(2024, 1, 2, 0, 0)])
        self.chain("0 22/2 * * *", D(2024, 1, 1, 21, 0),
                   [D(2024, 1, 1, 22, 0), D(2024, 1, 2, 22, 0)])
        self.chain("15,45 8-10 * * *", D(2024, 1, 1, 10, 45),
                   [D(2024, 1, 2, 8, 15), D(2024, 1, 2, 8, 45), D(2024, 1, 2, 9, 15)])
        self.chain("1,10-20/5,59 * * * *", D(2024, 1, 1, 10, 1),
                   [D(2024, 1, 1, 10, 10), D(2024, 1, 1, 10, 15), D(2024, 1, 1, 10, 20),
                    D(2024, 1, 1, 10, 59), D(2024, 1, 1, 11, 1)])
        self.chain("30,10,10 * * * *", D(2024, 1, 1, 10, 0),
                   [D(2024, 1, 1, 10, 10), D(2024, 1, 1, 10, 30)])

    def test_number_forms_and_whitespace(self):
        self.assertEqual(next_run("05 09 * * *", D(2024, 1, 1, 0, 0)), D(2024, 1, 1, 9, 5))
        self.assertEqual(next_run("  0   12 \t * *  * ", D(2024, 1, 1, 0, 0)), D(2024, 1, 1, 12, 0))
        self.assertEqual(next_run("0 12 * * *\n", D(2024, 1, 1, 0, 0)), D(2024, 1, 1, 12, 0))
        self.assertEqual(next_run("0 0 01 01 *", D(2024, 1, 1, 0, 0)), D(2025, 1, 1))

    def test_dom_dow_or_rule(self):
        # 2024-09-01 is Sunday; Fridays in Sept: 6, 13, 20, 27. Oct 4 and 11 are Fridays.
        self.chain("0 0 13 * fri", D(2024, 9, 1, 0, 0),
                   [D(2024, 9, 6), D(2024, 9, 13), D(2024, 9, 20), D(2024, 9, 27),
                    D(2024, 10, 4), D(2024, 10, 11), D(2024, 10, 13), D(2024, 10, 18)])
        self.chain("0 0 1 * mon", D(2024, 1, 1, 0, 0),
                   [D(2024, 1, 8), D(2024, 1, 15), D(2024, 1, 22), D(2024, 1, 29),
                    D(2024, 2, 1), D(2024, 2, 5)])
        self.chain("0 0 1 1 mon", D(2024, 1, 29, 0, 0), [D(2025, 1, 1), D(2025, 1, 6)])
        self.assertEqual(next_run("0 0 1 JaN MoN", D(2024, 1, 1, 0, 0)), D(2024, 1, 8))
        # month must still match: Mondays and the 15th only count inside June
        self.assertEqual(next_run("0 0 15 6 mon", D(2024, 1, 1, 0, 0)), D(2024, 6, 3))
        # OR rule makes an otherwise impossible date possible
        self.assertEqual(next_run("0 0 30 2 mon", D(2024, 2, 1, 0, 0)), D(2024, 2, 5))

    def test_only_one_restricted(self):
        self.assertEqual(next_run("0 0 * * 1", D(2024, 1, 1, 0, 0)), D(2024, 1, 8))
        self.assertEqual(next_run("0 0 15 * *", D(2024, 1, 1, 0, 0)), D(2024, 1, 15))
        self.assertEqual(next_run("0 0 15 * *", D(2024, 1, 15, 0, 0)), D(2024, 2, 15))
        self.assertEqual(next_run("0 0 * * *", D(2024, 1, 1, 0, 0)), D(2024, 1, 2))

    def test_star_step_counts_as_restricted(self):
        # dom */2 = odd days; OR with Monday
        self.chain("0 0 */2 * mon", D(2024, 1, 1, 0, 0),
                   [D(2024, 1, 3), D(2024, 1, 5), D(2024, 1, 7), D(2024, 1, 8), D(2024, 1, 9)])
        # dow */2 = sun,tue,thu,sat with dom unrestricted
        self.chain("0 0 * * */2", D(2024, 1, 1, 0, 0),
                   [D(2024, 1, 2), D(2024, 1, 4), D(2024, 1, 6), D(2024, 1, 7), D(2024, 1, 9)])
        # "*/1", "1-31" and "*,5" are restricted, so OR makes every day match
        for dom in ("*/1", "1-31", "*,5"):
            self.assertEqual(next_run("0 0 %s * mon" % dom, D(2024, 1, 1, 0, 0)), D(2024, 1, 2), dom)
        self.assertEqual(next_run("0 0 * * */1", D(2024, 1, 1, 0, 0)), D(2024, 1, 2))
        self.chain("0 0 */10 * *", D(2024, 1, 21, 0, 0), [D(2024, 1, 31), D(2024, 2, 1), D(2024, 2, 11)])
        self.assertEqual(next_run("0 0 */10 * *", D(2024, 4, 21, 0, 0)), D(2024, 5, 1))

    def test_sunday_zero_and_seven(self):
        for dow in ("0", "7", "sun", "SUN", "Sun", "7-7", "0-0", "7/3"):
            self.assertEqual(next_run("0 0 * * " + dow, D(2024, 1, 1, 0, 0)), D(2024, 1, 7), dow)
        self.chain("0 0 * * 5-7", D(2024, 1, 1, 0, 0),
                   [D(2024, 1, 5), D(2024, 1, 6), D(2024, 1, 7), D(2024, 1, 12)])
        self.chain("0 0 * * fri-7", D(2024, 1, 1, 0, 0),
                   [D(2024, 1, 5), D(2024, 1, 6), D(2024, 1, 7), D(2024, 1, 12)])
        self.chain("0 0 * * 6,7", D(2024, 1, 1, 0, 0), [D(2024, 1, 6), D(2024, 1, 7), D(2024, 1, 13)])
        self.chain("0 0 * * 0-7", D(2024, 1, 1, 0, 0), [D(2024, 1, 2), D(2024, 1, 3)])
        self.chain("0 0 * * sun-sat", D(2024, 1, 6, 0, 0), [D(2024, 1, 7), D(2024, 1, 8)])
        # 5/2 runs 5..7 step 2 = fri, sun ; */3 = sun, wed, sat
        self.chain("0 0 * * 5/2", D(2024, 1, 1, 0, 0), [D(2024, 1, 5), D(2024, 1, 7), D(2024, 1, 12)])
        self.chain("0 0 * * */3", D(2024, 1, 1, 0, 0),
                   [D(2024, 1, 3), D(2024, 1, 6), D(2024, 1, 7), D(2024, 1, 10)])

    def test_weekday_names(self):
        self.chain("0 0 * * mon-fri", D(2024, 1, 5, 0, 0), [D(2024, 1, 8), D(2024, 1, 9)])
        self.chain("0 0 * * MON-FRI/2", D(2024, 1, 1, 0, 0),
                   [D(2024, 1, 3), D(2024, 1, 5), D(2024, 1, 8)])
        self.chain("0 0 * * sat,Mon", D(2024, 1, 1, 0, 0), [D(2024, 1, 6), D(2024, 1, 8)])
        self.chain("0 0 * * tue-thu,sat", D(2024, 1, 1, 0, 0),
                   [D(2024, 1, 2), D(2024, 1, 3), D(2024, 1, 4), D(2024, 1, 6), D(2024, 1, 9)])

    def test_month_names_and_steps(self):
        self.chain("0 0 1 FEB,dec *", D(2024, 1, 1, 0, 0),
                   [D(2024, 2, 1), D(2024, 12, 1), D(2025, 2, 1)])
        self.chain("0 0 1 */5 *", D(2024, 6, 1, 0, 0), [D(2024, 11, 1), D(2025, 1, 1), D(2025, 6, 1)])
        self.chain("0 0 1 mar/4 *", D(2024, 3, 1, 0, 0), [D(2024, 7, 1), D(2024, 11, 1), D(2025, 3, 1)])
        self.chain("0 0 1 6-8 *", D(2024, 8, 1, 0, 0), [D(2025, 6, 1), D(2025, 7, 1)])
        self.chain("0 0 1 jun-aug *", D(2024, 8, 1, 0, 0), [D(2025, 6, 1)])
        self.chain("0 0 1 1-mar *", D(2024, 1, 1, 0, 0), [D(2024, 2, 1), D(2024, 3, 1), D(2025, 1, 1)])
        self.chain("0 0 1 */3 *", D(2024, 10, 1, 0, 0), [D(2025, 1, 1), D(2025, 4, 1)])
        self.assertEqual(next_run("0 0 1 1 *", D(2024, 1, 1, 0, 0)), D(2025, 1, 1))
        self.assertEqual(next_run("0 0 1 1 *", D(2023, 12, 31, 23, 59)), D(2024, 1, 1))

    def test_short_months(self):
        self.chain("0 0 31 * *", D(2024, 1, 31, 0, 0),
                   [D(2024, 3, 31), D(2024, 5, 31), D(2024, 7, 31), D(2024, 8, 31),
                    D(2024, 10, 31), D(2024, 12, 31), D(2025, 1, 31), D(2025, 3, 31)])
        self.chain("0 0 30 * *", D(2024, 1, 30, 0, 0), [D(2024, 3, 30), D(2024, 4, 30)])
        self.assertEqual(next_run("0 0 29 * *", D(2023, 1, 29, 0, 0)), D(2023, 3, 29))
        self.assertEqual(next_run("0 0 29 * *", D(2024, 1, 29, 0, 0)), D(2024, 2, 29))
        self.assertEqual(next_run("0 0 30,31 2,4 *", D(2024, 1, 1, 0, 0)), D(2024, 4, 30))

    def test_leap_day(self):
        e = "0 0 29 2 *"
        self.assertEqual(next_run(e, D(2024, 1, 1, 0, 0)), D(2024, 2, 29))
        self.assertEqual(next_run(e, D(2024, 2, 29, 0, 0)), D(2028, 2, 29))
        self.assertEqual(next_run(e, D(2021, 1, 1, 0, 0)), D(2024, 2, 29))
        self.assertEqual(next_run(e, D(2096, 3, 1, 0, 0)), D(2104, 2, 29))
        self.assertEqual(next_run(e, D(2100, 1, 1, 0, 0)), D(2104, 2, 29))
        self.assertEqual(next_run(e, D(1897, 1, 1, 0, 0)), D(1904, 2, 29))
        self.assertEqual(next_run("30 6 29 2 *", D(2024, 2, 29, 6, 29)), D(2024, 2, 29, 6, 30))

    def test_never_matches(self):
        for e in ("0 0 30 2 *", "0 0 31 4 *", "0 0 31 4,6,9,11 *", "0 0 30-31 2 *",
                  "* * 30 feb *"):
            with self.assertRaises(ValueError, msg=e):
                next_run(e, D(2024, 1, 1, 0, 0))
        with self.assertRaises(ValueError):
            next_run("0 0 31 11 *", D(2024, 1, 1))

    def test_invalid_expressions(self):
        bad = [
            "", "   ", "* * * *", "* * * * * *", "@daily", "0 0 1 1",
            "60 * * * *", "* 24 * * *", "* * 0 * *", "* * 32 * *", "* * * 0 *",
            "* * * 13 *", "* * * * 8", "0-60 * * * *", "* 0-24 * * *", "* * 1-32 * *",
            "* * * 1-13 *", "* * * * 0-8", "99999999999999999999 * * * *",
            "*/0 * * * *", "0/0 * * * *", "1-5/0 * * * *", "* * * * */0",
            "5-1 * * * *", "* * * * sat-sun", "* * * * fri-sun", "* * * * 7-0", "* * * dec-jan *",
            "1,,2 * * * *", ",1 * * * *", "1, * * * *", "*,, * * * *",
            "* * * foo *", "* * * * funday", "* * * janu *", "* * * * monday",
            "mon * * * *", "* mon * * *", "* * mon * *", "* * * mon *", "* * * * jan",
            "-1 * * * *", "+5 * * * *", "1.5 * * * *", "*/-1 * * * *", "*/ * * * *",
            "5/ * * * *", "1-/2 * * * *", "-5 * * * *", "1-2-3 * * * *", "*-5 * * * *",
            "** * * * *", "*/2/3 * * * *", "1-5/2/3 * * * *", "?  * * * *", "L * * * *",
            "0x5 * * * *", "1e1 * * * *", "*/1.5 * * * *", "*/+2 * * * *",
        ]
        for e in bad:
            with self.assertRaises(ValueError, msg=repr(e)):
                next_run(e, D(2024, 1, 1, 0, 0))

    def test_timezone_aware_rejected(self):
        with self.assertRaises(ValueError):
            next_run("* * * * *", D(2024, 1, 1, 0, 0, tzinfo=timezone.utc))


if __name__ == "__main__":
    unittest.main()
