"""Time helpers. Every instant inside roombook is an aware datetime in UTC; room-local time is only for display,
opening hours and calendar days."""

from __future__ import annotations

from datetime import date, datetime, time, timedelta, timezone

from .errors import ValidationError

UTC = timezone.utc


def require_aware(dt: datetime, what: str = "time") -> datetime:
    if dt.tzinfo is None or dt.tzinfo.utcoffset(dt) is None:
        raise ValidationError(f"{what} must include a UTC offset")
    return dt.astimezone(UTC)


def parse_instant(text: str) -> datetime:
    """Parses an ISO 8601 instant with an offset ('2026-12-01T09:00:00+01:00' or '...Z') into UTC."""
    try:
        dt = datetime.fromisoformat(text.strip())
    except ValueError as exc:
        raise ValidationError(f"not an ISO 8601 time: {text!r}") from exc
    return require_aware(dt)


def room_tz(room) -> timezone:
    return timezone(timedelta(minutes=room.utc_offset_minutes))


def to_room_local(dt: datetime, room) -> datetime:
    return require_aware(dt).astimezone(room_tz(room))


def from_room_local(naive: datetime, room) -> datetime:
    """Interprets a wall-clock time (no offset) in the room's local time and returns the UTC instant."""
    if naive.tzinfo is not None:
        raise ValidationError("expected a wall-clock time without an offset")
    return naive.replace(tzinfo=room_tz(room)).astimezone(UTC)


def local_day_bounds(day: date, room) -> tuple[datetime, datetime]:
    """The UTC instants at which the room's local calendar day starts and ends."""
    start = from_room_local(datetime.combine(day, time.min), room)
    return start, start + timedelta(days=1)


def overlaps(a_start: datetime, a_end: datetime, b_start: datetime, b_end: datetime) -> bool:
    """True when the two half-open intervals [start, end) share time. Back-to-back intervals do not overlap."""
    return a_start < b_end and b_start < a_end


def format_local(dt: datetime, room) -> str:
    return to_room_local(dt, room).strftime("%Y-%m-%d %H:%M")


def minutes_between(start: datetime, end: datetime) -> int:
    return int((end - start).total_seconds() // 60)
