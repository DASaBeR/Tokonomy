"""Duration parsing."""

import re

_PATTERN = re.compile(r"(?:([0-9]+)d)?(?:([0-9]+)h)?(?:([0-9]+)m)?(?:([0-9]+)s)?")
_SECONDS = (86400, 3600, 60, 1)


def parse_duration(text: str) -> int:
    if not isinstance(text, str):
        raise TypeError("duration must be a string")
    stripped = text.strip()
    if not stripped:
        raise ValueError("empty duration")
    match = _PATTERN.fullmatch(stripped)
    if match is None:
        raise ValueError(f"invalid duration: {text!r}")
    return sum(int(part) * unit for part, unit in zip(match.groups(), _SECONDS) if part is not None)
