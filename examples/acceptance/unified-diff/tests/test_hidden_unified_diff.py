"""Hidden acceptance test: copied in only after the run, never shown to workers."""

import itertools
import random
import re
import time
import unittest
from functools import lru_cache

from shop.difftool import apply_script, edit_script, unified


def oracle_script(a, b):
    """Independent implementation of the stated tie rule using an edit-distance recursion."""
    a, b = list(a), list(b)
    n, m = len(a), len(b)

    @lru_cache(maxsize=None)
    def cost(i, j):
        if i == n:
            return m - j
        if j == m:
            return n - i
        if a[i] == b[j]:
            return cost(i + 1, j + 1)
        return 1 + min(cost(i + 1, j), cost(i, j + 1))

    out, i, j = [], 0, 0
    while i < n or j < m:
        if i < n and j < m and a[i] == b[j]:
            out.append(("=", a[i]))
            i += 1
            j += 1
        elif i < n and (j == m or cost(i + 1, j) <= cost(i, j + 1)):
            out.append(("-", a[i]))
            i += 1
        else:
            out.append(("+", b[j]))
            j += 1
    return out


def all_minimal_scripts(a, b):
    """Brute force: every minimal script (tiny inputs only)."""
    found = []

    def rec(i, j, acc):
        if i == len(a) and j == len(b):
            found.append(list(acc))
            return
        if i < len(a) and j < len(b) and a[i] == b[j]:
            acc.append(("=", a[i]))
            rec(i + 1, j + 1, acc)
            acc.pop()
        if i < len(a):
            acc.append(("-", a[i]))
            rec(i + 1, j, acc)
            acc.pop()
        if j < len(b):
            acc.append(("+", b[j]))
            rec(i, j + 1, acc)
            acc.pop()

    rec(0, 0, [])
    best = min(sum(1 for op, _ in s if op != "=") for s in found)
    return [s for s in found if sum(1 for op, _ in s if op != "=") == best]


def lcs_len(a, b):
    prev = [0] * (len(b) + 1)
    for x in a:
        cur = [0]
        for j, y in enumerate(b):
            cur.append(prev[j] + 1 if x == y else max(prev[j + 1], cur[j]))
        prev = cur
    return prev[-1]


def block_ordered(script):
    """Inside every maximal run of non-'=' entries, no '-' follows a '+'."""
    seen_plus = False
    for op, _ in script:
        if op == "=":
            seen_plus = False
        elif op == "+":
            seen_plus = True
        elif seen_plus:
            return False
    return True


HDR = re.compile(r"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@$")


def patch(a, text):
    """Tiny patch applier written from the format rules, to cross-check unified()."""
    lines = text.split("\n")
    assert lines[-1] == ""
    lines = lines[:-1]
    out, pos, k = [], 0, 2
    while k < len(lines):
        m = HDR.match(lines[k])
        assert m, lines[k]
        sa, ca = int(m.group(1)), int(m.group(2) if m.group(2) is not None else 1)
        sb, cb = int(m.group(3)), int(m.group(4) if m.group(4) is not None else 1)
        k += 1
        start = sa if ca == 0 else sa - 1
        out.extend(a[pos:start])
        pos = start
        na = nb = 0
        while k < len(lines) and not lines[k].startswith("@@"):
            tag, body = lines[k][0], lines[k][1:]
            if tag in " -":
                assert a[pos] == body
                pos += 1
                na += 1
            if tag in " +":
                out.append(body)
                nb += 1
            k += 1
        assert (na, nb) == (ca, cb)
        # the b-side start must agree with what has been produced so far
        expected_sb = len(out) - nb + 1 if cb else len(out)
        assert sb == expected_sb, (sb, expected_sb)
    out.extend(a[pos:])
    return out


def numbered(n):
    return ["l%d" % i for i in range(1, n + 1)]


class EditScriptTests(unittest.TestCase):
    def test_empty_inputs(self):
        self.assertEqual(edit_script([], []), [])

    def test_identical(self):
        self.assertEqual(edit_script(["a", "b"], ["a", "b"]), [("=", "a"), ("=", "b")])

    def test_pure_insert_into_empty(self):
        self.assertEqual(edit_script([], ["x", "y"]), [("+", "x"), ("+", "y")])

    def test_pure_delete_to_empty(self):
        self.assertEqual(edit_script(["x", "y"], []), [("-", "x"), ("-", "y")])

    def test_replace_single(self):
        self.assertEqual(edit_script(["a"], ["b"]), [("-", "a"), ("+", "b")])

    def test_repeated_line_first_kept_when_deleting(self):
        self.assertEqual(edit_script(["x", "x"], ["x"]), [("=", "x"), ("-", "x")])

    def test_repeated_line_inserting(self):
        self.assertEqual(edit_script(["x"], ["x", "x"]), [("=", "x"), ("+", "x")])

    def test_block_minus_before_plus(self):
        self.assertEqual(
            edit_script(["p", "q"], ["r", "s"]),
            [("-", "p"), ("-", "q"), ("+", "r"), ("+", "s")],
        )

    def test_block_with_unequal_sizes(self):
        self.assertEqual(
            edit_script(["a", "b", "c"], ["x", "y"]),
            [("-", "a"), ("-", "b"), ("-", "c"), ("+", "x"), ("+", "y")],
        )

    def test_tie_prefers_delete(self):
        self.assertEqual(
            edit_script(["a", "b"], ["b", "a"]), [("-", "a"), ("=", "b"), ("+", "a")]
        )

    def test_swap_inside_context(self):
        self.assertEqual(
            edit_script(list("abcd"), list("acbd")),
            [("=", "a"), ("-", "b"), ("=", "c"), ("+", "b"), ("=", "d")],
        )

    def test_delete_around_match(self):
        self.assertEqual(
            edit_script(["a", "b", "a"], ["b"]), [("-", "a"), ("=", "b"), ("-", "a")]
        )

    def test_insert_around_match(self):
        self.assertEqual(
            edit_script(["b"], ["a", "b", "a"]), [("+", "a"), ("=", "b"), ("+", "a")]
        )

    def test_ties_with_repeated_prefix(self):
        self.assertEqual(
            edit_script(["a", "a", "b"], ["a", "b", "a"]),
            [("=", "a"), ("-", "a"), ("=", "b"), ("+", "a")],
        )

    def test_insert_middle(self):
        self.assertEqual(
            edit_script(["a", "c"], ["a", "b", "c"]), [("=", "a"), ("+", "b"), ("=", "c")]
        )

    def test_empty_string_lines_are_ordinary(self):
        self.assertEqual(
            edit_script(["", "a"], ["a", ""]), [("-", ""), ("=", "a"), ("+", "")]
        )

    def test_returns_tuples_in_list(self):
        s = edit_script(["a"], ["a", "b"])
        self.assertIsInstance(s, list)
        self.assertTrue(all(isinstance(t, tuple) and len(t) == 2 for t in s))

    def test_does_not_modify_inputs(self):
        a, b = ["a", "b"], ["b", "c"]
        edit_script(a, b)
        self.assertEqual((a, b), (["a", "b"], ["b", "c"]))

    def test_random_pairs_properties(self):
        rng = random.Random(99)
        for _ in range(200):
            alphabet = rng.choice(["ab", "abc", "abcd"])
            a = [rng.choice(alphabet) for _ in range(rng.randint(0, 9))]
            b = [rng.choice(alphabet) for _ in range(rng.randint(0, 9))]
            script = edit_script(a, b)
            self.assertEqual(apply_script(a, script), b, (a, b))
            edits = sum(1 for op, _ in script if op != "=")
            self.assertEqual(edits, len(a) + len(b) - 2 * lcs_len(a, b), (a, b))
            self.assertTrue(block_ordered(script), (a, b))
            self.assertEqual(script, oracle_script(a, b), (a, b))

    def test_tie_rule_picks_one_of_the_minimal_scripts(self):
        rng = random.Random(7)
        for _ in range(60):
            a = [rng.choice("ab") for _ in range(rng.randint(0, 6))]
            b = [rng.choice("ab") for _ in range(rng.randint(0, 6))]
            self.assertIn(edit_script(a, b), all_minimal_scripts(a, b), (a, b))

    def test_exhaustive_tiny_inputs(self):
        for la, lb in itertools.product(range(4), repeat=2):
            for a in itertools.product("ab", repeat=la):
                for b in itertools.product("ab", repeat=lb):
                    self.assertEqual(
                        edit_script(list(a), list(b)), oracle_script(a, b), (a, b)
                    )

    def test_performance_1500_lines(self):
        rng = random.Random(5)
        a = ["line %d" % rng.randint(0, 400) for _ in range(1500)]
        b = []
        for line in a:
            r = rng.random()
            if r < 0.04:
                continue  # deleted
            if r < 0.07:
                b.append("new %d" % rng.randint(0, 999))  # inserted before
            if 0.07 <= r < 0.10:
                b.append("chg %d" % rng.randint(0, 999))  # replaced
            else:
                b.append(line)
        start = time.time()
        script = edit_script(a, b)
        elapsed = time.time() - start
        self.assertLess(elapsed, 5.0)
        self.assertEqual(apply_script(a, script), b)
        edits = sum(1 for op, _ in script if op != "=")
        self.assertEqual(edits, len(a) + len(b) - 2 * lcs_len(a, b))


class ApplyScriptTests(unittest.TestCase):
    def test_applies_valid_script(self):
        script = [("=", "a"), ("-", "b"), ("+", "x"), ("=", "c")]
        self.assertEqual(apply_script(["a", "b", "c"], script), ["a", "x", "c"])

    def test_empty(self):
        self.assertEqual(apply_script([], []), [])

    def test_insert_only(self):
        self.assertEqual(apply_script([], [("+", "x")]), ["x"])

    def test_returns_list(self):
        self.assertIsInstance(apply_script(["a"], [("=", "a")]), list)

    def test_equal_mismatch(self):
        with self.assertRaises(ValueError):
            apply_script(["a"], [("=", "b")])

    def test_delete_mismatch(self):
        with self.assertRaises(ValueError):
            apply_script(["a", "b"], [("-", "b"), ("=", "a")])

    def test_script_does_not_consume_all(self):
        with self.assertRaises(ValueError):
            apply_script(["a", "b"], [("=", "a")])

    def test_empty_script_on_nonempty(self):
        with self.assertRaises(ValueError):
            apply_script(["a"], [])

    def test_script_longer_than_input(self):
        with self.assertRaises(ValueError):
            apply_script(["a"], [("=", "a"), ("-", "b")])
        with self.assertRaises(ValueError):
            apply_script([], [("=", "a")])

    def test_unknown_operation(self):
        with self.assertRaises(ValueError):
            apply_script(["a"], [("?", "a")])
        with self.assertRaises(ValueError):
            apply_script([], [("*", "a")])

    def test_wrong_order_in_a(self):
        with self.assertRaises(ValueError):
            apply_script(["a", "b"], [("=", "b"), ("=", "a")])


class UnifiedTests(unittest.TestCase):
    def test_identical_is_empty_string(self):
        self.assertEqual(unified(["a", "b"], ["a", "b"]), "")
        self.assertEqual(unified([], []), "")
        self.assertEqual(unified(["a"], ["a"], context=0), "")

    def test_insertion_into_empty_file(self):
        self.assertEqual(
            unified([], ["x", "y"]), "--- a\n+++ b\n@@ -0,0 +1,2 @@\n+x\n+y\n"
        )

    def test_insertion_at_start_context_zero(self):
        self.assertEqual(
            unified(["a"], ["x", "a"], context=0), "--- a\n+++ b\n@@ -0,0 +1 @@\n+x\n"
        )

    def test_insertion_at_start_with_context(self):
        self.assertEqual(
            unified(["a", "b"], ["x", "a", "b"], context=1),
            "--- a\n+++ b\n@@ -1 +1,2 @@\n+x\n a\n",
        )

    def test_delete_everything(self):
        self.assertEqual(
            unified(["x", "y"], []), "--- a\n+++ b\n@@ -1,2 +0,0 @@\n-x\n-y\n"
        )

    def test_delete_everything_single_line(self):
        self.assertEqual(unified(["x"], []), "--- a\n+++ b\n@@ -1 +0,0 @@\n-x\n")

    def test_deletion_after_line_three_context_zero(self):
        self.assertEqual(
            unified(list("abcd"), list("abc"), context=0),
            "--- a\n+++ b\n@@ -4 +3,0 @@\n-d\n",
        )

    def test_insertion_after_line_two_context_zero(self):
        self.assertEqual(
            unified(list("abc"), list("abxc"), context=0),
            "--- a\n+++ b\n@@ -2,0 +3 @@\n+x\n",
        )

    def test_single_line_counts_omitted(self):
        self.assertEqual(unified(["a"], ["b"]), "--- a\n+++ b\n@@ -1 +1 @@\n-a\n+b\n")

    def test_one_side_count_omitted_only(self):
        self.assertEqual(
            unified(list("abc"), list("abxy"), context=0),
            "--- a\n+++ b\n@@ -3 +3,2 @@\n-c\n+x\n+y\n",
        )

    def test_empty_line_content(self):
        self.assertEqual(unified([""], ["x"]), "--- a\n+++ b\n@@ -1 +1 @@\n-\n+x\n")

    def test_custom_names(self):
        out = unified(["a"], ["b"], name_a="old.txt", name_b="new.txt")
        self.assertTrue(out.startswith("--- old.txt\n+++ new.txt\n@@"))

    def test_default_context_three(self):
        a = numbered(10)
        b = list(a)
        b[4] = "X"
        expected = (
            "--- a\n+++ b\n@@ -2,7 +2,7 @@\n l2\n l3\n l4\n-l5\n+X\n l6\n l7\n l8\n"
        )
        self.assertEqual(unified(a, b), expected)

    def test_merge_when_gap_equals_twice_context(self):
        a = numbered(12)
        b = list(a)
        b[1], b[6] = "X2", "X7"  # unchanged l3..l6: gap 4 == 2*2
        expected = (
            "--- a\n+++ b\n@@ -1,9 +1,9 @@\n l1\n-l2\n+X2\n l3\n l4\n l5\n l6\n"
            "-l7\n+X7\n l8\n l9\n"
        )
        self.assertEqual(unified(a, b, context=2), expected)

    def test_split_when_gap_is_twice_context_plus_one(self):
        a = numbered(12)
        b = list(a)
        b[1], b[7] = "X2", "X8"  # unchanged l3..l7: gap 5 == 2*2+1
        expected = (
            "--- a\n+++ b\n@@ -1,4 +1,4 @@\n l1\n-l2\n+X2\n l3\n l4\n"
            "@@ -6,5 +6,5 @@\n l6\n l7\n-l8\n+X8\n l9\n l10\n"
        )
        self.assertEqual(unified(a, b, context=2), expected)

    def test_merge_context_one(self):
        a = ["1", "2", "3", "4", "5", "6"]
        b = ["X", "2", "3", "Y", "5", "6"]
        expected = "--- a\n+++ b\n@@ -1,5 +1,5 @@\n-1\n+X\n 2\n 3\n-4\n+Y\n 5\n"
        self.assertEqual(unified(a, b, context=1), expected)

    def test_split_context_one(self):
        a = ["1", "2", "3", "4", "5", "6", "7"]
        b = ["X", "2", "3", "4", "Y", "6", "7"]
        expected = (
            "--- a\n+++ b\n@@ -1,2 +1,2 @@\n-1\n+X\n 2\n"
            "@@ -4,3 +4,3 @@\n 4\n-5\n+Y\n 6\n"
        )
        self.assertEqual(unified(a, b, context=1), expected)

    def test_context_zero_one_hunk_per_block(self):
        a = numbered(5)
        b = ["l1", "X", "l3", "Y", "l5"]
        expected = "--- a\n+++ b\n@@ -2 +2 @@\n-l2\n+X\n@@ -4 +4 @@\n-l4\n+Y\n"
        self.assertEqual(unified(a, b, context=0), expected)

    def test_context_zero_adjacent_lines_one_block(self):
        a = numbered(5)
        b = ["l1", "X", "Y", "l4", "l5"]
        self.assertEqual(
            unified(a, b, context=0),
            "--- a\n+++ b\n@@ -2,2 +2,2 @@\n-l2\n-l3\n+X\n+Y\n",
        )

    def test_change_at_file_end(self):
        a = numbered(6)
        b = list(a)
        b[5] = "X"
        self.assertEqual(
            unified(a, b, context=2),
            "--- a\n+++ b\n@@ -4,3 +4,3 @@\n l4\n l5\n-l6\n+X\n",
        )

    def test_delete_last_line(self):
        self.assertEqual(
            unified(numbered(5), numbered(4), context=1),
            "--- a\n+++ b\n@@ -4,2 +4 @@\n l4\n-l5\n",
        )

    def test_append_line(self):
        self.assertEqual(
            unified(numbered(5), numbered(6), context=1),
            "--- a\n+++ b\n@@ -5 +5,2 @@\n l5\n+l6\n",
        )

    def test_change_at_file_start_context_two(self):
        a = numbered(6)
        b = list(a)
        b[0] = "X"
        self.assertEqual(
            unified(a, b, context=2),
            "--- a\n+++ b\n@@ -1,3 +1,3 @@\n-l1\n+X\n l2\n l3\n",
        )

    def test_ends_with_newline_and_has_no_marker(self):
        out = unified(["a", "b"], ["a", "c"])
        self.assertTrue(out.endswith("\n"))
        self.assertFalse(out.endswith("\n\n"))
        self.assertNotIn("No newline", out)

    def test_random_unified_roundtrip(self):
        rng = random.Random(99)
        for _ in range(200):
            alphabet = rng.choice(["ab", "abc"])
            a = [rng.choice(alphabet) for _ in range(rng.randint(0, 12))]
            b = [rng.choice(alphabet) for _ in range(rng.randint(0, 12))]
            context = rng.choice([0, 1, 2, 3])
            text = unified(a, b, context=context)
            if a == b:
                self.assertEqual(text, "")
                continue
            self.assertEqual(patch(a, text), b, (a, b, context, text))
            # hunks must be separated by more than 2*context unchanged lines
            script = oracle_script(a, b)
            blocks = [k for k, (op, _) in enumerate(script) if op != "="]
            splits = sum(
                1 for x, y in zip(blocks, blocks[1:]) if y - x - 1 > 2 * context
            )
            self.assertEqual(text.count("\n@@ "), splits + 1, (a, b, context, text))


if __name__ == "__main__":
    unittest.main()
