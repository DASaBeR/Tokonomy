"""Deterministic list scheduler for a task DAG."""

from collections import deque
from dataclasses import dataclass, field


class CycleError(ValueError):
    def __init__(self, cycle):
        super().__init__("dependency cycle: " + " -> ".join(cycle))
        self.cycle = list(cycle)


@dataclass
class Schedule:
    makespan: int
    assignments: list = field(default_factory=list)


def _is_int(v):
    return isinstance(v, int) and not isinstance(v, bool)


def _canonical_cycle(deps):
    # nodes on a cycle: those that can reach themselves via >= 1 step
    def dist_to(target):
        # fewest dependency steps from every node to target (reverse BFS)
        rev = {}
        for a, ds in deps.items():
            for d in ds:
                rev.setdefault(d, []).append(a)
        dist = {target: 0}
        q = deque([target])
        while q:
            x = q.popleft()
            for p in rev.get(x, []):
                if p not in dist:
                    dist[p] = dist[x] + 1
                    q.append(p)
        return dist

    on_cycle = []
    for n in sorted(deps):
        dist = dist_to(n)
        if any(d in dist for d in deps[n]):
            on_cycle.append(n)
            break
    if not on_cycle:
        return None
    n = on_cycle[0]
    dist = dist_to(n)
    cycle = [n]
    cur = n
    while True:
        nxt = min((d for d in deps[cur] if d in dist), key=lambda d: (dist[d], d))
        if nxt == n:
            return cycle
        cycle.append(nxt)
        cur = nxt


def schedule(tasks, workers):
    if not _is_int(workers) or workers < 1:
        raise ValueError("workers must be an int >= 1")
    for name in sorted(tasks):
        dur = tasks[name][0]
        if not _is_int(dur) or dur < 1:
            raise ValueError("bad duration for %r" % (name,))
    deps = {}
    for name in sorted(tasks):
        raw = tasks[name][1]
        for d in raw:
            if d not in tasks:
                raise KeyError(d)
        deps[name] = sorted(set(raw))
    cyc = _canonical_cycle(deps)
    if cyc is not None:
        raise CycleError(cyc)
    if not tasks:
        return Schedule(0, [])

    dur = {n: tasks[n][0] for n in tasks}
    dependents = {n: [] for n in tasks}
    for n, ds in deps.items():
        for d in ds:
            dependents[d].append(n)
    cp = {}

    # fill in reverse topological order (iterative DFS)
    topo, seen = [], set()
    for root in sorted(tasks):
        if root in seen:
            continue
        stack = [(root, iter(deps[root]))]
        seen.add(root)
        while stack:
            node, it = stack[-1]
            for d in it:
                if d not in seen:
                    seen.add(d)
                    stack.append((d, iter(deps[d])))
                    break
            else:
                topo.append(node)
                stack.pop()
    for n in reversed(topo):
        cp[n] = dur[n] + max((cp[m] for m in dependents[n]), default=0)

    prio = sorted(tasks, key=lambda n: (-cp[n], -dur[n], n))
    end = {}
    free_at = [0] * workers
    started = set()
    out = []
    t = 0
    while len(started) < len(tasks):
        ready = [n for n in prio if n not in started and all(d in end and end[d] <= t for d in deps[n])]
        idle = [w for w in range(workers) if free_at[w] <= t]
        for n, w in zip(ready, idle):
            started.add(n)
            end[n] = t + dur[n]
            free_at[w] = end[n]
            out.append((n, w, t, end[n]))
        if len(started) < len(tasks):
            t = min(e for e in end.values() if e > t)
    out.sort(key=lambda a: (a[2], a[1]))
    return Schedule(max(a[3] for a in out), out)
