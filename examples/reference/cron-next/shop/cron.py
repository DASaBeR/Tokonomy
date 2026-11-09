"""Cron expression evaluation (reference implementation)."""

import datetime

_MONTHS = {n: i + 1 for i, n in enumerate(
    "jan feb mar apr may jun jul aug sep oct nov dec".split())}
_DAYS = {n: i for i, n in enumerate("sun mon tue wed thu fri sat".split())}

# name, lo, hi, star_hi, names
_FIELDS = [
    ("minute", 0, 59, 59, None),
    ("hour", 0, 23, 23, None),
    ("day-of-month", 1, 31, 31, None),
    ("month", 1, 12, 12, _MONTHS),
    ("day-of-week", 0, 7, 6, _DAYS),
]


def _num(text, lo, hi, names, what):
    if text.isascii() and text.isdigit():
        v = int(text)
    elif names is not None and text.lower() in names:
        v = names[text.lower()]
    else:
        raise ValueError("bad %s value %r" % (what, text))
    if not lo <= v <= hi:
        raise ValueError("%s value %r out of range" % (what, text))
    return v


def _parse_field(text, spec):
    what, lo, hi, star_hi, names = spec
    values = set()
    for item in text.split(","):
        if item == "":
            raise ValueError("empty list item in %s" % what)
        parts = item.split("/")
        if len(parts) > 2:
            raise ValueError("bad term %r" % item)
        base = parts[0]
        step = None
        if len(parts) == 2:
            s = parts[1]
            if not (s.isascii() and s.isdigit()) or int(s) < 1:
                raise ValueError("bad step %r" % s)
            step = int(s)
        if base == "*":
            a, b = lo, star_hi
        elif "-" in base:
            ends = base.split("-")
            if len(ends) != 2:
                raise ValueError("bad range %r" % base)
            a = _num(ends[0], lo, hi, names, what)
            b = _num(ends[1], lo, hi, names, what)
            if a > b:
                raise ValueError("reversed range %r" % base)
        else:
            a = _num(base, lo, hi, names, what)
            b = hi if step is not None else a
        values.update(range(a, b + 1, step or 1))
    if what == "day-of-week" and 7 in values:
        values.discard(7)
        values.add(0)
    return values


def _parse(expr):
    if not isinstance(expr, str):
        raise ValueError("expression must be a string")
    parts = expr.split()
    if len(parts) != 5:
        raise ValueError("expected 5 fields, got %d" % len(parts))
    sets = [_parse_field(p, s) for p, s in zip(parts, _FIELDS)]
    return sets, parts[2] != "*", parts[4] != "*"


def next_run(expr, after):
    (minutes, hours, doms, months, dows), dom_r, dow_r = _parse(expr)
    if after.tzinfo is not None:
        raise ValueError("timezone-aware datetime not supported")
    start = after.replace(second=0, microsecond=0) + datetime.timedelta(minutes=1)
    last_year = after.year + 8
    day = start.date()
    first = True
    one = datetime.timedelta(days=1)
    while day.year <= last_year:
        if day.month in months:
            dom_ok = day.day in doms
            dow_ok = (day.weekday() + 1) % 7 in dows
            if dom_r and dow_r:
                ok = dom_ok or dow_ok
            elif dom_r:
                ok = dom_ok
            elif dow_r:
                ok = dow_ok
            else:
                ok = True
            if ok:
                floor = (start.hour, start.minute) if first else (0, 0)
                for h in sorted(hours):
                    for m in sorted(minutes):
                        if (h, m) >= floor:
                            return datetime.datetime(day.year, day.month, day.day, h, m)
        day += one
        first = False
    raise ValueError("expression never matches")
