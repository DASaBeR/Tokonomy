"""Domain objects. Datetimes are always timezone-aware; stored and compared in UTC."""

from __future__ import annotations

from dataclasses import dataclass, field, replace
from datetime import datetime
from enum import Enum


class Role(str, Enum):
    MEMBER = "member"
    ADMIN = "admin"


class BookingStatus(str, Enum):
    ACTIVE = "active"
    CANCELLED = "cancelled"


@dataclass(frozen=True)
class User:
    id: str
    name: str
    role: Role = Role.MEMBER

    @property
    def is_admin(self) -> bool:
        return self.role is Role.ADMIN


@dataclass(frozen=True)
class Room:
    id: str
    name: str
    capacity: int
    # Rooms live in different offices. Opening hours and calendar days are evaluated in the room's local time.
    utc_offset_minutes: int = 0
    features: tuple[str, ...] = field(default_factory=tuple)


@dataclass(frozen=True)
class Booking:
    id: int | None
    room_id: str
    user_id: str
    start: datetime
    end: datetime
    title: str = ""
    attendees: int = 1
    status: BookingStatus = BookingStatus.ACTIVE
    created_at: datetime | None = None

    @property
    def duration_minutes(self) -> int:
        return int((self.end - self.start).total_seconds() // 60)

    @property
    def is_active(self) -> bool:
        return self.status is BookingStatus.ACTIVE

    def with_status(self, status: BookingStatus) -> "Booking":
        return replace(self, status=status)
