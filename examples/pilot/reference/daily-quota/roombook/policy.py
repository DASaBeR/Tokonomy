"""Booking rules that do not depend on other bookings. Rules that need existing bookings live in the service."""

from __future__ import annotations

from dataclasses import dataclass
from datetime import datetime, timedelta

from .errors import PolicyError
from .timeutil import minutes_between, to_room_local


@dataclass(frozen=True)
class BookingPolicy:
    min_minutes: int = 15
    max_minutes: int = 240
    granularity_minutes: int = 15
    open_hour: int = 7    # room-local
    close_hour: int = 21  # room-local, exclusive
    max_days_ahead: int = 60
    daily_quota_minutes: int | None = 240  # per member and room-local calendar day; None disables

    def validate(self, room, start: datetime, end: datetime, now: datetime, user) -> None:
        minutes = minutes_between(start, end)
        if minutes < self.min_minutes:
            raise PolicyError("too_short", f"bookings must last at least {self.min_minutes} minutes")
        if minutes > self.max_minutes and not user.is_admin:
            raise PolicyError("too_long", f"bookings may last at most {self.max_minutes} minutes")
        local_start = to_room_local(start, room)
        local_end = to_room_local(end, room)
        if local_start.minute % self.granularity_minutes or local_end.minute % self.granularity_minutes \
                or local_start.second or local_end.second:
            raise PolicyError("granularity", f"bookings start and end on {self.granularity_minutes}-minute marks")
        if start < now:
            raise PolicyError("in_past", "bookings cannot start in the past")
        if start > now + timedelta(days=self.max_days_ahead) and not user.is_admin:
            raise PolicyError("too_far_ahead", f"bookings may be made at most {self.max_days_ahead} days ahead")
        opens = local_start.replace(hour=self.open_hour, minute=0)
        closes = local_start.replace(hour=self.close_hour, minute=0)
        if local_start < opens or local_end > closes:
            raise PolicyError(
                "outside_hours", f"the room is open from {self.open_hour:02d}:00 to {self.close_hour:02d}:00 local time"
            )
