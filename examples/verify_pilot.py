"""Proves the roombook pilot tasks (examples/tasks-pilot.yaml) are fair and that their traps bite.

For every task under examples/pilot/reference/<task>/ it checks that
  1. the untouched fixture passes its visible tests,
  2. the hidden acceptance tests FAIL on the untouched fixture (the task is not already done),
  3. the reference solution keeps the visible tests passing,
  4. the reference solution PASSES the hidden acceptance tests,
and, for each mutation below (a plausible but wrong solution made by editing the reference),
  5. the hidden acceptance tests FAIL on the mutated reference (the trap the task is built around is really tested).

Run:  python examples/verify_pilot.py                 (all tasks)
      python examples/verify_pilot.py csv-import      (some tasks)
Everything happens in temporary directories; nothing in the repository is modified.
"""

import re
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent
FIXTURE = ROOT / "roombook"
ACCEPTANCE = ROOT / "pilot" / "acceptance"
REFERENCE = ROOT / "pilot" / "reference"
TASKSET = ROOT / "tasks-pilot.yaml"

# (task, name, file inside the reference overlay, text to replace, replacement)
MUTATIONS = [
    ("back-to-back", "fixes the conflict check but not the shared helper (free slots stay wrong)",
     "roombook/timeutil.py", "return a_start < b_end and b_start < a_end", "return a_start <= b_end and b_start <= a_end"),
    ("csv-import", "reads the file as plain UTF-8 (the byte order mark breaks the header)",
     "roombook/importer.py", 'encoding="utf-8-sig"', 'encoding="utf-8"'),
    ("csv-import", "treats the times as UTC instead of room-local",
     "roombook/importer.py", "start = from_room_local(_wall_clock(values[\"start\"]), room)",
     "start = _wall_clock(values[\"start\"]).replace(tzinfo=__import__('datetime').timezone.utc)"),
    ("csv-import", "keeps the valid rows of a failed all-or-nothing import",
     "roombook/importer.py", "            if result.errors and not partial:\n                raise _Rollback()\n", ""),
    ("daily-quota", "counts cancelled bookings (for_user returns every booking)",
     "roombook/service.py", "if b.is_active and day_start <= b.start < day_end", "if day_start <= b.start < day_end"),
    ("daily-quota", "uses the UTC calendar day instead of the room-local day",
     "roombook/service.py", "day = to_room_local(start, room).date()\n        day_start, day_end = local_day_bounds(day, room)",
     "day_start = start.replace(hour=0, minute=0)\n        day_end = day_start + __import__('datetime').timedelta(days=1)"),
    ("maintenance", "forgets free slots",
     "roombook/service.py", "        taken += [w for w in self.maintenance.for_room(room.id) if w.end > opens and w.start < closes]\n", ""),
    ("maintenance", "lets members schedule maintenance",
     "roombook/service.py", "        if not actor.is_admin:\n            raise PermissionDeniedError(\"only admins can schedule maintenance\")\n", ""),
    ("cancel-permissions", "admins cancel silently (owner not told who did it)",
     "roombook/service.py", "by = actor.name if actor.id != booking.user_id else None", "by = None"),
    ("cancel-permissions", "checks the owner but forgets admins",
     "roombook/service.py", "if booking.user_id != actor.id and not actor.is_admin:", "if booking.user_id != actor.id:"),
]


def run_tests(cwd: Path) -> tuple[bool, str]:
    proc = subprocess.run(
        [sys.executable, "-m", "unittest", "discover", "-s", "tests", "-t", "."],
        cwd=cwd, capture_output=True, text=True, timeout=300,
    )
    out = (proc.stdout + proc.stderr).strip()
    return proc.returncode == 0, out.splitlines()[-1] if out else ""


def fresh_copy(parent: Path, name: str) -> Path:
    dest = parent / name
    shutil.copytree(FIXTURE, dest, ignore=shutil.ignore_patterns("__pycache__", "*.pyc", "*.db"))
    return dest


def overlay(source: Path, dest: Path) -> None:
    shutil.copytree(source, dest, dirs_exist_ok=True)


def mutate(tree: Path, relative: str, old: str, new: str) -> bool:
    path = tree / relative
    text = path.read_text(encoding="utf-8")
    if old not in text:
        return False
    path.write_text(text.replace(old, new, 1), encoding="utf-8", newline="")
    return True


def main(argv: list[str]) -> int:
    tasks = sorted(p.name for p in REFERENCE.iterdir() if p.is_dir())
    if argv:
        unknown = [t for t in argv if t not in tasks]
        if unknown:
            print(f"unknown task(s): {', '.join(unknown)}; known: {', '.join(tasks)}")
            return 2
        tasks = argv

    failures = []
    declared = set(re.findall(r"^\s*- id: (\S+)", TASKSET.read_text(encoding="utf-8"), re.M))
    for task in tasks:
        if task not in declared:
            failures.append(f"{task}: has a reference solution but is not declared in {TASKSET.name}")
        if not (ACCEPTANCE / task).is_dir():
            failures.append(f"{task}: missing acceptance directory")
    for task in sorted(declared - set(tasks) if not argv else []):
        failures.append(f"{task}: declared in {TASKSET.name} but has no reference solution")

    with tempfile.TemporaryDirectory(prefix="tokonomy-verify-pilot-") as tmp_name:
        tmp = Path(tmp_name)
        ok, last = run_tests(fresh_copy(tmp, "base"))
        print(f"{'ok  ' if ok else 'FAIL'} fixture visible tests pass on the untouched fixture ({last})")
        if not ok:
            failures.append("fixture: visible tests fail on the untouched fixture")

        for task in tasks:
            if not (ACCEPTANCE / task).is_dir() or not (REFERENCE / task).is_dir():
                continue

            base = fresh_copy(tmp, f"{task}-base")
            overlay(ACCEPTANCE / task, base)
            hidden_on_base, _ = run_tests(base)

            ref = fresh_copy(tmp, f"{task}-ref")
            overlay(REFERENCE / task, ref)
            visible_on_ref, visible_note = run_tests(ref)
            overlay(ACCEPTANCE / task, ref)
            hidden_on_ref, hidden_note = run_tests(ref)

            checks = [
                (not hidden_on_base, "hidden tests fail on the base"),
                (visible_on_ref, f"visible tests still pass with the reference ({visible_note})"),
                (hidden_on_ref, f"hidden tests pass with the reference ({hidden_note})"),
            ]
            for index, (mtask, name, relative, old, new) in enumerate(m for m in MUTATIONS if m[0] == task):
                wrong = fresh_copy(tmp, f"{task}-wrong{index}")
                overlay(REFERENCE / task, wrong)
                applied = mutate(wrong, relative, old, new)
                overlay(ACCEPTANCE / task, wrong)
                hidden_on_wrong, _ = run_tests(wrong) if applied else (True, "")
                checks.append((applied and not hidden_on_wrong,
                               f"wrong solution is caught: {name}" + ("" if applied else " (MUTATION DID NOT APPLY)")))

            status = "ok  " if all(c for c, _ in checks) else "FAIL"
            print(f"{status} {task}")
            for passed, text in checks:
                print(f"       {'+' if passed else 'x'} {text}")
                if not passed:
                    failures.append(f"{task}: {text}")

    print()
    if failures:
        print("PROBLEMS:")
        for failure in failures:
            print(f"  - {failure}")
        return 1
    print(f"all {len(tasks)} task(s) verified")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
