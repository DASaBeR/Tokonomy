"""Shared test setup: an in-memory database with the demo seed and a fixed clock."""

from __future__ import annotations

from datetime import datetime

from roombook.clock import FixedClock
from roombook.seed import load_seed
from roombook.service import BookingService
from roombook.storage import Database
from roombook.timeutil import UTC, from_room_local

# Tuesday 1 December 2026, 06:00 UTC: before every office opens.
NOW = datetime(2026, 12, 1, 6, 0, tzinfo=UTC)


def make_service(now: datetime = NOW, **kwargs) -> BookingService:
    db = Database(":memory:")
    load_seed(db)
    return BookingService(db, clock=FixedClock(now), **kwargs)


def local(service: BookingService, room_id: str, text: str) -> datetime:
    """'2026-12-02 09:00' in the room's local time, as a UTC instant."""
    return from_room_local(datetime.strptime(text, "%Y-%m-%d %H:%M"), service.rooms.get(room_id))
