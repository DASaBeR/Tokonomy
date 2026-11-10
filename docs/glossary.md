# Glossary

The words Tokonomy uses, in the order you meet them.

**Strong model / cheap model.** The two roles. The strong model (also called the *architect*) plans the work, rescues steps the cheap model cannot do, reviews, and can write the tests. The cheap model (the *worker*) writes the code. Each can come from a different provider; see [config.md](config.md#connections-any-model-from-any-provider).

**Plan, unit.** The strong model turns your request into a *plan*: a short list of *units*, each a small, checkable piece of work (for a landing page: the HTML, then the CSS, then the JavaScript), with the order they depend on each other.

**Worktree, run branch.** Each run works in its own git worktree on its own branch (`tokonomy/<run-id>`), one commit per unit. Your files are untouched until you merge.

**Gate.** An automatic pass/fail check that Tokonomy runs on the worker's output before it accepts a unit. A gate is an ordinary command: a build, a linter, a type check, your test suite. Because a command decides, the model's own claim that it finished counts for nothing. A unit is accepted only when every gate passes. Gates are listed under `gates:` in `.tokonomy/policy.yaml` and are detected for your stack by `tokonomy init`.

**Acceptance tests.** Gates for projects with no test suite of their own (a landing page, a script). The strong model writes tests for each unit while it plans; they are committed before the worker starts, the worker cannot change them (anything it does to them is undone before they run), and a unit is accepted only when they pass. See [config.md](config.md#acceptance-tests-written-by-the-architect). (In the benchmark, *hidden acceptance tests* are a different thing: tests the worker never sees that decide whether a trial succeeded.)

**Baseline.** Before any worker runs, Tokonomy runs every gate once on your starting commit. A gate then blocks a unit only for failures that are *new* compared with that baseline, so a test that was already broken does not stop the work.

**Retry.** When a gate fails, the worker runs again with the gate's output as feedback.

**Takeover.** When retries run out (or the worker times out or makes no changes), the strong model fixes the unit itself with exact search-and-replace edits, then hands back to a fresh worker for the next unit. Takeovers are recorded and the commit is labelled `[architect takeover]`.

**Locked region.** Text the strong model wrote during a takeover. A built-in gate checks it is still in place, so a later worker cannot undo the fix.

**Gate isolation (sandbox).** Gates run code the worker wrote, so by default they run in a disposable Windows Sandbox VM with a copy of your source, no network and none of your files. See [gate-isolation.md](gate-isolation.md).

**Strategy.** A way of getting a task done, used mainly by the benchmark: `1` strong model alone, `1b` strong model with gates and retries, `2` cheap model alone, `3` plan plus cheap worker without gates, `4` plan plus cheap worker plus gates and supervision (what `tokonomy build` uses). See [benchmarking.md](benchmarking.md).

**Ledger.** The local database (`.tokonomy/ledger.db`) that records every model call, its tokens and cost, every attempt, and every gate result. `tokonomy report` reads it.
