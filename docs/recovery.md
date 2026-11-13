# Crash recovery: `tokonomy run --resume`

A run can be interrupted at any point: the machine reboots, the terminal closes, Ctrl+C, a provider call hangs past
its limit. `tokonomy run --resume <run-id>` continues it on its own branch. It works for every strategy (4 and 5
included) and never redoes, re-bills or re-applies work that already finished.

```sh
tokonomy report                  # find the run id (status RUNNING or ERROR)
tokonomy run --resume 20261113-101500-a1b2c3
```

## What is saved, and when

| Where | What | Written |
|---|---|---|
| run branch `tokonomy/<run-id>` | one commit per accepted unit (plus a commit with the architect's acceptance tests) | when a unit is accepted |
| `runs/<id>/plan.json` | the original plan | at planning |
| `runs/<id>/plan-state.json` | original plan, current revision, revision history, lessons, unit progress | after every plan change and unit result |
| `runs/<id>/run-state.json` | checkpoint: phase (idle, unit, supervision, review, takeover, decomposition, finished), current unit and its start commit, locked regions, handoffs, intervention and split counters | at every phase change |
| `ledger.db` | every model call and its cost, attempts, gate results, interventions, plan revisions, reviews, worker reports | as they happen |
| `runs/<id>/run.lock` | which process holds the run | for the life of that process |

Both JSON files are written atomically (a temporary file, then a rename), so an interruption leaves the previous good
version, never a half-written one. The plan state is written **before** its ledger row, so after a crash the ledger can
only lag behind, and resume records what is missing.

## What resume does

1. **Checks the run is resumable.** Status `RUNNING` (killed) or `ERROR` (Ctrl+C, an unexpected error), or
   `ABORTED (budget)` once the budget was raised. `PASS` has nothing to resume, `FAIL` is a verdict (a unit failed its
   gates or its escalation; use `run --plan <id> --again`), `PLANNED` never started.
2. **Takes the run lock.** If another process still holds it, resume stops: a live run is never entered twice. The
   operating system releases the lock when a process dies.
3. **Re-attaches the branch.** A worktree the crashed run left behind is never reused. Whatever it holds that was not
   committed is unverified work of the interrupted unit: it is saved as a patch under `runs/<id>/recovery/` (for you to
   inspect) and the worktree is removed safely. A fresh worktree is created on the run branch at its tip.
4. **Reconciles the saved state with the evidence.** The branch decides what is complete, not a saved flag:
   - a unit committed on the branch is accepted, even if the crash came before its state was saved;
   - a unit saved as accepted without its commit runs again;
   - an interrupted unit restarts from its last verified commit with the latest plan revision;
   - plan revisions saved but missing from the ledger are recorded; ledger rows for a revision that never reached the
     plan state are kept for audit and not applied.
5. **Re-runs prepare and the baseline on the base commit** (exactly as the original run did), then **re-checks the
   accepted units**: every gate, the acceptance tests and the locked-region check run on the branch tip. If they no
   longer pass, resume stops: the branch or the environment changed and building on it would hide that.
6. **Continues** with the remaining units. Per-unit history comes from the ledger: attempt numbers continue, and the
   takeover and intervention caps count what already happened, so a takeover is never re-applied and an intervention
   that completed is never asked again. Only the interrupted attempt's retry window starts fresh, because that attempt
   was never judged. The budget continues from what the ledger says was spent.

## Interruption at each point

| Interrupted during | After resume |
|---|---|
| a worker attempt or its gates | the unit restarts from its last commit; the attempt's changes are saved as a patch, not reused |
| a plan revision | the revision is either fully in `plan-state.json` (kept, and recorded in the ledger if missing) or not at all (lost; the unit restarts) |
| an architect intervention | a completed plan revision from it is kept; edits it applied but did not commit are discarded; the intervention is asked again only if it was never recorded |
| a takeover or correction | committed: the unit is accepted and the locks are restored; not committed: discarded, and the takeover cap counts any recorded attempt |
| a review | the unit was not committed, so it runs and is reviewed again |
| the commit of an accepted unit | the commit exists: accepted; it does not: the unit runs again |

## When resume refuses

Resume stops with an explanation, and changes nothing, when guessing could produce wrong work:

- `plan-state.json` is unreadable, belongs to another plan, or has an inconsistent revision history;
- units are committed but `plan-state.json` is missing;
- the branch has a commit Tokonomy did not make, a unit committed twice, a unit that is not in the plan, or a unit
  committed before one it depends on;
- `run-state.json` is missing or unreadable after an architect takeover (its locked regions cannot be restored);
- a unit already failed (a verdict, not an interruption);
- the accepted units no longer pass their gates on the branch tip.

To recover by hand: inspect the branch (`git log tokonomy/<run-id>`) and the patches in `runs/<id>/recovery/`, then
either merge what is good or start again with `tokonomy run --plan <id> --again`.

## Limitations

- The policy and executors used on resume are the current ones in `.tokonomy/`; changing them between the crash and
  the resume changes how the rest of the run behaves (the accepted units are re-checked against the current gates).
- Calls metered by Tokonomy's proxy are recorded as they happen and stay in the ledger and the budget. Usage a
  subscription agent (Claude Code, Antigravity, Codex) reports only at the end of an attempt is lost when that attempt is
  interrupted, so its estimated cost is under-counted. The interrupted attempt is not recorded as an attempt.
- Resume reads the per-repository worktree location; if `isolation.worktree_root` changed since, the old worktree is
  not found and is left on disk (`tokonomy doctor` lists leftover worktrees).
