"""Proves the shop-demo benchmark tasks are fair.

For every task that has a reference solution under examples/reference/<task>/ it checks that
  1. the untouched fixture passes its visible tests,
  2. the hidden acceptance tests FAIL on the untouched fixture (the task is not already done),
  3. the reference solution keeps the visible tests passing,
  4. the reference solution PASSES the hidden acceptance tests (the task is achievable from its prompt alone).

Run:  python examples/verify_tasks.py            (all tasks)
      python examples/verify_tasks.py ttl-cache  (some tasks)
Everything happens in temporary directories; nothing in the repository is modified.
"""

import re
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent
FIXTURE = ROOT / "shop-demo"
ACCEPTANCE = ROOT / "acceptance"
REFERENCE = ROOT / "reference"
TASKSET = ROOT / "tasks-hard.yaml"


def run_tests(cwd: Path) -> tuple[bool, str]:
    proc = subprocess.run(
        [sys.executable, "-m", "unittest", "discover", "-s", "tests", "-t", "."],
        cwd=cwd, capture_output=True, text=True, timeout=300,
    )
    return proc.returncode == 0, (proc.stdout + proc.stderr).strip().splitlines()[-1] if (proc.stdout + proc.stderr).strip() else ""


def fresh_copy(parent: Path, name: str) -> Path:
    dest = parent / name
    shutil.copytree(FIXTURE, dest, ignore=shutil.ignore_patterns("__pycache__", "*.pyc"))
    return dest


def overlay(source: Path, dest: Path) -> None:
    shutil.copytree(source, dest, dirs_exist_ok=True)


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
            failures.append(f"{task}: has a reference solution but is not declared in tasks-hard.yaml")
        if not (ACCEPTANCE / task).is_dir():
            failures.append(f"{task}: missing acceptance directory")
    for task in sorted(declared - set(tasks) if not argv else []):
        failures.append(f"{task}: declared in tasks-hard.yaml but has no reference solution")

    with tempfile.TemporaryDirectory(prefix="taskrail-verify-") as tmp_name:
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
            status = "ok  " if all(c for c, _ in checks) else "FAIL"
            print(f"{status} {task}")
            for passed, text in checks:
                print(f"       {'✓' if passed else '✗'} {text}")
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
