"""Minimal line diff with deterministic tie-breaking and unified output."""


def edit_script(a, b):
    n, m = len(a), len(b)
    L = [[0] * (m + 1) for _ in range(n + 1)]
    for i in range(n - 1, -1, -1):
        row, nxt, ai = L[i], L[i + 1], a[i]
        for j in range(m - 1, -1, -1):
            if ai == b[j]:
                row[j] = nxt[j + 1] + 1
            else:
                x, y = nxt[j], row[j + 1]
                row[j] = x if x >= y else y
    out = []
    i = j = 0
    while i < n or j < m:
        if i < n and j < m and a[i] == b[j]:
            out.append(("=", a[i]))
            i += 1
            j += 1
        elif i < n and (j == m or L[i + 1][j] >= L[i][j + 1]):
            out.append(("-", a[i]))
            i += 1
        else:
            out.append(("+", b[j]))
            j += 1
    return out


def apply_script(a, script):
    result = []
    i = 0
    for op, line in script:
        if op == "+":
            result.append(line)
        elif op in ("=", "-"):
            if i >= len(a) or a[i] != line:
                raise ValueError("script does not match input")
            i += 1
            if op == "=":
                result.append(line)
        else:
            raise ValueError("unknown operation %r" % (op,))
    if i != len(a):
        raise ValueError("script does not consume all input")
    return result


def _range(start0, count):
    if count == 0:
        return "%d,0" % start0
    if count == 1:
        return "%d" % (start0 + 1)
    return "%d,%d" % (start0 + 1, count)


def unified(a, b, context=3, name_a="a", name_b="b"):
    script = edit_script(a, b)
    if all(op == "=" for op, _ in script):
        return ""
    changed = [k for k, (op, _) in enumerate(script) if op != "="]
    groups = []
    for k in changed:
        if groups and k - groups[-1][1] - 1 <= 2 * context:
            groups[-1][1] = k
        else:
            groups.append([k, k])
    # a_pos[k] / b_pos[k]: lines of a / b consumed before script index k
    a_pos, b_pos = [0], [0]
    for op, _ in script:
        a_pos.append(a_pos[-1] + (op != "+"))
        b_pos.append(b_pos[-1] + (op != "-"))
    out = ["--- %s" % name_a, "+++ %s" % name_b]
    for first, last in groups:
        lo = max(0, first - context)
        hi = min(len(script), last + 1 + context)
        body = script[lo:hi]
        ca = sum(1 for op, _ in body if op != "+")
        cb = sum(1 for op, _ in body if op != "-")
        out.append("@@ -%s +%s @@" % (_range(a_pos[lo], ca), _range(b_pos[lo], cb)))
        for op, line in body:
            out.append((" " if op == "=" else op) + line)
    return "\n".join(out) + "\n"
