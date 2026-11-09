"""Hidden acceptance test: copied in only after the run, never shown to workers."""

import random
import unittest

from shop.scheduler import CycleError, schedule


def A(s):
    return [tuple(x) for x in s.assignments]


def oracle(tasks, workers):
    """Plain step-by-step simulation, one integer time unit at a time."""
    names = list(tasks)
    dur = {n: tasks[n][0] for n in names}
    deps = {n: set(tasks[n][1]) for n in names}
    # critical path by relaxation until nothing changes
    cp = dict(dur)
    changed = True
    while changed:
        changed = False
        for n in names:
            for d in deps[n]:
                if cp[d] < dur[d] + cp[n]:
                    cp[d] = dur[d] + cp[n]
                    changed = True
    busy_until = [0] * workers
    start, end = {}, {}
    out = []
    t = 0
    while len(start) < len(names):
        ready = [n for n in names if n not in start and all(d in end and end[d] <= t for d in deps[n])]
        ready.sort(key=lambda n: (-cp[n], -dur[n], n))
        for w in range(workers):
            if ready and busy_until[w] <= t:
                n = ready.pop(0)
                start[n], end[n] = t, t + dur[n]
                busy_until[w] = end[n]
                out.append((n, w, t, t + dur[n]))
        t += 1
    out.sort(key=lambda a: (a[2], a[1]))
    return out


class SchedulerAcceptance(unittest.TestCase):
    def test_diamond_two_workers(self):
        tasks = {"a": (2, []), "b": (3, ["a"]), "c": (1, ["a"]), "d": (2, ["b", "c"])}
        s = schedule(tasks, 2)
        self.assertEqual(A(s), [("a", 0, 0, 2), ("b", 0, 2, 5), ("c", 1, 2, 3), ("d", 0, 5, 7)])
        self.assertEqual(s.makespan, 7)

    def test_diamond_one_worker_pure_priority(self):
        tasks = {"a": (2, []), "b": (3, ["a"]), "c": (1, ["a"]), "d": (2, ["b", "c"])}
        s = schedule(tasks, 1)
        self.assertEqual(A(s), [("a", 0, 0, 2), ("b", 0, 2, 5), ("c", 0, 5, 6), ("d", 0, 6, 8)])
        self.assertEqual(s.makespan, 8)

    def test_more_workers_than_parallelism(self):
        tasks = {"a": (2, []), "b": (3, ["a"]), "c": (1, ["a"]), "d": (2, ["b", "c"])}
        self.assertEqual(A(schedule(tasks, 9)), A(schedule(tasks, 2)))
        self.assertEqual(schedule(tasks, 9).makespan, 7)

    def test_independent_tasks_ties_on_name_and_duration(self):
        tasks = {"x": (2, []), "y": (2, []), "z": (2, []), "w": (3, [])}
        s = schedule(tasks, 2)
        # w has the longest path; then x, y, z tie on everything but the name
        self.assertEqual(A(s), [("w", 0, 0, 3), ("x", 1, 0, 2), ("y", 1, 2, 4), ("z", 0, 3, 5)])
        self.assertEqual(s.makespan, 5)

    def test_name_tie_is_plain_string_comparison(self):
        tasks = {"b": (1, []), "B": (1, []), "a": (1, []), "10": (1, []), "9": (1, [])}
        s = schedule(tasks, 1)
        self.assertEqual([a[0] for a in A(s)], ["10", "9", "B", "a", "b"])

    def test_tie_on_critical_path_broken_by_duration(self):
        # a: cp 1+3 = 4 (duration 1); b: cp 4 (duration 4); c: cp 3
        tasks = {"a": (1, []), "b": (4, []), "c": (3, ["a"])}
        s = schedule(tasks, 1)
        self.assertEqual(A(s), [("b", 0, 0, 4), ("a", 0, 4, 5), ("c", 0, 5, 8)])

    def test_critical_path_uses_duration_not_hops(self):
        # chain P-Q-R is three hops but only 3 long; X-Y is two hops but 6 long
        tasks = {"P": (1, []), "Q": (1, ["P"]), "R": (1, ["Q"]), "X": (5, []), "Y": (1, ["X"])}
        s = schedule(tasks, 1)
        # cp: X 6, P 3, Q 2, R 1, Y 1.  After X: P(3) first; then Q(2); then R and Y tie
        # on cp 1 and duration 1, so the name decides: R before Y.
        self.assertEqual([a[0] for a in A(s)], ["X", "P", "Q", "R", "Y"])
        self.assertEqual(s.makespan, 9)

    def test_critical_path_takes_max_over_dependents_not_sum(self):
        # cp(a) = 1 + max(5, 4) = 6, not 10; so the independent e (8) goes first
        tasks = {"a": (1, []), "b": (5, ["a"]), "c": (4, ["a"]), "e": (8, [])}
        s = schedule(tasks, 1)
        self.assertEqual(A(s), [("e", 0, 0, 8), ("a", 0, 8, 9), ("b", 0, 9, 14), ("c", 0, 14, 18)])

    def test_zero_gap_restart_at_completion_time(self):
        tasks = {"a": (2, []), "b": (2, []), "c": (4, ["a"])}
        s = schedule(tasks, 2)
        # a and b both end at 2; c becomes ready at 2 and takes the lowest idle worker, 0
        self.assertEqual(A(s), [("a", 0, 0, 2), ("b", 1, 0, 2), ("c", 0, 2, 6)])
        self.assertEqual(s.makespan, 6)

    def test_worker_finishing_at_t_is_idle_at_t(self):
        tasks = {"a": (3, []), "b": (3, []), "c": (1, ["a"]), "d": (1, [])}
        s = schedule(tasks, 2)
        # cp: a 4, c 1, b 3, d 1.  t0: a->0, b->1.  t3: both free; c then d (name): c->0, d->1
        self.assertEqual(A(s), [("a", 0, 0, 3), ("b", 1, 0, 3), ("c", 0, 3, 4), ("d", 1, 3, 4)])

    def test_long_chain_vs_many_short_tasks(self):
        tasks = {"c1": (3, []), "c2": (3, ["c1"]), "c3": (3, ["c2"])}
        for i in range(1, 5):
            tasks["s%d" % i] = (1, [])
        s = schedule(tasks, 2)
        self.assertEqual(
            A(s),
            [
                ("c1", 0, 0, 3), ("s1", 1, 0, 1), ("s2", 1, 1, 2), ("s3", 1, 2, 3),
                ("c2", 0, 3, 6), ("s4", 1, 3, 4), ("c3", 0, 6, 9),
            ],
        )
        self.assertEqual(s.makespan, 9)

    def test_long_chain_vs_many_short_tasks_one_worker(self):
        tasks = {"c1": (3, []), "c2": (3, ["c1"]), "c3": (3, ["c2"])}
        for i in range(1, 5):
            tasks["s%d" % i] = (1, [])
        s = schedule(tasks, 1)
        self.assertEqual([a[0] for a in A(s)], ["c1", "c2", "c3", "s1", "s2", "s3", "s4"])
        self.assertEqual(s.makespan, 13)

    def test_tricky_staggered_workers(self):
        # Derivation, 3 workers.
        # cp: D=1, E=4, F=2, B=2+max(D1,F2)=4, C=3+max(E4,F2)=7, A=5.
        # Priority: C(7), A(5), E(4, dur 4), B(4, dur 2), F(2), D(1).
        # t0 ready A,B,C -> C w0 (0-3), A w1 (0-5), B w2 (0-2).
        # t2 B ends; ready D only -> w2 (2-3).
        # t3 C and D end; ready E,F; idle w0,w2 (w1 busy) -> E w0 (3-7), F w2 (3-5).
        # t5 nothing ready; t7 E ends.
        tasks = {
            "A": (5, []), "B": (2, []), "C": (3, []),
            "D": (1, ["B"]), "E": (4, ["C"]), "F": (2, ["B", "C"]),
        }
        s = schedule(tasks, 3)
        self.assertEqual(
            A(s),
            [("C", 0, 0, 3), ("A", 1, 0, 5), ("B", 2, 0, 2), ("D", 2, 2, 3), ("E", 0, 3, 7), ("F", 2, 3, 5)],
        )
        self.assertEqual(s.makespan, 7)

    def test_lowest_idle_worker_gets_best_ready_task(self):
        # Derivation, 2 workers.
        # cp: m=6, g=2+6=8, h=4+1=5, k=1, i=1.
        # t0 ready g,h,i -> g w0 (0-2), h w1 (0-4); i waits (no idle worker).
        # t2 w0 idle: ready m(cp 6) and i(cp 1): m -> w0 (2-8).
        # t4 h ends, w1 idle: ready k(cp1,dur1) and i(cp1,dur1): name i first -> w1 (4-5).
        # t5 w1 idle: k -> w1 (5-6).  Makespan 8.
        tasks = {
            "g": (2, []), "h": (4, []), "i": (1, []),
            "k": (1, ["h"]), "m": (6, ["g"]),
        }
        s = schedule(tasks, 2)
        self.assertEqual(
            A(s),
            [("g", 0, 0, 2), ("h", 1, 0, 4), ("m", 0, 2, 8), ("i", 1, 4, 5), ("k", 1, 5, 6)],
        )
        self.assertEqual(s.makespan, 8)

    def test_join_after_three_equal_tasks_uses_worker_zero(self):
        tasks = {"m": (1, []), "n": (1, []), "o": (1, []), "p": (1, ["m", "n", "o"])}
        s = schedule(tasks, 3)
        self.assertEqual(A(s), [("m", 0, 0, 1), ("n", 1, 0, 1), ("o", 2, 0, 1), ("p", 0, 1, 2)])

    def test_duplicate_dependencies_ignored(self):
        t1 = {"m": (1, []), "n": (1, []), "p": (2, ["m", "m", "n", "n", "m"])}
        t2 = {"m": (1, []), "n": (1, []), "p": (2, ["n", "m"])}
        self.assertEqual(A(schedule(t1, 2)), A(schedule(t2, 2)))
        self.assertEqual(A(schedule(t1, 2)), [("m", 0, 0, 1), ("n", 1, 0, 1), ("p", 0, 1, 3)])

    def test_tuples_accepted_and_input_not_mutated(self):
        tasks = {"a": (1, ("b", "b")), "b": (2, [])}
        snapshot = {k: (v[0], list(v[1])) for k, v in tasks.items()}
        s = schedule(tasks, 1)
        self.assertEqual(A(s), [("b", 0, 0, 2), ("a", 0, 2, 3)])
        self.assertEqual({k: (v[0], list(v[1])) for k, v in tasks.items()}, snapshot)

    def test_empty(self):
        s = schedule({}, 3)
        self.assertEqual(s.makespan, 0)
        self.assertEqual(list(s.assignments), [])
        with self.assertRaises(ValueError):
            schedule({}, 0)

    def test_single_task(self):
        s = schedule({"only": (4, [])}, 3)
        self.assertEqual(A(s), [("only", 0, 0, 4)])
        self.assertEqual(s.makespan, 4)

    # ---- validation ----

    def test_bad_workers(self):
        for w in (0, -1, 1.5, True, "2", None):
            with self.assertRaises(ValueError, msg=repr(w)) as cm:
                schedule({"a": (1, [])}, w)
            self.assertNotIsInstance(cm.exception, CycleError)

    def test_bad_durations(self):
        for d in (0, -2, 1.5, 2.0, True, "3", None):
            with self.assertRaises(ValueError, msg=repr(d)) as cm:
                schedule({"a": (d, [])}, 1)
            self.assertNotIsInstance(cm.exception, CycleError)

    def test_unknown_dependency_is_keyerror_in_sorted_task_order(self):
        tasks = {"b": (1, ["zz"]), "a": (1, ["yy", "xx"])}
        with self.assertRaises(KeyError) as cm:
            schedule(tasks, 2)
        self.assertEqual(cm.exception.args[0], "yy")
        with self.assertRaises(KeyError) as cm:
            schedule({"b": (1, ["a", "q", "p"]), "a": (1, [])}, 2)
        self.assertEqual(cm.exception.args[0], "q")

    def test_validation_order(self):
        # workers before everything
        with self.assertRaises(ValueError) as cm:
            schedule({"a": (1, ["nope"])}, 0)
        self.assertNotIsInstance(cm.exception, (CycleError, KeyError))
        # durations before unknown dependencies, even if the bad duration sorts later
        with self.assertRaises(ValueError) as cm:
            schedule({"a": (1, ["nope"]), "z": (0, [])}, 1)
        self.assertNotIsInstance(cm.exception, (CycleError, KeyError))
        # unknown dependencies before cycles
        with self.assertRaises(KeyError) as cm:
            schedule({"a": (1, ["b"]), "b": (1, ["a"]), "c": (1, ["ghost"])}, 1)
        self.assertEqual(cm.exception.args[0], "ghost")
        # durations before cycles
        with self.assertRaises(ValueError) as cm:
            schedule({"a": (1, ["a"]), "b": (-1, [])}, 1)
        self.assertNotIsInstance(cm.exception, CycleError)

    # ---- cycles ----

    def cycle_of(self, tasks):
        with self.assertRaises(CycleError) as cm:
            schedule(tasks, 2)
        self.assertIsInstance(cm.exception, ValueError)
        return cm.exception.cycle

    def test_self_dependency(self):
        self.assertEqual(self.cycle_of({"x": (1, ["x"])}), ["x"])
        self.assertEqual(self.cycle_of({"a": (1, []), "x": (1, ["a", "x"])}), ["x"])

    def test_two_cycle(self):
        self.assertEqual(self.cycle_of({"b": (1, ["a"]), "a": (1, ["b"])}), ["a", "b"])

    def test_three_cycle_follows_dependency_direction(self):
        # a depends on c, c on b, b on a
        tasks = {"a": (1, ["c"]), "b": (1, ["a"]), "c": (1, ["b"])}
        self.assertEqual(self.cycle_of(tasks), ["a", "c", "b"])

    def test_fewest_steps_back_beats_smaller_name(self):
        # from m: n is smaller than o, but o leads back in one step, n needs two
        tasks = {"m": (1, ["n", "o"]), "n": (1, ["o"]), "o": (1, ["m"])}
        self.assertEqual(self.cycle_of(tasks), ["m", "o"])

    def test_equal_steps_back_broken_by_name(self):
        tasks = {"m": (1, ["q", "p"]), "p": (1, ["m"]), "q": (1, ["m"])}
        self.assertEqual(self.cycle_of(tasks), ["m", "p"])

    def test_smallest_name_on_a_cycle_not_smallest_name_overall(self):
        tasks = {
            "aa": (1, ["n"]),        # tail hanging off the cycle, smaller name
            "m": (1, ["n"]),
            "n": (1, ["o"]),
            "o": (1, ["m"]),
            "zz": (1, ["m"]),        # depends on the cycle
            "ab": (1, []),
            "ac": (1, ["ab", "zz"]),
        }
        self.assertEqual(self.cycle_of(tasks), ["m", "n", "o"])

    def test_two_separate_cycles_pick_the_one_with_smallest_name(self):
        tasks = {
            "a": (1, ["d"]),
            "d": (1, ["e"]),
            "e": (1, ["d"]),
            "c": (1, ["b"]),
            "b": (1, ["c"]),
            "z": (1, []),
        }
        self.assertEqual(self.cycle_of(tasks), ["b", "c"])

    def test_cycle_in_larger_graph_with_tails(self):
        tasks = {}
        for i in range(10):
            tasks["t%d" % i] = (i + 1, ["t%d" % (i - 1)] if i else [])
        tasks["t4"] = (5, ["t3", "t7"])  # t4 -> t7 -> t6 -> t5 -> t4
        tasks["head"] = (1, ["t9"])
        self.assertEqual(self.cycle_of(tasks), ["t4", "t7", "t6", "t5"])

    # ---- property test ----

    def random_dag(self, rng):
        n = rng.randint(1, 12)
        names = rng.sample(["t%02d" % i for i in range(30)] + ["u", "v", "A", "Z"], n)
        tasks = {}
        for i, name in enumerate(names):
            pool = names[:i]
            deps = [rng.choice(pool) for _ in range(rng.randint(0, 3))] if pool else []
            tasks[name] = (rng.randint(1, 5), deps)
        items = list(tasks.items())
        rng.shuffle(items)
        return dict(items)

    def test_random_dags_against_invariants_and_oracle(self):
        rng = random.Random(7)
        for case in range(100):
            tasks = self.random_dag(rng)
            workers = rng.randint(1, 5)
            s = schedule(tasks, workers)
            asg = A(s)
            ctx = "case %d workers %d tasks %r" % (case, workers, tasks)
            by_task = {}
            for name, w, st, en in asg:
                self.assertNotIn(name, by_task, ctx)
                by_task[name] = (w, st, en)
                self.assertTrue(0 <= w < workers, ctx)
                self.assertEqual(en - st, tasks[name][0], ctx)
            self.assertEqual(set(by_task), set(tasks), ctx)
            for name, (w, st, en) in by_task.items():
                for d in tasks[name][1]:
                    self.assertLessEqual(by_task[d][2], st, ctx)
            for w in range(workers):
                spans = sorted((st, en) for (ww, st, en) in by_task.values() if ww == w)
                for (s1, e1), (s2, e2) in zip(spans, spans[1:]):
                    self.assertLessEqual(e1, s2, ctx)
            self.assertEqual(s.makespan, max(en for (_, _, en) in by_task.values()), ctx)
            self.assertEqual(asg, sorted(asg, key=lambda a: (a[2], a[1])), ctx)
            self.assertEqual(asg, oracle(tasks, workers), ctx)


if __name__ == "__main__":
    unittest.main()
