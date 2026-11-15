"""Injectable clock so rules that depend on 'now' are testable."""

from __future__ import annotations

from datetime import datetime, timedelta

from .timeutil import UTC, require_aware


class SystemClock:
    def now(self) -> datetime:
        return datetime.now(UTC)


class FixedClock:
    def __init__(self, now: datetime):
        self._now = require_aware(now)

    def now(self) -> datetime:
        return self._now

    def advance(self, **kwargs) -> None:
        self._now = self._now + timedelta(**kwargs)
